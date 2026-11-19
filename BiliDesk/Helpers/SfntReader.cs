using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 最小化 sfnt(.ttf/.otf/.ttc) 表读取器(2026-11-19 新增): 只为 UiFont 的「基准字重」判定服务,
/// 只读两张表 —— name(ID1/2/16/17) 与 fvar(wght 轴默认值), 其余一概不碰。
///
/// 为什么不直接用 WPF 公开 API: FamilyTypefaces 只给 (Style, Weight, Stretch) 三元组,
/// 既不暴露"哪个字面叫 Regular", 也不暴露 fvar 默认实例 —— 而 MiSans 这类非标准字重家族
/// 恰恰需要这两样才能把 400 请求校正回真正的 Regular(见 UiFont.PickRegularWeight)。
/// GlyphTypeface.FontUri 给出文件路径, 剩下就是几张大端序表, 自己读最省依赖。
///
/// ★ 所有读取都有边界保护: 表偏移/长度越界、不认识的格式一律返回空事实,
///   让上层回落 400(缺证据时不校正, 见 UiFont.PickRegularWeight 第 3 条规则)。
/// </summary>
internal static class SfntReader
{
    /// <summary>单个子字体的关键事实: 首选子族名(ID17, 缺则 ID2)与 fvar 的 wght 默认值(可变字体才有)。</summary>
    public readonly record struct FontFacts(string? Subfamily, int? FvarDefault);

    /// <summary>
    /// 读一个字体文件的关键事实。.ttc(集合)按 familyNames 匹配子字体(匹配不到取第一个);
    /// 单字体文件直接读。任何失败都返回空事实, 不抛异常。
    /// </summary>
    public static FontFacts Inspect(string path, IReadOnlyList<string> familyNames)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            if (!ReadExactly(fs, header)) return default;
            var magic = BeU32(header, 0);

            if (magic == 0x74746366) // 'ttcf' 字体集合
            {
                var buf = new byte[8];
                if (!ReadExactly(fs, buf)) return default;
                var count = BeU16(buf, 4);
                var offsets = new byte[4 * count];
                if (!ReadExactly(fs, offsets)) return default;

                FontFacts? first = null, matched = null;
                for (var i = 0; i < count && matched == null; i++)
                {
                    var offset = (long)BeU32(offsets, 4 * i);
                    var facts = InspectSubFont(fs, offset, familyNames, out var familyMatched);
                    first ??= facts;
                    if (familyMatched) matched = facts;
                }
                return matched ?? first ?? default;
            }

