using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// B 站直播弹幕 WebSocket 协议(2026-11-13 新增): 组包 / 拆包 / 解压 / 业务消息解析。
///
/// ★ 为什么整块抽成纯函数(不放进 Service):
///   这套协议最容易出错的地方全在**字节层** —— 大端包头、嵌套压缩包、cmd 带后缀。
///   把它和网络、重连、UI 混在一起时, 出问题只能"进直播间碰运气";
///   抽成纯逻辑后, 单元测试可以直接喂构造好的字节流断言, 不需要真的连 B 站。
///
/// 包结构(全部大端):
///   偏移 0  4B 包总长(含 16B 头)
///   偏移 4  2B 头长(固定 16)
///   偏移 6  2B 协议版本: 0/1=纯 JSON, 2=zlib, 3=brotli
///   偏移 8  4B 操作码: 2=心跳 3=心跳回应 5=业务消息 7=鉴权 8=鉴权回应
///   偏移 12 4B 序号(固定 1 即可)
/// </summary>
public static class LiveDanmakuProtocol
{
    public const int HeaderSize = 16;

    // ---- 操作码 ----
    public const int OpHeartbeat = 2;
    public const int OpHeartbeatReply = 3;
    public const int OpMessage = 5;
    public const int OpAuth = 7;
    public const int OpAuthReply = 8;

    // ---- 协议版本 ----
    private const int ProtoPlain = 0;    // 1 也是纯 JSON, 这里归为同一类
    private const int ProtoZlib = 2;
    private const int ProtoBrotli = 3;

    /// <summary>单个包的长度上限。畸形/恶意包直接丢弃, 不让它把内存打爆。</summary>
    private const int MaxPacketBytes = 8 * 1024 * 1024;

    /// <summary>压缩包最多嵌套几层。正常只有一层, 留余量防"解压炸弹"式构造。</summary>
    private const int MaxDecodeDepth = 4;

    /// <summary>直播弹幕正文的长度上限(像素溢出/内存保护)。超过直接截断。</summary>
    private const int MaxTextLength = 100;

    /// <summary>解包结果: 协议层只关心"操作码 + 原始 body"。</summary>
    public readonly struct LivePacket
    {
        public readonly int Operation;
        public readonly byte[] Body;

        public LivePacket(int operation, byte[] body)
        {
            Operation = operation;
            Body = body;
        }
    }

    // ------------------------------------------------------------ 组包

