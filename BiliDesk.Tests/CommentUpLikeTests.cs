using System.Reflection;
using System.Text.Json;
using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// "UP主觉得很赞"解析: 接口在评论条目顶层给 <c>up_action: { like: true }</c>。
/// 这里用最小 JSON 直接打到 ParseComment, 防止解析字段被后续重构悄悄挪走
/// (UI 上就表现为标记不出现)。
///
/// ★ 实测依据(2026-11-27 抓 /x/v2/reply/main, 333 条样本):
///   · up_action 在**评论条目顶层**, 从不为 null;
///   · 里面的 like 是**真 bool**;
///   · 官方文案在**另一处** card_label[].text_content 里, 不在 up_action 里。
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

    [Fact]
    public void UpActionLike为false时不算已赞()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": false, "reply": true },
              "content": { "message": "普通评论" }
            }
            """);

        Assert.False(item.IsUpLiked);
    }

    /// <summary>
    /// up_action 为 null 时不能崩(接口偶尔会给 null), 也不能算已赞。
    /// ★ ParseComment 外层只有一个大 try —— 这里抛异常整页评论全丢。
    /// </summary>
    [Fact]
    public void UpAction为null时不算已赞()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": null,
              "content": { "message": "普通评论" }
            }
            """);

        Assert.False(item.IsUpLiked);
    }

    /// <summary>
    /// 数字形态的 like 也要认。
    /// ★ 这里特意用 **1.0**(小数) 而不是 1: JsonElement.TryGetInt32 遇到小数
    ///   会直接返回 false(实测确认), 所以早先那版用 TryGetInt32 的兜底分支
    ///   遇到小数是**静默失效**的 —— 改成 GetDouble 才走得通。
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("2")]
    public void 数字形态的like也算已赞(string like)
    {
        var item = Parse($$"""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": {{like}} },
              "content": { "message": "普通评论" }
            }
            """);

        Assert.True(item.IsUpLiked);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    public void 数字形态的零不算已赞(string like)
    {
        var item = Parse($$"""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": {{like}} },
              "content": { "message": "普通评论" }
            }
            """);

        Assert.False(item.IsUpLiked);
    }

    /// <summary>
    /// 官方文案走 card_label(实测 text_content = "UP主觉得很赞"), 界面绑它,
    /// 官方改文案我们不用发版。
    /// </summary>
    [Fact]
    public void 文案取自CardLabel()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": true, "reply": true },
              "card_label": [
                {
                  "rpid": 1,
                  "text_content": "UP主觉得很赞",
                  "text_color_day": "#757575",
                  "text_color_night": "#939393",
                  "label_color_day": "#F4F4F4",
                  "label_color_night": "#1E1E1E",
                  "type": 0
                }
              ],
              "content": { "message": "普通评论" }
            }
            """);

        Assert.True(item.IsUpLiked);
        Assert.Equal("UP主觉得很赞", item.UpLikedText);
    }

    /// <summary>
    /// card_label 里没有对应标签(接口没给 / 将来换了 type)时回落到默认文案,
    /// 不能让这行字变成空白。
    /// </summary>
    [Fact]
    public void 没有CardLabel时回落到默认文案()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": true },
              "content": { "message": "普通评论" }
            }
            """);

        Assert.True(item.IsUpLiked);
        Assert.Equal(CommentItem.DefaultUpLikedText, item.UpLikedText);
        Assert.NotEmpty(item.UpLikedText);
    }

    /// <summary>
    /// card_label 是**通用角标数组**, 将来 B 站可能塞别的角标进去。
    /// 只认 type 显式为 0 的那个 —— 别的 type 不能被当成"UP主觉得很赞"。
    /// </summary>
    [Fact]
    public void 非零Type的角标不当作Up赞文案()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": true },
              "card_label": [
                { "text_content": "别的角标", "type": 1 }
              ],
              "content": { "message": "普通评论" }
            }
            """);

        Assert.True(item.IsUpLiked);
        Assert.Equal(CommentItem.DefaultUpLikedText, item.UpLikedText);
    }

    /// <summary>没被 UP 赞过时不该显示这行字(文案字段留着也不影响, 界面按 IsUpLiked 折叠)。</summary>
    [Fact]
    public void 未被Up赞时不取CardLabel文案()
    {
        var item = Parse("""
            {
              "rpid": 1,
              "mid": 2,
              "up_action": { "like": false },
              "card_label": [
                { "text_content": "UP主觉得很赞", "type": 0 }
              ],
              "content": { "message": "普通评论" }
            }
            """);

        Assert.False(item.IsUpLiked);
        Assert.Equal(CommentItem.DefaultUpLikedText, item.UpLikedText);
    }
}
