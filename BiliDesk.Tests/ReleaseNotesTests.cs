using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// Release 正文清洗。样本照抄真实 GitHub Release body(含开头重复标题与末尾下载/SHA256),
/// 站点生成脚本 gen_changelog.py 对 HTML 做的正是同一件事。
/// </summary>
public class ReleaseNotesTests
{
    private const string Sample = """
## 1.8.1 更新内容

### 新增

- **滑条按压动画**：音量条与进度条的白点按下时会放大，松手回弹。
- **历史与收藏支持搜索**：搜索框收进顶部标签栏右侧。
  - 历史按关键词筛选。

### 修复

- **UP 主主页获赞数不对齐**。

直接覆盖安装即可，配置与缓存都会保留。

---

### 下载

- `BiliDesk-1.8.1-setup.exe` · 安装版
- `BiliDesk-1.8.1-portable.zip` · 便携版

**SHA256**

```text
206BAE7C00B6EF9827A80FF185E08B7C6DF807B48F702DBCD5BD94357D1A7E75  BiliDesk-1.8.1-setup.exe
```
""";

    [Fact]
    public void 去掉重复标题()
    {
        var clean = ReleaseNotes.Clean(Sample, "1.8.1");
        Assert.DoesNotContain("## 1.8.1 更新内容", clean);
        Assert.StartsWith("### 新增", clean);
    }

    [Fact]
    public void 裁掉下载与校验和段()
    {
        var clean = ReleaseNotes.Clean(Sample, "1.8.1");
        Assert.DoesNotContain("下载", clean);
        Assert.DoesNotContain("SHA256", clean);
        Assert.DoesNotContain("setup.exe", clean);
        Assert.DoesNotContain("206BAE7C", clean);
        // 下载段前面那条 --- 也要跟着走, 不然正文末尾会挂一条孤零零的分隔线
        Assert.DoesNotContain("---", clean);
    }

    [Fact]
    public void 正文一段不少()
    {
        var clean = ReleaseNotes.Clean(Sample, "1.8.1");
        Assert.Contains("### 新增", clean);
        Assert.Contains("### 修复", clean);
        Assert.Contains("滑条按压动画", clean);
        Assert.Contains("  - 历史按关键词筛选。", clean);
        Assert.Contains("直接覆盖安装即可", clean);
    }

    [Fact]
    public void 加粗写法的下载段也认()
    {
        var body = "### 修复\n\n- 一条。\n\n---\n\n**下载**\n\n- `x.zip`\n";
        var clean = ReleaseNotes.Clean(body, "1.6.4");
        Assert.DoesNotContain("下载", clean);
        Assert.DoesNotContain("x.zip", clean);
        Assert.Contains("一条。", clean);
    }

    [Fact]
    public void 没有下载段时正文原样保留()
    {
        var body = "### 改进\n\n- 过渡动画改为纯颜色渐变。";
        Assert.Equal(body, ReleaseNotes.Clean(body, "1.7.1"));
    }

    [Fact]
    public void CRLF与BOM不影响清洗()
    {
        var body = "\uFEFF## 1.8.0 更新内容\r\n\r\n### 新增\r\n\r\n- 一条。\r\n";
        var clean = ReleaseNotes.Clean(body, "1.8.0");
        Assert.Equal("### 新增\n\n- 一条。", clean.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void 空正文返回空串(string? body)
    {
        Assert.Equal("", ReleaseNotes.Clean(body, "1.8.1"));
    }
}
