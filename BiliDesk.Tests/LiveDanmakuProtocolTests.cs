using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 直播弹幕协议(2026-11-13 新增)的纯逻辑测试: 组包/拆包/解压/业务消息解析。
///
/// ★ 为什么值得单独测: 这块的输入是**字节流**, 出问题时的表现只有"直播间里一条弹幕都没有",
///   在真环境里几乎无法定位是大端写错、还是压缩解错、还是 cmd 带后缀没匹配上。
///   这里不联网, 直接喂构造好的包, 把三类错误分开钉死。
/// </summary>
public class LiveDanmakuProtocolTests
{
    // ------------------------------------------------------------ 工具: 手工组包

    private static byte[] Packet(int operation, byte[] body, int protocol = 1, int sequence = 1)
    {
        var packet = new byte[LiveDanmakuProtocol.HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), LiveDanmakuProtocol.HeaderSize);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), (short)protocol);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), sequence);
        body.CopyTo(packet.AsSpan(LiveDanmakuProtocol.HeaderSize));
        return packet;
    }

    private static byte[] Compress(byte[] data, bool zlib)
    {
        using var output = new MemoryStream();
        using (Stream encoder = zlib
                   ? new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)
                   : new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            encoder.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private const string DanmakuJson =
        "{\"cmd\":\"DANMU_MSG\",\"info\":[" +
        "[0,1,25,16711680,1700000000000,0,0,\"\",0,0,0],\"前方高能\",[12345,\"测试用户\",0,0]]}";

    // ------------------------------------------------------------ 组包

    [Fact]
    public void BuildHeartbeat_包头字段全部正确()
    {
        var packet = LiveDanmakuProtocol.BuildHeartbeatPacket();

        Assert.Equal(packet.Length, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(0, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(4, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(6, 2)));
        Assert.Equal(LiveDanmakuProtocol.OpHeartbeat, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4)));
        Assert.Equal("[Object object]", Encoding.UTF8.GetString(packet[16..]));
    }

    [Fact]
    public void BuildAuth_包含房间号密钥与protover3()
    {
        var packet = LiveDanmakuProtocol.BuildAuthPacket(545068, 8739477, "tok\"en", "buvid3-abc");
        var json = Encoding.UTF8.GetString(packet[16..]);

        Assert.Equal(LiveDanmakuProtocol.OpAuth, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4)));

        // ★ 用 JSON 解析断言而不是字符串包含: 密钥里可能带引号/反斜杠,
        //   System.Text.Json 会把它转义成 \u0022 之类的形式 —— 字符串比对会误判。
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(545068, root.GetProperty("roomid").GetInt64());
        Assert.Equal(8739477, root.GetProperty("uid").GetInt64());
        Assert.Equal(3, root.GetProperty("protover").GetInt32());
        Assert.Equal("web", root.GetProperty("platform").GetString());
        Assert.Equal(2, root.GetProperty("type").GetInt32());
        Assert.Equal("tok\"en", root.GetProperty("key").GetString());
        Assert.Equal("buvid3-abc", root.GetProperty("buvid").GetString());
    }

    // ------------------------------------------------------------ 拆包

    [Fact]
    public void Decode_单个明文包()
    {
        var body = Encoding.UTF8.GetBytes(DanmakuJson);
        var packets = LiveDanmakuProtocol.Decode(Packet(LiveDanmakuProtocol.OpMessage, body));

        var packet = Assert.Single(packets);
        Assert.Equal(LiveDanmakuProtocol.OpMessage, packet.Operation);
        Assert.Equal(body, packet.Body);
    }

    [Fact]
    public void Decode_粘在一起的多个包要逐个切开()
    {
        var first = Packet(LiveDanmakuProtocol.OpMessage, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var second = Packet(LiveDanmakuProtocol.OpHeartbeatReply, new byte[] { 0, 0, 0, 1 });
        var data = first.Concat(second).ToArray();

        var packets = LiveDanmakuProtocol.Decode(data);

        Assert.Equal(2, packets.Count);
        Assert.Equal(LiveDanmakuProtocol.OpMessage, packets[0].Operation);
        Assert.Equal(LiveDanmakuProtocol.OpHeartbeatReply, packets[1].Operation);
    }

    [Fact]
    public void Decode_长度字段被截断时安全放弃()
    {
        var body = Encoding.UTF8.GetBytes(DanmakuJson);
        var packet = Packet(LiveDanmakuProtocol.OpMessage, body);
        // 声称的长度比实际字节多: 不能越界读, 也不能返回半条消息
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length + 100);

        Assert.Empty(LiveDanmakuProtocol.Decode(packet));
    }

    [Fact]
    public void Decode_头长非法时安全放弃()
    {
        var packet = Packet(LiveDanmakuProtocol.OpMessage, new byte[] { 1, 2, 3 });
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), 8); // < 16

        Assert.Empty(LiveDanmakuProtocol.Decode(packet));
    }

    [Theory]
    [InlineData(true)]   // protover 2 = zlib
    [InlineData(false)]  // protover 3 = brotli
    public void Decode_压缩包里嵌套的包要递归解开(bool zlib)
    {
        var inner1 = Packet(LiveDanmakuProtocol.OpMessage, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var inner2 = Packet(LiveDanmakuProtocol.OpMessage, Encoding.UTF8.GetBytes("{\"b\":2}"));
        var compressed = Compress(inner1.Concat(inner2).ToArray(), zlib);
        var outer = Packet(LiveDanmakuProtocol.OpMessage, compressed, zlib ? 2 : 3);

        var packets = LiveDanmakuProtocol.Decode(outer);

        Assert.Equal(2, packets.Count);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(packets[0].Body));
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(packets[1].Body));
    }

    [Fact]
    public void Decode_损坏的压缩数据只丢这一包不抛异常()
    {
        var outer = Packet(LiveDanmakuProtocol.OpMessage, new byte[] { 1, 2, 3, 4, 5 }, protocol: 3);

        Assert.Empty(LiveDanmakuProtocol.Decode(outer));
    }

    // ------------------------------------------------------------ 业务消息

    [Fact]
    public void ParseDanmaku_普通滚动弹幕()
    {
        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(DanmakuJson, out var item));

        Assert.Equal("前方高能", item.Text);
        Assert.Equal(1, item.Mode);
        Assert.Equal(0xFF0000u, item.Color);   // 16711680
        Assert.Equal("测试用户", item.UserName);
        Assert.Equal(0, item.MedalLevel);
        Assert.False(item.IsAdmin);
    }

    [Fact]
    public void ParseDanmaku_解析粉丝勋章房管与用户等级()
    {
        var json = "{\"cmd\":\"DANMU_MSG\",\"info\":[[" +
                   "0,1,25,16777215,0,0,0,\"\",0,0,0],\"hi\"," +
                   "[1,\"u\",1,0,0]," +                                  // 房管
                   "[7,\"小电视\",\"主播\",12345,6067854,0]," +          // 勋章
                   "[12]]}";                                               // 用户等级

        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(json, out var item));
        Assert.Equal(7, item.MedalLevel);
        Assert.Equal("小电视", item.MedalName);
        Assert.Equal(6067854u, item.MedalColor);
        Assert.True(item.IsAdmin);
        Assert.Equal(12, item.UserLevel);
    }

    [Fact]
    public void ParseDanmaku_cmd带后缀也要认()
    {
        var json = DanmakuJson.Replace("\"DANMU_MSG\"", "\"DANMU_MSG:4:0:2:2:2:0\"");

        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(json, out var item));
        Assert.Equal("前方高能", item.Text);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ParseDanmaku_顶部底部固定模式保留(int mode)
    {
        var json = "{\"cmd\":\"DANMU_MSG\",\"info\":[[" +
                   "0," + mode + ",25,16777215,0,0,0,\"\",0,0,0],\"固定弹幕\",[1,\"u\",0,0]]}";

        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(json, out var item));
        Assert.Equal(mode, item.Mode);
    }

    [Fact]
    public void ParseDanmaku_未知模式归为滚动()
    {
        var json = "{\"cmd\":\"DANMU_MSG\",\"info\":[[0,7,25,0,0,0,0,\"\",0,0,0],\"高级\",[1,\"u\",0,0]]}";

        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(json, out var item));
        Assert.Equal(1, item.Mode);
        Assert.Equal(0xFFFFFFu, item.Color);   // 颜色 0 视为白色
    }

    [Theory]
    [InlineData("{\"cmd\":\"SUPER_CHAT_MESSAGE\",\"data\":{\"message\":\"x\"}}")]
    [InlineData("{\"cmd\":\"SEND_GIFT\",\"data\":{}}")]
    [InlineData("{\"cmd\":\"DANMU_MSG\"}")]                       // 没有 info
    [InlineData("{\"cmd\":\"DANMU_MSG\",\"info\":[]}")]           // info 为空
    [InlineData("{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,0,0],\"\",[1,\"u\"]]}")] // 空白正文
    [InlineData("不是 JSON")]
    public void ParseDanmaku_非聊天或残缺消息返回false(string json)
    {
        Assert.False(LiveDanmakuProtocol.TryParseDanmaku(json, out _));
    }

    [Fact]
    public void ParseDanmaku_正文首尾空白被裁掉()
    {
        var json = "{\"cmd\":\"DANMU_MSG\",\"info\":[[0,1,25,16777215,0,0,0,\"\",0,0,0],\"  hi  \",[1,\"u\",0,0]]}";

        Assert.True(LiveDanmakuProtocol.TryParseDanmaku(json, out var item));
        Assert.Equal("hi", item.Text);
    }

    // ------------------------------------------------------------ 鉴权回应

    [Theory]
    [InlineData("{\"code\":0}", 0)]
    [InlineData("{\"code\":-101}", -101)]
    [InlineData("{\"code\":\"0\"}", -1)]   // 非数字: 按失败处理
    [InlineData("不是 JSON", -1)]
    public void ParseAuthCode_按接口形状取值(string json, int expected)
    {
        Assert.Equal(expected, LiveDanmakuProtocol.ParseAuthCode(Encoding.UTF8.GetBytes(json)));
    }
}
