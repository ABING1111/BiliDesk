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
