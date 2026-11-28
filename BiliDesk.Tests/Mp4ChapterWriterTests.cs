using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 下载后的章节写入是**纯字节改写**: 插进 moov/udta 的 chpl 会让 moov 变长,
/// 后面的 mdat 整体后移, stco 里的块偏移必须同步加上增量。这里用最小 MP4
/// 验证 chpl 存在且 stco 被修正, 避免"有章节但文件打不开"。
///
/// ★ 2026-11-28 补了一整组回归: 原先只有"最小合成 MP4"(无 udta、单 mdat、
///   stco 单条目)一个用例, 恰好把两个真 bug 挡在夹具之外 ——
///   ① 写端从 0 解析 moov 子盒(而不是 8), udta 永远找不到, "替换已有 chpl"成死代码,
///      每次写入都追加一个新 udta ⇒ 应用读到旧章节、外部播放器读到新章节;
///   ② stco/co64 的偏移**无条件**加 delta, 指向 moov 之前的数据(双 mdat 文件)
///      也被平移 ⇒ 那个 chunk 解出来是错位数据。
///   两处都靠下面的用例钉住。
/// </summary>
public class Mp4ChapterWriterTests
{
    [Fact]
    public void 写入章节并修正stco偏移()
    {
        var path = Path.Combine(Path.GetTempPath(), "bd-chpl-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2"));
            var stco = Box("stco", new byte[4], U32(1), U32(0)); // 占位, 下面按真实位置重写
            var stbl = Box("stbl", stco);
            var moov = Box("moov", Box("trak", Box("mdia", Box("minf", stbl))));
            var oldMdatStart = ftyp.Length + moov.Length;
            var stcoPosInMoov = IndexOf(moov, "stco");
            WriteU32(moov, stcoPosInMoov + 12, (uint)(oldMdatStart + 8)); // stco 的第一个偏移 = mdat payload
            var mdat = Box("mdat", Encoding.ASCII.GetBytes("0123456789"));
            File.WriteAllBytes(path, ftyp.Concat(moov).Concat(mdat).ToArray());

            var chapters = new List<VideoChapter>
            {
                new() { Content = "片头", FromMs = 0, ToMs = 10_000 },
                new() { Content = "正片", FromMs = 10_000, ToMs = 60_000 }
            };