    /// <summary>
    /// 组一个完整包。只用于客户端发出的鉴权包与心跳包(协议版本固定 1 = 普通包)。
    /// </summary>
    public static byte[] BuildPacket(int operation, ReadOnlySpan<byte> body, int sequence = 1)
    {
        var packet = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), sequence);
        body.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>
    /// 鉴权包。★ protover 必须与下面一致(3 = brotli):
    ///   它决定服务端之后用什么协议推业务包 —— 两边对不上就只能解出乱码。
    /// </summary>
    public static byte[] BuildAuthPacket(long roomId, long uid, string token, string buvid)
    {
        var json = new StringBuilder(192)
            .Append("{\"uid\":").Append(uid)
            .Append(",\"roomid\":").Append(roomId)
            .Append(",\"protover\":").Append(ProtoBrotli)
            .Append(",\"buvid\":").Append(JsonSerializer.Serialize(buvid))
            .Append(",\"platform\":\"web\"")
            .Append(",\"type\":2")
            .Append(",\"key\":").Append(JsonSerializer.Serialize(token))
            .Append('}');
        return BuildPacket(OpAuth, Encoding.UTF8.GetBytes(json.ToString()));
    }

    /// <summary>心跳包。body 用 B 站自己的占位串即可, 服务端不解析内容。</summary>
    public static byte[] BuildHeartbeatPacket()
        => BuildPacket(OpHeartbeat, "[Object object]"u8);

    // ------------------------------------------------------------ 拆包 / 解压

    /// <summary>
    /// 把一次 WebSocket 收到的二进制帧解成业务包列表。
    /// 会自动处理"压缩包里面还套着若干完整包"的嵌套结构。
    /// </summary>
    public static List<LivePacket> Decode(ReadOnlySpan<byte> data)
    {
        var packets = new List<LivePacket>();
        DecodeInto(data, packets, 0);
        return packets;
    }

    private static void DecodeInto(ReadOnlySpan<byte> data, List<LivePacket> output, int depth)
    {
        if (depth > MaxDecodeDepth) return;

        var offset = 0;
        while (data.Length - offset >= HeaderSize)
        {
            var span = data[offset..];
            var packetLength = BinaryPrimitives.ReadInt32BigEndian(span[..4]);
            var headerLength = BinaryPrimitives.ReadInt16BigEndian(span.Slice(4, 2));
            var protocol = BinaryPrimitives.ReadInt16BigEndian(span.Slice(6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));

            // 长度字段不可信(网络截断/畸形包): 一个字节都不再消费, 直接放弃本次剩余数据。
            // 宁可丢这一帧, 也不能越界读或无限循环。
            if (packetLength < HeaderSize || packetLength > span.Length || packetLength > MaxPacketBytes)
                return;
            if (headerLength < HeaderSize || headerLength > packetLength)
                return;

            var body = span[headerLength..packetLength];

            if (protocol == ProtoZlib || protocol == ProtoBrotli)
            {
                var decompressed = Decompress(body, protocol == ProtoZlib);
                if (decompressed.Length > 0)
                    DecodeInto(decompressed, output, depth + 1);
            }
            else if (protocol == ProtoPlain || protocol == 1)
            {
                // 纯 JSON 包: 复制出来交给调用方(span 指向接收缓冲区, 下一帧会被覆盖)
                output.Add(new LivePacket(operation, body.ToArray()));
            }
            // 其余协议版本(目前未定义)直接跳过, 不能当明文解析 —— 那样只会得到一堆乱码异常

            offset += packetLength;
        }
    }

    private static byte[] Decompress(ReadOnlySpan<byte> body, bool zlib)
    {
        try
        {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using Stream decoder = zlib
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decoder.CopyTo(output);

            // 解压后同样限长: 这是"解压炸弹"唯一的实际防线(压缩比可以做到上千倍)
            if (output.Length > MaxPacketBytes) return Array.Empty<byte>();
            return output.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    // ------------------------------------------------------------ 业务消息

    /// <summary>
    /// 从一条业务 JSON 里解析普通聊天弹幕。
    ///
    /// ★ 只认 DANMU_MSG —— 礼物/醒目留言/进场提示在播放器里属于噪音, 由调用方
    ///   "解析不出来就不显示"自然忽略, 不需要在这里逐个列 cmd。
    /// ★ cmd 可能是 <c>DANMU_MSG</c>, 也可能是 <c>DANMU_MSG:4:0:2:2:2:0</c>(带后缀),
    ///   所以必须用 StartsWith, 不能相等比较。
    /// </summary>
    public static bool TryParseDanmaku(string json, out LiveDanmakuItem item)
    {
        item = default;
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("cmd", out var cmdElement) ||
                cmdElement.ValueKind != JsonValueKind.String)
                return false;
            var cmd = cmdElement.GetString();
            if (cmd == null || !cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal)) return false;

            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array)
                return false;
            if (info.GetArrayLength() < 3) return false;

            // info[0] = [?, 模式, 字号, 颜色, 时间戳, ...]
            var mode = 1;
            var color = 0xFFFFFFu;
            var head = info[0];
            if (head.ValueKind == JsonValueKind.Array && head.GetArrayLength() > 3)
            {
                var modeElement = head[1];
                if (modeElement.ValueKind == JsonValueKind.Number && modeElement.TryGetInt32(out var m))
                    mode = m;

                var colorElement = head[3];
                if (colorElement.ValueKind == JsonValueKind.Number && colorElement.TryGetInt64(out var c))
                {
                    // 0 表示"没指定", 与白色等价; 越界值按白色处理(避免 Color.FromRgb 收到垃圾)
                    if (c > 0 && c <= 0xFFFFFF) color = (uint)c;
                }
            }
            // 直播只会用到这三种; 其它值(历史遗留/新玩法)一律当滚动, 避免出现"看不见的弹幕"
            if (mode != 4 && mode != 5) mode = 1;

            var textElement = info[1];
            if (textElement.ValueKind != JsonValueKind.String) return false;
            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];

            // info[2] = [uid, 昵称, 是否房管, ...]
            var userName = "";
            if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array &&
                info[2].GetArrayLength() > 1 && info[2][1].ValueKind == JsonValueKind.String)
                userName = info[2][1].GetString() ?? "";

            // 房管: info[2][2] == 1
            var isAdmin = false;
            if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array &&
                info[2].GetArrayLength() > 2 && info[2][2].ValueKind == JsonValueKind.Number)
                isAdmin = info[2][2].TryGetInt32(out var adminFlag) && adminFlag == 1;

            // 粉丝勋章: info[3] = [等级, 名称, 主播名, 房间号, 颜色, ...]
            var medalLevel = 0;
            var medalName = "";
            var medalColor = 0u;
            if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array &&
                info[3].GetArrayLength() >= 5)
            {
                if (info[3][0].ValueKind == JsonValueKind.Number &&
                    info[3][0].TryGetInt32(out var level))
                    medalLevel = level;
                if (info[3][1].ValueKind == JsonValueKind.String)
                    medalName = info[3][1].GetString() ?? "";
                if (info[3][4].ValueKind == JsonValueKind.Number &&
                    info[3][4].TryGetInt64(out var medalColorValue) &&
                    medalColorValue > 0 && medalColorValue <= 0xFFFFFF)
                    medalColor = (uint)medalColorValue;
            }

            // 用户等级: info[4] = [等级, ...]
            var userLevel = 0;
            if (info.GetArrayLength() > 4 && info[4].ValueKind == JsonValueKind.Array &&
                info[4].GetArrayLength() > 0 && info[4][0].ValueKind == JsonValueKind.Number)
                info[4][0].TryGetInt32(out userLevel);

            item = new LiveDanmakuItem(text, mode, color, userName,
                medalLevel, medalName, medalColor, isAdmin, userLevel);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析鉴权回应里的 code。0 = 成功; 返回 -1 表示响应本身不可解析(按失败处理)。
    /// </summary>
    public static int ParseAuthCode(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            return doc.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value)
                ? value : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }
}
