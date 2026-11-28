using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 把 B 站章节写成 MP4 的 `moov/udta/chpl` 原子(QuickTime/Nero 章节列表)。
///
/// ★ 为什么不用 ffmpeg: 本项目不打包 ffmpeg.exe, libmpv 只用于播放。下载合流
///   本来就是纯 C# 重写 MP4(见 <see cref="DashRemuxer"/>), 章节继续用同一条路,
///   不新增外部依赖。
///
/// 写入步骤:
///   1. 读顶层盒子, 定位 `moov`;
///   2. 在 `moov` 的直接子 `udta` 里追加(或替换)`chpl`;
///   3. 因为 `moov` 变长, 若 `mdat` 位于它后面, 需要把新 `moov` 里的
///      `stco`/`co64` 块偏移统一加上增量;
///   4. 写到临时文件后原子替换。
/// </summary>
public static class Mp4ChapterWriter
{
    private const int MaxChplChapters = 255;
    private const int MaxTitleBytes = 255;
    private const long ChplTicksPerMs = 10_000; // 100ns 单位

    /// <summary>
    /// 尝试写入章节。失败只返回 false + error, 不破坏原文件(临时文件在失败路径删除)。
    /// </summary>
    public static bool TryAdd(string path, IReadOnlyList<VideoChapter> chapters,
        out string? error)
    {
        error = null;
        var list = Normalize(chapters);
        if (list.Count == 0)
        {
            error = "没有可用章节";
            return false;
        }

        // ★ 临时名带唯一后缀: 两个线程同时写同一个目标(如同名视频并发下载)用固定的
        //   ".chpl.tmp" 会互相覆盖 —— 项目 AtomicFile / SessionManager 的同款约定。
        var tmp = path + ".chpl-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var boxes = ReadTopLevel(path);
            var moov = boxes.FirstOrDefault(b => b.Type == "moov");
            if (moov == null)
            {
                error = "MP4 缺少 moov";
                return false;
            }
            if (moov.Size > 64 * 1024 * 1024)
            {
                error = "moov 过大, 放弃写章节";
                return false;
            }
            // ★ moov 在 2GB 之外时读不进 int 寻址的内存: 显式放弃, 而不是让
            //   checked((int)moov.Offset) 抛溢出异常(读端 Mp4ChapterReader 同款处理)。
            if (moov.Offset > int.MaxValue)
            {
                error = "moov 偏移过大, 放弃写章节";
                return false;
            }

            var moovBytes = ReadAt(path, checked((int)moov.Offset), checked((int)moov.Size));
            var newMoov = AddChpl(moovBytes, list, out error);
            if (newMoov == null) return false;

            var delta = newMoov.Length - moov.Size;
            // ★ 只有 moov **之后**的数据才会因为 moov 变长而整体后移; moov 之前的数据一动不动。
            //   所以判据是"偏移是否 >= moov 的结束位置", 而不是"文件里有没有排在 moov 后面的 mdat"
            //   —— 双 mdat 的文件(mdat1|moov|mdat2)里, 指向 mdat1 的 chunk 绝对不能加增量,
            //   否则它会被指到错位置, 解出来是错位数据。
            if (delta != 0)
            {
                if (!PatchChunkOffsets(newMoov, delta, moov.Offset + moov.Size, out error))
                    return false;
            }

            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 256 * 1024, FileOptions.SequentialScan))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, 256 * 1024, false))
            {
                CopyRange(src, dst, 0, moov.Offset);
                dst.Write(newMoov, 0, newMoov.Length);
                src.Position = moov.Offset + moov.Size;
                src.CopyTo(dst, 256 * 1024);
            }

            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(tmp);
            return false;
        }
    }

    /// <summary>供单测: 生成 `chpl` 的 payload(不含 8 字节盒子头)。</summary>
    public static byte[] BuildPayload(IReadOnlyList<VideoChapter> chapters)
    {
        var list = Normalize(chapters);
        using var ms = new MemoryStream();
        ms.WriteByte(1);              // version 1
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // flags
        ms.Write(new byte[4], 0, 4);  // version 1 的 reserved
        ms.WriteByte((byte)list.Count); // count 是 u8, 最多 255
        foreach (var c in list)
        {
            var ticks = checked((ulong)Math.Max(0, c.FromMs) * (ulong)ChplTicksPerMs);
            var raw = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(raw, ticks);
            ms.Write(raw, 0, raw.Length);
            var title = TrimTitle(c.Content);
            ms.WriteByte((byte)title.Length);
            ms.Write(title, 0, title.Length);
        }
        return ms.ToArray();
    }

    private static List<VideoChapter> Normalize(IReadOnlyList<VideoChapter>? chapters)
    {
        if (chapters == null || chapters.Count == 0) return new List<VideoChapter>();
        // chpl 只能按开始时间排序, 同一时间只留第一条; 上限 255 是格式本身的 u8 count。
        return chapters
            .Where(c => c != null && c.FromMs >= 0)
            .OrderBy(c => c.FromMs)
            .GroupBy(c => c.FromMs)
            .Select(g => g.First())
            .Take(MaxChplChapters)
            .ToList();
    }

    private static byte[] TrimTitle(string? title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "" : title.Trim();
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxTitleBytes) return bytes;
        // 不要在 UTF-8 多字节字符中间截断。
        var len = MaxTitleBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;
        return bytes.Take(len).ToArray();
    }

    private static byte[]? AddChpl(byte[] moov, List<VideoChapter> chapters, out string? error)
    {
        error = null;
        var chpl = MakeBox("chpl", BuildPayload(chapters));
        // ★ 从 8 开始: moov 这块 buffer 的 [0..8) 是 **moov 自己的头**(size + "moov")。
        //   从 0 解析的话第一个"子盒"就是 moov 自己, udta 永远找不到 ⇒ "替换已有 chpl"
        //   那条分支成了死代码, 每次写入都在 moov 末尾追加一个新 udta。
        //   读端 Mp4ChapterReader.FindChpl 用的就是 ParseBoxes(moov, 8, moov.Length - 8),
        //   写读两端必须一致(2026-11-28 review 实测: 写端写成了 0, 应用读到旧章节、
        //   ffprobe 读到新章节 —— 同一份文件两套章节)。
        var children = ParseBoxes(moov, 8, moov.Length - 8);
        // 一个 MP4 可以有多个 udta(实测: 一个放 meta, 一个放 chpl)。
        // ★ 优先用**已经含 chpl 的那个**(替换语义), 其次用第一个, 再没有才新建 ——
        //   绝不能新建第二个 udta: 应用读端取第一个含 chpl 的 udta, 外部播放器(mp4 规范)
        //   取最后一个, 两个 udta ⇒ 应用和播放器显示两套章节。
        var udtas = children.FindAll(b => b.Type == "udta");
        var udta = udtas.FirstOrDefault(u => FindChplIn(moov, u) != null) ?? udtas.FirstOrDefault();

        byte[] newUdta;
        if (udta == null)
        {
            newUdta = MakeBox("udta", chpl);
            // 直接追加到 moov 末尾。
            var result = new byte[moov.Length + newUdta.Length];
            Buffer.BlockCopy(moov, 0, result, 0, moov.Length);
            Buffer.BlockCopy(newUdta, 0, result, moov.Length, newUdta.Length);
            WriteU32(result, 0, (uint)result.Length);
            return result;
        }

        var oldChpl = FindChplIn(moov, udta);
        byte[] udtaPayload;
        if (oldChpl != null)
        {
            // 替换已有 chpl, 保留 udta 里其它元数据。
            var before = ReadAt(moov, checked((int)udta.Offset + 8),
                checked((int)(oldChpl.Offset - udta.Offset - 8)));
            var afterOffset = oldChpl.Offset + oldChpl.Size;
            var after = ReadAt(moov, checked((int)afterOffset),
                checked((int)(udta.Offset + udta.Size - afterOffset)));
            udtaPayload = Concat(before, chpl, after);
        }
        else
        {
            // udta 里还没有 chpl: 追加进去, 保留它原有的其它元数据。
            var existing = ReadAt(moov, checked((int)udta.Offset + 8),
                checked((int)udta.Size - 8));
            udtaPayload = Concat(existing, chpl);
        }

        newUdta = MakeBox("udta", udtaPayload);
        var result2 = new byte[moov.Length - udta.Size + newUdta.Length];
        Buffer.BlockCopy(moov, 0, result2, 0, checked((int)udta.Offset));
        Buffer.BlockCopy(newUdta, 0, result2, checked((int)udta.Offset), newUdta.Length);
        Buffer.BlockCopy(moov, checked((int)(udta.Offset + udta.Size)), result2,
            checked((int)(udta.Offset + newUdta.Length)),
            checked((int)(moov.Length - udta.Offset - udta.Size)));
        WriteU32(result2, 0, (uint)result2.Length);
        return result2;
    }

    /// <summary>这个 udta 里有没有 chpl(只看直接子盒)。</summary>
    private static BoxInfo? FindChplIn(byte[] moov, BoxInfo udta)
    {
        var inner = ParseBoxes(moov, checked((int)udta.Offset + 8),
            checked((int)udta.Size - 8));
        return inner.Find(b => b.Type == "chpl");
    }

    /// <summary>
    /// 修正 moov 里的块偏移。
    ///
    /// ★ <paramref name="moovEnd"/> 是 moov 在**文件里**的结束位置(绝对偏移):
    ///   只有指向它之后的数据的 chunk 才会因为 moov 变长而整体后移, 需要加 delta;
    ///   指向它之前的数据(moov 前面的 mdat)一动不动, 加了 delta 就指错位置了。
    /// </summary>
    private static bool PatchChunkOffsets(byte[] moov, long delta, long moovEnd,
        out string? error)
    {
        error = null;
        try
        {
            PatchBoxes(moov, 8, moov.Length, delta, moovEnd);
            return true;
        }
        catch (Exception ex)
        {
            error = "章节写入后修正块偏移失败: " + ex.Message;
            return false;
        }
    }

    private static void PatchBoxes(byte[] data, int start, int end, long delta, long moovEnd)
    {
        foreach (var b in ParseBoxes(data, start, end - start))
        {
            if (b.Type == "stco") PatchStco(data, b, delta, moovEnd);
            else if (b.Type == "co64") PatchCo64(data, b, delta, moovEnd);
            else if (IsContainer(b.Type))
                PatchBoxes(data, checked((int)(b.Offset + 8)),
                    checked((int)(b.Offset + b.Size)), delta, moovEnd);
        }
    }

    private static void PatchStco(byte[] data, BoxInfo box, long delta, long moovEnd)
    {
        var p = checked((int)(box.Offset + 8));
        var count = checked((int)ReadU32(data, p + 4));
        p += 8;
        if (p + count * 4 > box.Offset + box.Size) throw new InvalidDataException("stco 越界");
        for (var i = 0; i < count; i++)
        {
            var old = ReadU32(data, checked((int)(p + i * 4)));
            // ★ 指向 moov 之前的数据: 绝对偏移没有变(moov 变长只把**后面**的东西推后)。
            if (old < moovEnd) continue;
            var value = old + delta;
            if (value < 0 || value > uint.MaxValue)
                throw new InvalidDataException("stco 偏移超界");
            WriteU32(data, checked((int)(p + i * 4)), (uint)value);
        }
    }

    private static void PatchCo64(byte[] data, BoxInfo box, long delta, long moovEnd)
    {
        var p = checked((int)(box.Offset + 8));
        var count = checked((int)ReadU32(data, p + 4));
        p += 8;
        if (p + count * 8 > box.Offset + box.Size) throw new InvalidDataException("co64 越界");
        for (var i = 0; i < count; i++)
        {
            var old = checked((long)ReadU64(data, checked((int)(p + i * 8))));
            // 同 stco: moov 之前的数据不动。
            if (old < moovEnd) continue;
            var value = old + delta;
            if (value < 0) throw new InvalidDataException("co64 偏移为负");
            WriteU64(data, checked((int)(p + i * 8)), (ulong)value);
        }
    }

    private static bool IsContainer(string type) => type is
        "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "dinf" or "udta";

    private static List<BoxInfo> ReadTopLevel(string path)
    {
        var result = new List<BoxInfo>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var len = fs.Length;
        long p = 0;
        while (p + 8 <= len)
        {
            fs.Position = p;
            var h = new byte[8];
            if (fs.Read(h, 0, h.Length) != 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.ASCII.GetString(h, 4, 4);
            var header = 8L;
            if (size == 1)
            {
                var ext = new byte[8];
                if (fs.Read(ext, 0, ext.Length) != 8) break;
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(ext));
                header = 16;
            }
            else if (size == 0)
            {
                size = len - p;
            }
            if (size < header || p + size > len) break;
            result.Add(new BoxInfo(type, p, (long)size));
            p += (long)size;
        }
        return result;
    }

    private static List<BoxInfo> ParseBoxes(byte[] data, int start, int length)
    {
        var result = new List<BoxInfo>();
        var end = start + length;
        var p = start;
        while (p + 8 <= end)
        {
            var size = ReadU32(data, p);
            var type = Encoding.ASCII.GetString(data, p + 4, 4);
            long header = 8;
            if (size == 1)
            {
                if (p + 16 > end) break;
                var large = ReadU64(data, p + 8);
                if (large > int.MaxValue) break;
                size = (uint)large;
                header = 16;
            }
            else if (size == 0)
            {
                size = (uint)(end - p);
            }
            if (size < header || p + size > end) break;
            result.Add(new BoxInfo(type, p, size, (int)header));
            p += (int)size;
        }
        return result;
    }

    private static byte[] ReadAt(string path, int offset, int count)
    {
        var buf = new byte[count];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        fs.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    private static byte[] ReadAt(byte[] data, int offset, int count)
    {
        var buf = new byte[count];
        Buffer.BlockCopy(data, offset, buf, 0, count);
        return buf;
    }

    private static void CopyRange(Stream src, Stream dst, long offset, long count)
    {
        src.Position = offset;
        var buf = new byte[256 * 1024];
        while (count > 0)
        {
            var n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            count -= n;
        }
    }

    private static byte[] MakeBox(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        WriteU32(result, 0, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        Buffer.BlockCopy(payload, 0, result, 8, payload.Length);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var p = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, p, part.Length);
            p += part.Length;
        }
        return result;
    }

    private static uint ReadU32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ulong ReadU64(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    private static void WriteU32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    private static void WriteU64(byte[] data, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record BoxInfo(string Type, long Offset, long Size, int Header = 8);
}