            Assert.True(Mp4ChapterWriter.TryAdd(path, chapters, out var err), err);
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes);
            Assert.Contains("chpl", text);
            Assert.Contains("片头", Encoding.UTF8.GetString(bytes));

            // 新 moov 变长后, mdat 起点后移; stco 里的偏移也必须指向新的 mdat payload。
            var moovPos = IndexOf(bytes, "moov");
            var moovSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(moovPos - 4, 4));
            var stcoPos = IndexOf(bytes, "stco");
            var newMdatStart = moovPos - 4 + (int)moovSize;
            var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stcoPos + 12, 4));
            Assert.Equal((uint)(newMdatStart + 8), firstOffset);

            // 写进去的章节也要能被同一个读取器读回来(本地缓存播放走的就是这条)。
            Assert.True(Mp4ChapterReader.TryRead(path, out var read, out var readErr), readErr);
            Assert.Equal(2, read.Count);
            Assert.Equal("片头", read[0].Content);
            Assert.Equal(0, read[0].FromMs);
            Assert.Equal("正片", read[1].Content);
            Assert.Equal(10_000, read[1].FromMs);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 多个udta时读取第二个chpl()
    {
        var path = Path.Combine(Path.GetTempPath(), "bd-chpl2-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var chapters = new List<VideoChapter>
            {
                new() { Content = "片头", FromMs = 0, ToMs = 10_000 },
                new() { Content = "正片", FromMs = 10_000, ToMs = 60_000 }
            };
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var moov = Box("moov",
                Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", new byte[4], U32(0)))))),
                Box("udta", Box("meta", new byte[4])),
                Box("udta", Box("chpl", Mp4ChapterWriter.BuildPayload(chapters))));
            File.WriteAllBytes(path, ftyp.Concat(moov).ToArray());

            Assert.True(Mp4ChapterReader.TryRead(path, out var read, out var err), err);
            Assert.Equal(2, read.Count);
            Assert.Equal("片头", read[0].Content);
            Assert.Equal("正片", read[1].Content);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ------------------------------------------------------------ 2026-11-28 回归

    /// <summary>
    /// 已有 chpl 的文件再写一次: 必须在**原来的 udta 里替换**, 不能新建第二个 udta。
    /// 两个 udta ⇒ 应用读端(取第一个含 chpl 的 udta)与外部播放器(取最后一个)读到两套章节。
    /// </summary>
    [Fact]
    public void 已有chpl时替换而不是新建第二个udta()
    {
        var path = TempPath("replace");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var moov = Box("moov",
                Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", new byte[4], U32(0)))))),
                Box("udta", Box("chpl", Mp4ChapterWriter.BuildPayload(Chapters("OLD_1", "OLD_2")))));
            File.WriteAllBytes(path, ftyp.Concat(moov).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("NEW_1", "NEW_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            Assert.Single(MoovChildren(bytes), b => b.Type == "udta");
            Assert.Single(FindAll(bytes, 0, bytes.Length, "chpl"));

            Assert.True(Mp4ChapterReader.TryRead(path, out var read, out var readErr), readErr);
            Assert.Equal(2, read.Count);
            Assert.Equal("NEW_1", read[0].Content);
            Assert.Equal("NEW_2", read[1].Content);
        }
        finally { TryDelete(path); }
    }

    /// <summary>udta 里已有的其它元数据(meta)必须原样保留。</summary>
    [Fact]
    public void 保留udta里的其它元数据()
    {
        var path = TempPath("meta");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var moov = Box("moov",
                Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", new byte[4], U32(0)))))),
                Box("udta", Box("meta", new byte[] { 1, 2, 3, 4 })));
            File.WriteAllBytes(path, ftyp.Concat(moov).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("N_1", "N_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            var udta = MoovChildren(bytes).First(b => b.Type == "udta");
            var inner = Children(bytes, udta.Offset + 8, udta.Size - 8);
            Assert.Contains(inner, b => b.Type == "meta");
            Assert.Contains(inner, b => b.Type == "chpl");
            Assert.Single(MoovChildren(bytes), b => b.Type == "udta");

            var meta = inner.First(b => b.Type == "meta");
            Assert.Equal(new byte[] { 1, 2, 3, 4 },
                bytes.Skip(meta.Offset + 8).Take(4).ToArray());
        }
        finally { TryDelete(path); }
    }

    /// <summary>moov 在文件尾(mdat 在前): mdat 没移动, stco 偏移就**不许**加 delta。</summary>
    [Fact]
    public void mdat在moov之前时偏移不变()
    {
        var path = TempPath("mdat-first");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var mdat = Box("mdat", Encoding.ASCII.GetBytes("0123456789"));
            var mdatPayload = ftyp.Length + 8;
            var stco = Box("stco", new byte[4], U32(1), U32((uint)mdatPayload));
            var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stco)))));
            File.WriteAllBytes(path, ftyp.Concat(mdat).Concat(moov).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("A_1", "A_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new List<uint> { (uint)mdatPayload }, StcoEntries(bytes));
            // mdat 的载荷一个字节都没动
            Assert.Equal("0123456789",
                Encoding.ASCII.GetString(bytes.Skip(mdatPayload).Take(10).ToArray()));
        }
        finally { TryDelete(path); }
    }

    /// <summary>
    /// 双 mdat(mdat1 | moov | mdat2): 指向 mdat1 的 chunk 在 moov 之前, **不许**加 delta;
    /// 指向 mdat2 的 chunk 在 moov 之后, 要跟着 moov 变长整体后移。
    /// </summary>
    [Fact]
    public void 双mdat时moov之前的块偏移不变()
    {
        var path = TempPath("two-mdat");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var mdat1 = Box("mdat", Encoding.ASCII.GetBytes("AAAA"));
            var mdat1Payload = ftyp.Length + 8;
            var mdat2 = Box("mdat", Encoding.ASCII.GetBytes("BBBB"));
            var stco = Box("stco", new byte[4], U32(2), U32(0), U32(0));
            var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stco)))));
            var mdat2Payload = ftyp.Length + mdat1.Length + moov.Length + 8;
            var stcoPos = IndexOf(moov, "stco");
            WriteU32(moov, stcoPos + 12, (uint)mdat1Payload);
            WriteU32(moov, stcoPos + 16, (uint)mdat2Payload);
            File.WriteAllBytes(path, ftyp.Concat(mdat1).Concat(moov).Concat(mdat2).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("B_1", "B_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            var entries = StcoEntries(bytes);
            Assert.Equal(2, entries.Count);
            Assert.Equal((uint)mdat1Payload, entries[0]);   // moov 之前: 没动

            var newMoov = TopLevel(bytes).First(b => b.Type == "moov");
            var delta = newMoov.Size - moov.Length;
            Assert.Equal((uint)(mdat2Payload + delta), entries[1]); // moov 之后: 后移

            // 两个 mdat 的载荷都要还在原来的内容上
            Assert.Equal("AAAA", Encoding.ASCII.GetString(bytes.Skip(mdat1Payload).Take(4).ToArray()));
            Assert.Equal("BBBB",
                Encoding.ASCII.GetString(bytes.Skip((int)entries[1]).Take(4).ToArray()));
        }
        finally { TryDelete(path); }
    }

    /// <summary>co64(64 位块偏移)与 stco 同一条规则。</summary>
    [Fact]
    public void co64也只修正moov之后的偏移()
    {
        var path = TempPath("co64");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var mdat1 = Box("mdat", Encoding.ASCII.GetBytes("AAAA"));
            var mdat1Payload = ftyp.Length + 8;
            var mdat2 = Box("mdat", Encoding.ASCII.GetBytes("BBBB"));
            var co64 = Co64Box(0, 0);
            var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", co64)))));
            var mdat2Payload = ftyp.Length + mdat1.Length + moov.Length + 8;
            var co64Pos = IndexOf(moov, "co64");
            // ★ IndexOf 返回的是**类型串**的位置(盒子头 +4): version/flags 在 +4、
            //   count 在 +8、第 0 条在 +12、第 1 条在 +20。
            BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(co64Pos + 12, 8), (ulong)mdat1Payload);
            BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(co64Pos + 20, 8), (ulong)mdat2Payload);
            File.WriteAllBytes(path, ftyp.Concat(mdat1).Concat(moov).Concat(mdat2).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("C_1", "C_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            var entries = Co64Entries(bytes);
            Assert.Equal(2, entries.Count);
            Assert.Equal((ulong)mdat1Payload, entries[0]);

            var newMoov = TopLevel(bytes).First(b => b.Type == "moov");
            var delta = newMoov.Size - moov.Length;
            Assert.Equal((ulong)(mdat2Payload + delta), entries[1]);
            Assert.Equal("BBBB",
                Encoding.ASCII.GetString(bytes.Skip((int)entries[1]).Take(4).ToArray()));
        }
        finally { TryDelete(path); }
    }

    /// <summary>连续写两次: 第二次必须生效(旧章节不能留在文件里)。</summary>
    [Fact]
    public void 连续写两次以第二次为准()
    {
        var path = TempPath("twice");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var moov = Box("moov",
                Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", new byte[4], U32(0)))))));
            File.WriteAllBytes(path, ftyp.Concat(moov).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("FIRST_1", "FIRST_2"), out var e1), e1);
            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("SECOND_1", "SECOND_2"), out var e2), e2);

            var bytes = File.ReadAllBytes(path);
            // ★ 写两次不许堆出两个 udta / 两个 chpl —— 那正是"应用与外部播放器两套章节"的根因。
            Assert.Single(MoovChildren(bytes), b => b.Type == "udta");
            Assert.Single(FindAll(bytes, 0, bytes.Length, "chpl"));

            Assert.True(Mp4ChapterReader.TryRead(path, out var read, out var readErr), readErr);
            Assert.Equal(2, read.Count);
            Assert.Equal("SECOND_1", read[0].Content);
            Assert.Equal("SECOND_2", read[1].Content);
        }
        finally { TryDelete(path); }
    }

    /// <summary>
    /// 多个 udta(一个放 meta, 一个放 chpl)时: 在**含 chpl 的那个**里替换, 不新建第三个;
    /// meta 那个 udta 也不能动。不变量: 全文件只有一个 chpl。
    /// </summary>
    [Fact]
    public void 多udta时在含chpl的那个里替换()
    {
        var path = TempPath("multi-udta");
        try
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512));
            var moov = Box("moov",
                Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", new byte[4], U32(0)))))),
                Box("udta", Box("meta", new byte[] { 9, 9, 9, 9 })),
                Box("udta", Box("chpl", Mp4ChapterWriter.BuildPayload(Chapters("OLD_1", "OLD_2")))));
            File.WriteAllBytes(path, ftyp.Concat(moov).ToArray());

            Assert.True(Mp4ChapterWriter.TryAdd(path, Chapters("NEW_1", "NEW_2"), out var err), err);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(2, MoovChildren(bytes).Count(b => b.Type == "udta")); // 没多出第三个
            Assert.Single(FindAll(bytes, 0, bytes.Length, "chpl"));

            Assert.True(Mp4ChapterReader.TryRead(path, out var read, out var readErr), readErr);
            Assert.Equal(2, read.Count);
            Assert.Equal("NEW_1", read[0].Content);
            Assert.Equal("NEW_2", read[1].Content);
        }
        finally { TryDelete(path); }
    }

    // ------------------------------------------------------------ 夹具与盒子工具

    private static List<VideoChapter> Chapters(string a, string b) => new()
    {
        new() { Content = a, FromMs = 0, ToMs = 10_000 },
        new() { Content = b, FromMs = 10_000, ToMs = 60_000 }
    };

    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(),
            "bd-chpl-" + tag + "-" + Guid.NewGuid().ToString("N") + ".mp4");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed record BoxInfo(string Type, int Offset, int Size);

    private static bool IsContainer(string type) => type is
        "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "dinf" or "udta";

    private static List<BoxInfo> Children(byte[] data, int start, int length)
    {
        var result = new List<BoxInfo>();
        var end = start + length;
        var p = start;
        while (p + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            var type = Encoding.ASCII.GetString(data, p + 4, 4);
            if (size < 8 || p + size > end) break;
            result.Add(new BoxInfo(type, p, size));
            p += size;
        }
        return result;
    }

    private static List<BoxInfo> TopLevel(byte[] data) => Children(data, 0, data.Length);

    /// <summary>moov 的**直接**子盒(不含 moov 自己)。</summary>
    private static List<BoxInfo> MoovChildren(byte[] data)
    {
        var moov = TopLevel(data).Find(b => b.Type == "moov");
        Assert.NotNull(moov);
        return Children(data, moov!.Offset + 8, moov.Size - 8);
    }

    /// <summary>递归找某类型的所有盒子。</summary>
    private static List<BoxInfo> FindAll(byte[] data, int start, int length, string type)
    {
        var result = new List<BoxInfo>();
        foreach (var b in Children(data, start, length))
        {
            if (b.Type == type) result.Add(b);
            if (IsContainer(b.Type))
                result.AddRange(FindAll(data, b.Offset + 8, b.Size - 8, type));
        }
        return result;
    }

    /// <summary>stco 里的块偏移(version/flags 4 字节 + entry_count 4 字节)。</summary>
    private static List<uint> StcoEntries(byte[] data)
    {
        var stco = FindAll(data, 0, data.Length, "stco").FirstOrDefault();
        if (stco == null) return new List<uint>();
        var p = stco.Offset + 12;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        p += 4;
        var list = new List<uint>();
        for (var i = 0; i < count; i++)
            list.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + i * 4, 4)));
        return list;
    }

    /// <summary>co64 里的块偏移(布局与 stco 相同, 条目是 8 字节)。</summary>
    private static List<ulong> Co64Entries(byte[] data)
    {
        var co64 = FindAll(data, 0, data.Length, "co64").FirstOrDefault();
        if (co64 == null) return new List<ulong>();
        var p = co64.Offset + 12;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        p += 4;
        var list = new List<ulong>();
        for (var i = 0; i < count; i++)
            list.Add(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + i * 8, 8)));
        return list;
    }

    private static byte[] Co64Box(params ulong[] offsets)
    {
        var payload = new byte[8 + offsets.Length * 8];
        WriteU32(payload, 0, 0);
        WriteU32(payload, 4, (uint)offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8 + i * 8, 8), offsets[i]);
        return Box("co64", payload);
    }

    private static int IndexOf(byte[] data, string ascii)
    {
        var needle = Encoding.ASCII.GetBytes(ascii);
        for (var i = 0; i <= data.Length - needle.Length; i++)
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        var result = new byte[8 + body.Length];
        WriteU32(result, 0, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        body.CopyTo(result, 8);
        return result;
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        WriteU32(b, 0, value);
        return b;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
}