            return InspectSubFont(fs, 0, familyNames, out _);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// 读一个 sfnt 起点(offset 支持跳进 .ttc 的子字体)的 name/fvar。
    /// familyMatched = 该子字体的族名(ID1/ID16)是否命中 familyNames(.ttc 选子字体用)。
    /// </summary>
    private static FontFacts InspectSubFont(
        FileStream fs, long sfntOffset, IReadOnlyList<string> familyNames, out bool familyMatched)
    {
        familyMatched = false;
        try
        {
            fs.Seek(sfntOffset, SeekOrigin.Begin);
            var header = new byte[12];
            if (!ReadExactly(fs, header)) return default;
            var numTables = BeU16(header, 4);
            var records = new byte[16 * numTables];
            if (!ReadExactly(fs, records)) return default;

            long nameOff = -1, nameLen = 0, fvarOff = -1, fvarLen = 0;
            for (var i = 0; i < numTables; i++)
            {
                var tag = BeU32(records, 16 * i);
                var off = (long)BeU32(records, 16 * i + 8);
                var len = (long)BeU32(records, 16 * i + 12);
                switch (tag)
                {
                    case 0x6E616D65: nameOff = off; nameLen = len; break; // 'name'
                    case 0x66766172: fvarOff = off; fvarLen = len; break; // 'fvar'
                }
            }

            // 可变字体: "Regular" = fvar 默认实例, 它不依赖名字表, 先读出来
            int? fvarDefault = null;
            if (fvarOff >= 0) fvarDefault = ReadFvarWghtDefault(fs, fvarOff, fvarLen);

            var (fam1, fam16, sub2, sub17) = nameOff >= 0
                ? ReadNameTable(fs, nameOff, nameLen)
                : (null, null, null, null);

            foreach (var want in familyNames)
                if (EqualsName(want, fam1) || EqualsName(want, fam16))
                {
                    familyMatched = true;
                    break;
                }

            // 子族名: 排版子族名(ID17)优先于 legacy 子族名(ID2) —— 静态多字重族(如 MiSans 全家桶)
            // 每个文件的 ID2 都是 "Regular"(GDI 兼容命名), 只有 ID17 才是真身("Medium"/"Thin"/...)
            return new FontFacts(sub17 ?? sub2, fvarDefault);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>读 name 表里 ID1/ID2/ID16/ID17 四个名字(每种取先遇到的, 通常是 en-us)。</summary>
    private static (string? Fam1, string? Fam16, string? Sub2, string? Sub17) ReadNameTable(
        FileStream fs, long offset, long length)
    {
        try
        {
            var head = new byte[6];
            fs.Seek(offset, SeekOrigin.Begin);
            if (!ReadExactly(fs, head)) return default;
            var count = BeU16(head, 2);
            var stringOffset = BeU16(head, 4);
            var records = new byte[12 * count];
            if (!ReadExactly(fs, records)) return default;

            string? fam1 = null, fam16 = null, sub2 = null, sub17 = null;
            for (var i = 0; i < count; i++)
            {
                var platform = BeU16(records, 12 * i);
                var nameId = BeU16(records, 12 * i + 6);
                var len = BeU16(records, 12 * i + 8);
                var strOff = BeU16(records, 12 * i + 10);
                if (len <= 0) continue;
                var bytes = new byte[len];
                fs.Seek(offset + stringOffset + strOff, SeekOrigin.Begin);
                if (!ReadExactly(fs, bytes)) continue;
                var text = Decode(platform, bytes);
                if (text.Length == 0) continue;
                switch (nameId)
                {
                    case 1: fam1 ??= text; break;
                    case 2: sub2 ??= text; break;
                    case 16: fam16 ??= text; break;
                    case 17: sub17 ??= text; break;
                }
            }
            return (fam1, fam16, sub2, sub17);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>读 fvar 表里 'wght' 轴的默认值(16.16 定点 → 取整)。没有 wght 轴/不是可变字体返回 null。</summary>
    private static int? ReadFvarWghtDefault(FileStream fs, long offset, long length)
    {
        try
        {
            if (length < 16) return null;
            var head = new byte[16];
            fs.Seek(offset, SeekOrigin.Begin);
            if (!ReadExactly(fs, head)) return null;
            var axesArrayOffset = BeU16(head, 4);
            var axisCount = BeU16(head, 8);
            var axisSize = BeU16(head, 10);
            if (axisCount == 0 || axisSize < 20) return null;
            if (axesArrayOffset + (long)axisCount * axisSize > length) return null;

            var axes = new byte[axisCount * axisSize];
            fs.Seek(offset + axesArrayOffset, SeekOrigin.Begin);
            if (!ReadExactly(fs, axes)) return null;
            for (var i = 0; i < axisCount; i++)
            {
                var tag = BeU32(axes, i * axisSize);
                if (tag == 0x77676874) // 'wght'
                {
                    var fixed16 = BeU32(axes, i * axisSize + 8); // defaultValue(Fixed 16.16)
                    return (int)Math.Round(fixed16 / 65536.0);
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Decode(int platform, byte[] bytes)
    {
        try
        {
            // platform 3/0: UTF-16BE(现代字体一律此编码); platform 1: Mac Roman(ASCII 子集, Latin-1 兜底)
            return platform == 1
                ? Encoding.Latin1.GetString(bytes)
                : Encoding.BigEndianUnicode.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static bool EqualsName(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ReadExactly(FileStream fs, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = fs.Read(buffer, total, buffer.Length - total);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static uint BeU32(byte[] b, int i)
        => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static ushort BeU16(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
}
