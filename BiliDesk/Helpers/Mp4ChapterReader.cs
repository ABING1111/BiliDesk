using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 从本地 MP4 的 `moov/udta/chpl` 读取章节。
///
/// 下载器把 B 站章节写进 `<see cref="Mp4ChapterWriter"/>` 的同一位置;
/// 离线播放本地缓存时直接读文件, 不再请求接口(本地文件也没有 bvid/cid)。
/// 没有 chpl 的老缓存返回空列表, 行为与以前一致。
/// </summary>
public static class Mp4ChapterReader
{
    private const long ChplTicksPerMs = 10_000; // 100ns

    public static bool TryRead(string path, out List<VideoChapter> chapters, out string? error)
    {
        chapters = new List<VideoChapter>();
        error = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var boxes = ReadTopLevel(path);
            var moov = boxes.Find(b => b.Type == "moov");
            if (moov == null) return false;
            if (moov.Size > 64 * 1024 * 1024) return false;

            var moovBytes = ReadAt(path, checked((int)moov.Offset), checked((int)moov.Size));
            var chpl = FindChpl(moovBytes);
            if (chpl == null) return false;

            var list = ParseChpl(chpl, out error);
            if (list.Count == 0) return false;

            var durationMs = ReadMovieDurationMs(moovBytes);
            for (var i = 0; i < list.Count; i++)
            {
                var end = i + 1 < list.Count ? list[i + 1].FromMs : durationMs;
                list[i].ToMs = end > list[i].FromMs ? end : list[i].FromMs;
            }
            chapters = list;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            chapters = new List<VideoChapter>();
            return false;
        }
    }

    private static List<VideoChapter> ParseChpl(byte[] payload, out string? error)
    {
        error = null;
        var list = new List<VideoChapter>();
        if (payload.Length < 5) { error = "chpl 太短"; return list; }

        var version = payload[0];
        var p = 4;
        if (version >= 1)
        {
            if (payload.Length < 9) { error = "chpl v1 头不完整"; return list; }
            p += 4; // reserved
        }
        if (p >= payload.Length) { error = "chpl 缺 count"; return list; }
        var count = payload[p++];
        for (var i = 0; i < count; i++)
        {
            if (p + 8 > payload.Length) { error = "chpl 时间戳越界"; break; }
            var ticks = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(p, 8));
            p += 8;
            if (p >= payload.Length) { error = "chpl 缺标题长度"; break; }
            var len = payload[p++];
            if (p + len > payload.Length) { error = "chpl 标题越界"; break; }
            var title = Encoding.UTF8.GetString(payload, p, len).Trim();
            p += len;
            var fromMs = checked((long)(ticks / (ulong)ChplTicksPerMs));
            list.Add(new VideoChapter
            {
                Content = title.Length == 0 ? $"第 {i + 1} 章" : title,
                FromMs = Math.Max(0, fromMs),
                ToMs = Math.Max(0, fromMs)
            });
        }

        // 播放器按开始时间查找章节; 写坏/乱序的文件也先排好。
        list.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        return list;
    }

    private static byte[]? FindChpl(byte[] moov)
    {
        var moovChildren = ParseBoxes(moov, 8, moov.Length - 8);
        // 一个 MP4 可以有多个 udta(实测: 一个放 meta, 一个放 chpl);
        // 不能只取第一个, 否则章节明明存在却读不到。
        foreach (var udta in moovChildren.FindAll(b => b.Type == "udta"))
        {
            var inner = ParseBoxes(moov, checked((int)udta.Offset + 8), checked((int)udta.Size - 8));
            var chpl = inner.Find(b => b.Type == "chpl");
            if (chpl == null) continue;
            var payload = new byte[chpl.Size - 8];
            Buffer.BlockCopy(moov, checked((int)chpl.Offset + 8), payload, 0, payload.Length);
            return payload;
        }
        return null;
    }

    private static long ReadMovieDurationMs(byte[] moov)
    {
        var mvhd = ParseBoxes(moov, 8, moov.Length - 8).Find(b => b.Type == "mvhd");
        if (mvhd == null) return 0;
        var p = checked((int)mvhd.Offset + 8);
        var version = moov[p];
        long timescale, duration;
        if (version == 1)
        {
            timescale = ReadU32(moov, p + 20);
            duration = checked((long)ReadU64(moov, p + 24));
        }
        else
        {
            timescale = ReadU32(moov, p + 12);
            duration = ReadU32(moov, p + 16);
        }
        if (timescale <= 0 || duration <= 0) return 0;
        return checked(duration * 1000L / timescale);
    }

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
            if (fs.Read(h, 0, 8) != 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.ASCII.GetString(h, 4, 4);
            var header = 8L;
            if (size == 1)
            {
                var ext = new byte[8];
                if (fs.Read(ext, 0, 8) != 8) break;
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(ext));
                header = 16;
            }
            else if (size == 0) size = len - p;
            if (size < header || p + size > len) break;
            result.Add(new BoxInfo(type, p, size));
            p += size;
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
                size = checked((uint)ReadU64(data, p + 8));
                header = 16;
            }
            else if (size == 0) size = (uint)(end - p);
            if (size < header || p + size > end) break;
            result.Add(new BoxInfo(type, p, size));
            p += checked((int)size);
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

    private static uint ReadU32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ulong ReadU64(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    private sealed record BoxInfo(string Type, long Offset, long Size);
}
