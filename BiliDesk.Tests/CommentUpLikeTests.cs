using System.Reflection;
using System.Text.Json;
using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// “UP 主觉得很赞”解析: 接口在评论条目顶层给
/// <c>up_action: { like: true }</c>。这里用最小 JSON 直接打到 ParseComment,
/// 防止解析字段被后续重构悄悄挪走(UI 上就表现为标记不出现)。
/// </summary>
public class CommentUpLikeTests
{
    private static CommentItem Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(ApiClient).GetMethod(
            "ParseComment", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var item = method!.Invoke(null, new object[] { doc.RootElement, false, 0L });
        return Assert.IsType<CommentItem>(item);
    }

    [Fact]
    public void UpActionLike为true时解析为已赞()
    {
        var item = Parse("""
            {
              "rpid": 313718224816,
              "mid": 3494360733715001,
              "like": 147,
              "up_action": { "like": true, "reply": false },
              "content": { "message": "开头一句话没把我笑死" }
            }
            """);

        Assert.True(item.IsUpLiked);
        Assert.Equal(147, item.LikeCount);
    }

    [Fact]
    public void 缺少UpAction时不算已赞()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "like": 3,
              "content": { "message": "普通评论" }
            }
            """);

        Assert.False(item.IsUpLiked);
    }
}
