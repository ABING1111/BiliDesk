using System;
using System.Collections.Generic;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 极简 protobuf 读取器(只支持读取, 不支持写入)。
///
/// 为什么手写而不引第三方库:
/// 我们只需要解析 B 站弹幕接口(/x/v2/dm/web/seg.so)返回的 DmSegMobileReply,
/// 它的字段结构几十年没变、且只有 varint / length-delimited 两种线格式。
/// 为此引入 Google.Protobuf 或 protobuf-net(还要为它生成 .proto 代码)会让
/// 单 exe 体积和启动开销都变大, 收益不成比例。
///
/// 支持的线格式:
///   0 = varint        (int32/int64/bool/enum)
///   1 = 64-bit        (fixed64/double, 8 字节)
///   2 = length-delim  (string/bytes/嵌套 message)
///   5 = 32-bit        (fixed32/float, 4 字节)
/// 其余(3/4 组起始/结束)在 proto3 里已废弃, 遇到直接抛异常由调用方兜住。
/// </summary>
public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> buffer)
    {
        _buf = buffer;
        _pos = 0;
    }

    public bool Eof => _pos >= _buf.Length;

    /// <summary>读取一个 tag, 返回 (字段号, 线格式)。已到末尾时返回 false</summary>
    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (Eof) return false;

        var tag = ReadRawVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return true;
    }

    private ulong ReadRawVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_pos >= _buf.Length)
                throw new InvalidOperationException("protobuf: varint 越界");
            var b = _buf[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            // varint 最长 10 字节(64 位), 超过说明数据已损坏
            if (shift > 63) throw new InvalidOperationException("protobuf: varint 过长");
        }
        return result;
    }

    /// <summary>按字段类型跳过该字段的值</summary>
    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadRawVarint();
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                var len = (int)ReadRawVarint();
                Advance(len);
                break;
            case 5:
                Advance(4);
                break;
            default:
                throw new InvalidOperationException("protobuf: 不支持的线格式 " + wireType);
        }
    }

    public int ReadInt32(int wireType)
    {
        if (wireType != 0) { Skip(wireType); return 0; }
        return (int)ReadRawVarint();
    }

    /// <summary>读 uint32(弹幕颜色字段用它 —— 颜色是 0xRRGGBB, 高位不该当符号位)</summary>
    public uint ReadUInt32(int wireType)
    {
        if (wireType != 0) { Skip(wireType); return 0; }
        return (uint)ReadRawVarint();
    }

    /// <summary>读取 length-delimited 字段, 返回其内容片段(不拷贝)</summary>
    public ReadOnlySpan<byte> ReadBytes(int wireType)
    {
        if (wireType != 2) { Skip(wireType); return default; }
        var len = (int)ReadRawVarint();
        if (len < 0 || _pos + len > _buf.Length)
            throw new InvalidOperationException("protobuf: length-delimited 越界");
        var slice = _buf.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString(int wireType)
    {
        var bytes = ReadBytes(wireType);
        return bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes);
    }

    private void Advance(int n)
    {
        if (n < 0 || _pos + n > _buf.Length)
            throw new InvalidOperationException("protobuf: 越界");
        _pos += n;
    }
}

/// <summary>
/// 一条弹幕(protobuf 解析结果, 也是全应用传递弹幕的统一类型)。
///
/// 为什么全流程都用它而不是 `(double, string, int)` 元组: 元组给弹幕加一个字段
/// (比如这次的颜色)就要把签名在接口层/过滤/二分查找/渲染 6 个地方各改一遍,
/// 漏一处就是编译不过或者悄悄丢字段。
///
/// 只读 struct: 弹幕量可达几千条, 类对象会让 GC 压力明显上升。
/// </summary>
public readonly struct RawDanmaku
{
    public readonly double Time;   // 出现时间(秒)
    public readonly string Text;   // 内容
    public readonly int Mode;      // 1/2/3 滚动, 4 底部, 5 顶部, 6 逆向, 7 高级, 8 代码, 9 BAS

    /// <summary>
    /// 颜色, 0xRRGGBB(与接口的十进制字段同义: 16777215 = 白)。
    /// 只有"用户发弹幕时挑过颜色"的才是非白值 —— 也就是我们说的彩色弹幕。
    /// </summary>
    public readonly uint Color;

    public RawDanmaku(double time, string text, int mode, uint color = 0xFFFFFF)
    {
        Time = time;
        Text = text;
        Mode = mode;
        Color = color;
    }
}

/// <summary>弹幕 protobuf 解析器: 把 DmSegMobileReply 二进制解成 RawDanmaku 列表</summary>
public static class DanmakuParser
{
    /// <summary>
    /// 解析 DmSegMobileReply。
    ///
    /// 消息结构(与 B 站前端 / bilibili-API-collect 文档一致):
    ///   message DmSegMobileReply { repeated DanmakuElem elems = 1; }
    ///   message DanmakuElem {
    ///     int64  id       = 1;
    ///     int32  progress = 2;   // 出现时间, 单位毫秒
    ///     int32  mode     = 3;
    ///     int32  fontsize = 4;
    ///     uint32 color    = 5;
    ///     string midHash  = 6;
    ///     string content  = 7;
    ///     int64  ctime    = 8;
    ///     int32  weight   = 9;
    ///   }
    /// 这里只取我们渲染需要的 progress / mode / content, 其余字段跳过。
    /// </summary>
    public static List<RawDanmaku> ParseSegReply(ReadOnlySpan<byte> data, int maxCount = int.MaxValue)
    {
        var list = new List<RawDanmaku>();
        if (data.Length == 0) return list;

        try
        {
            var reader = new ProtoReader(data);
            while (reader.TryReadTag(out var field, out var wire))
            {
                if (field == 1 && wire == 2)
                {
                    if (list.Count >= maxCount) break;
                    var elemBytes = reader.ReadBytes(wire);
                    var elem = ParseElem(elemBytes);
                    if (elem.Text.Length > 0) list.Add(elem);
                }
                else
                {
                    reader.Skip(wire);
                }
            }
        }
        catch
        {
            // 解析中断(数据损坏/接口变更): 返回已成功解析的部分, 不抛给上层
        }
        return list;
    }

    private static RawDanmaku ParseElem(ReadOnlySpan<byte> elemBytes)
    {
        var progressMs = 0;
        var mode = 1;
        var content = "";
        uint color = 0xFFFFFF;   // 缺字段时按白色(接口绝大多数弹幕都是白)

        var reader = new ProtoReader(elemBytes);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 2: progressMs = reader.ReadInt32(wire); break;
                case 3: mode = reader.ReadInt32(wire); break;
                case 5: color = reader.ReadUInt32(wire); break;
                case 7: content = reader.ReadString(wire); break;
                default: reader.Skip(wire); break;
            }
        }

        // mode 6 是逆向滚动; 我们统一按正向右移处理, 避免出现"从右往左倒着飞"的怪象
        if (mode == 6) mode = 1;

        // 颜色规范化: 极少数第三方客户端把"白色"发成 255255255 而不是 16777215
        // (实测某个视频的一段里就有 3 条), 这种值 > 0xFFFFFF, 不是合法 RGB。
        // 不处理的话会被渲染端的位运算切成一个随机的青/蓝, 所以统一按白色收编。
        if (color > 0xFFFFFF) color = 0xFFFFFF;

        return new RawDanmaku(progressMs / 1000.0, content, mode, color);
    }
}
