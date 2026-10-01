using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BiliDesk.Services;

/// <summary>
/// WebView2 服务: 全应用共享同一个浏览器环境(用户数据目录),
/// 这样登录窗口的 Cookie 自动对播放器生效, 无需手动注入
/// </summary>
public class WebView2Service
{
    private Task<CoreWebView2Environment>? _envTask;

    public Task<CoreWebView2Environment> GetEnvironmentAsync()
        => _envTask ??= CoreWebView2Environment.CreateAsync(
            null, AppPaths.WebView2Dir, null);

    /// <summary>初始化指定 WebView2 控件(挂载共享环境 + 应用主题色彩 + 视频页清理脚本)</summary>
    public async Task InitAsync(WebView2 webView)
    {
        var env = await GetEnvironmentAsync();
        if (webView.CoreWebView2 == null)
            await webView.EnsureCoreWebView2Async(env);
        ApplyColorScheme(webView);
        await EnsureVideoCleanerInjectedAsync(webView);
    }

    /// <summary>为视频页注入的精简脚本: 隐藏广告/弹幕/推荐/举报/笔记/稍后看等
    /// 只保留播放器、点赞、关注、收藏、合集、UP主卡片、UP主首页入口
    /// 每次新文档创建时执行, 并通过 MutationObserver 持续清理动态加载内容</summary>
    private static readonly HashSet<int> _cleanerInjectedWebViews = new();

    private async Task EnsureVideoCleanerInjectedAsync(WebView2 webView)
    {
        var hash = webView.GetHashCode();
        if (_cleanerInjectedWebViews.Contains(hash)) return;
        try
        {
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(VideoPageCleanerScript);
            _cleanerInjectedWebViews.Add(hash);
        }
        catch
        {
            // 注入失败不影响主流程
        }
    }

    private const string VideoPageCleanerScript = @"
(function(){
  if (window.__bdVideoCleanerLoaded) return;
  window.__bdVideoCleanerLoaded = true;

  // ============ CSS 即时隐藏(在 body 渲染前就生效, 避免元素闪一下再消失) ============
  var CSS = [
    // ===== 立刻隐藏(广告 / 弹幕列表 / 推荐 / 登录 / 笔记 / 顶部 / 直播) =====
    // 注: 保留 .bpx-player-dm-wrap(视频内飞行弹幕), native 工具栏「弹幕开关」按钮可切换显示
    '.danmaku-wrap, .bpx-player-dm-setting, .bpx-player-dm-icon-wrap, .bpx-player-dm-setting-wrap,',
    '.bpx-player-video-btn-dm, [class*=""dm-list""], [class*=""DanmakuList""],',

    '[class*=""recommend""], [class*=""Recommend""], [class*=""rec-list""], [class*=""RecList""],',
    '.recommend-list, .rec-footer, #recom_module,',
    '[class*=""popular-video""], [class*=""PopularVideo""], .popular-video,',
    '[class*=""special-card""], [class*=""SpecialCard""], .video-page-special-card,',
    '[class*=""floor-card""], [class*=""FloorCard""], [class*=""floor-banner""],',
    '.floor-single-card, .activity-banner, [class*=""activity-banner""],',

    '[class*=""login-tip""], [class*=""login-card""], [class*=""popup-login""], [class*=""LoginCard""],',
    '.bili-mini-login, .v-login-wrap, .login-tips, .login-tip, .popup-login,',
    '.login-panel-popup, [class*=""login-panel""],',

    '[class*=""video-note""], [class*=""VideoNote""], [class*=""note-card""],',

    '[class^=""ad-""], [class*=""-ad-""], [class*=""_ad_""],',
    '[id^=""ad-""], [id*=""-ad-""],',
    '.ad-report, .video-card-ad-info, [class*=""ad-report""],',

    '.bili-header, [class*=""bili-header""], .bili-footer, [class*=""bili-footer""],',
    '.nav-bar-wrap, .fixed-header, [class*=""nav-bar""],',

    '[class*=""live-card""], [class*=""LiveCard""], [class*=""live-player""], [class*=""LivePlayer""],',

    '[class*=""bpx-player-mini-warn""], [class*=""MiniWarn""],',
    '[class*=""bpx-player-issue""], [class*=""IssueBanner""],',

    '{ display:none !important; visibility:hidden !important; }',

    // ===== 视觉美化(不删元素, 只是改外观) =====
    '.video-container { max-width: 1400px !important; margin: 0 auto !important; padding: 12px 24px !important; }',
    'h1.video-title, .video-info-title { font-size: 18px !important; font-weight: 600 !important; }',
    '.video-info-container { padding: 12px 0 !important; border-bottom: 1px solid rgba(128,128,128,0.15) !important; }',
    '.video-toolbar { padding: 12px 0 !important; }',
    '.up-panel-container { margin-top: 16px !important; padding: 12px !important; border-radius: 10px !important;',
    '  background: rgba(128,128,128,0.06) !important; }',
    '.comment-container, .bili-comments, [class*=""CommentArea""] { margin-top: 24px !important; }',

    // 让右侧栏(保留 UP主+合集后)与左侧内容间距更自然
    '.right-container { padding-left: 16px !important; }',
    '.left-container { min-width: 0 !important; }',

    // ===== 右侧栏白名单: 只保留 UP 主卡片 + 合集, 其他(推荐/广告/直播)全部隐藏 =====
    '.right-container > *:not([class*=""up-""]):not([class*=""Up-""]):not([class*=""pugc""]):not([class*=""Pugc""]):not([class*=""episode""]):not([class*=""Episode""]):not([class*=""section""]),',
    '[class*=""right-container""] > *:not([class*=""up-""]):not([class*=""Up-""]):not([class*=""pugc""]):not([class*=""Pugc""]):not([class*=""episode""]):not([class*=""Episode""]):not([class*=""section""]),',
    '{ display:none !important; visibility:hidden !important; }',

    // ===== 播放器内部广告/弹窗/充电面板 =====
    '.bpx-player-popup, .bpx-player-electric-panel, .bpx-player-block-image,',
    '.bpx-player-toast-wrap, .bpx-player-mini-warn, .bpx-player-error-panel,',
    '.bpx-player-video-info, .bpx-player-promotion, [class*=""promotion-img""],',
    '[class*=""bpx-player-float""], [class*=""float-ball""], [class*=""float-layer""],',
    '[class*=""bpx-player-activity""], [class*=""ActivityBanner""],',

    // ===== 暂停/结束时的推荐视频 + 「进入哔哩哔哩」按钮 + 已静音开播提示 =====
    '.bpx-player-link, .bpx-player-recommend, .bpx-player-end-recommend,',
    '.bpx-player-pause-recommend, .bpx-player-recommend-bottom,',
    '.bpx-player-toast, [class*=""bpx-player-link""], [class*=""bpx-player-recommend""],',
    '[class*=""bpx-player-toast""], [class*=""next-video""], [class*=""NextVideo""],',
    '.bpx-player-video-info, .bpx-player-video-info-detail,',
    '{ display:none !important; visibility:hidden !important; }'
  ].join('\n');

  var STYLE_ID = 'bilidesk-cleaner';
  function injectCss(){
    try {
      var root = document.documentElement;
      if (!root) return;
      // 我们的 style 始终放在 root 最前面, 拥有最高优先级
      var old = document.getElementById(STYLE_ID);
      if (old) old.parentNode.removeChild(old);
      var s = document.createElement('style');
      s.id = STYLE_ID;
      s.textContent = CSS;
      root.insertBefore(s, root.firstChild);
    } catch(e) { /* 静默 */ }
  }

  // 文档一存在就立即注入(早于 body 渲染)
  injectCss();

  // documentElement 子元素变化时, 如果我们的 style 被移除就重注入
  // (B 站可能在 SPA 切换时清空 head)
  if (window.MutationObserver) {
    var obsCss = new MutationObserver(function(muts){
      for (var i = 0; i < muts.length; i++) {
        var m = muts[i];
        if (m.type !== 'childList') continue;
        var removed = false;
        for (var j = 0; j < m.removedNodes.length; j++) {
          if (m.removedNodes[j] && m.removedNodes[j].id === STYLE_ID) { removed = true; break; }
        }
        if (removed || !document.getElementById(STYLE_ID)) {
          injectCss();
          return;
        }
      }
    });
    obsCss.observe(document.documentElement, { childList: true });
  }

  // ============ 处理 JS 动态加载的内容(已用 CSS 隐藏, 这里做兜底彻底删除) ============
  function nukeDynamic(){
    try {
      var sels = [
        '[class*=""recommend""]', '[class*=""Recommend""]',
        '[class*=""popular-video""]', '[class*=""popular-video""]',
        '[class*=""special-card""]', '[class*=""SpecialCard""]',
        '[class*=""floor-card""]', '[class*=""floor-banner""]',
        '[class*=""login-tip""]', '[class*=""login-card""]', '[class*=""popup-login""]',
        '[class*=""video-note""]', '[class*=""VideoNote""]',
        '[class^=""ad-""], [class*=""-ad-""], [class*=""_ad_""],',
        '[class*=""live-card""]', '[class*=""LiveCard""]',
        '.bili-header', '.bili-footer', '.nav-bar-wrap'
      ].join(',');
      var arr = document.querySelectorAll(sels);
      for (var i = 0; i < arr.length; i++) {
        var e = arr[i];
        if (e && e.id !== STYLE_ID && e.parentNode) e.parentNode.removeChild(e);
      }

      // 工具栏右侧: 隐藏稍后看 / 举报 / 笔记 三个按钮
      var toolbars = document.querySelectorAll('.video-toolbar, [class*=""VideoToolbar""], [class*=""video-toolbar""]');
      for (var t = 0; t < toolbars.length; t++) {
        var tb = toolbars[t];
        var rights = tb.querySelectorAll('[class*=""toolbar-right""], [class*=""ToolbarRight""]');
        for (var r = 0; r < rights.length; r++) {
          var rt = rights[r];
          var kids = rt.children;
          for (var k = kids.length - 1; k >= 0; k--) {
            var c = kids[k];
            var txt = (c.textContent || '').replace(/\s/g, '');
            if (txt.indexOf('稍后看') >= 0 || txt.indexOf('举报') >= 0 || txt.indexOf('笔记') >= 0) {
              if (c.parentNode) c.parentNode.removeChild(c);
            }
          }
        }
      }

      // 右侧栏: 保留 UP 主卡片 + 合集, 其余(推荐/广告)删除
      var rightContainers = document.querySelectorAll('.right-container, [class*=""right-container""], [class*=""RightContainer""]');
      for (var rc = 0; rc < rightContainers.length; rc++) {
        var container = rightContainers[rc];
        var rkids = container.children;
        for (var rk = rkids.length - 1; rk >= 0; rk--) {
          var kid = rkids[rk];
          var kcls = (kid.className && kid.className.baseVal !== undefined ? kid.className.baseVal : kid.className) || '';
          // 白名单: UP 主面板 + 合集 + 分集列表
          if (/\b(up|pugc|episode|section)\b/i.test(kcls)) continue;
          if (kid.parentNode) kid.parentNode.removeChild(kid);
        }
      }

      // 播放器内部弹窗/广告
      var popups = document.querySelectorAll(
        '.bpx-player-popup, .bpx-player-electric-panel, .bpx-player-block-image, ' +
        '.bpx-player-toast-wrap, [class*=""bpx-player-promotion""], [class*=""float-ball""], ' +
        '[class*=""float-layer""], [class*=""bpx-player-activity""]');
      for (var p = 0; p < popups.length; p++) {
        var el = popups[p];
        if (el && el.parentNode) el.parentNode.removeChild(el);
      }

      // UP 主空间页: 隐藏除 [动态] [投稿] 外的所有 tab
      var host = location.host || '';
      if (host.indexOf('space.bilibili.com') >= 0) {
        var tabSelectors = '#navigator a, [class*=""navigator""] a, [class*=""nav-tab""], [class*=""Navigator""] a';
        var upTabs = document.querySelectorAll(tabSelectors);
        for (var u = 0; u < upTabs.length; u++) {
          var t = upTabs[u];
          var txt = (t.textContent || '').replace(/\s/g, '');
          // 保留: 动态、投稿/视频; 隐藏: 主页/收藏/追番/小店 等
          if (txt.indexOf('动态') >= 0 || txt.indexOf('投稿') >= 0 || txt.indexOf('视频') >= 0) continue;
          if (txt.length > 0 && txt.length < 10) {
            if (t.parentElement && t.parentElement.tagName === 'LI') {
              t.parentElement.style.display = 'none';
            } else {
              t.style.display = 'none';
            }
          }
        }
      }
    } catch(e) { /* 静默 */ }
  }

  // 等 body 出现后开始清理 + 持续节流清理
  function startObserver(){
    if (!window.MutationObserver) return;
    var scheduled = false;
    var obs = new MutationObserver(function(){
      if (scheduled) return;
      scheduled = true;
      setTimeout(function(){ scheduled = false; nukeDynamic(); }, 150);
    });
    if (document.body) obs.observe(document.body, { childList: true, subtree: true });
  }

  if (document.body) startObserver();
  else {
    document.addEventListener('DOMContentLoaded', function(){
      injectCss();
      nukeDynamic();
      startObserver();
    });
    document.addEventListener('readystatechange', function(){
      if (document.body) startObserver();
    });
  }
})();
";

    public void ApplyColorScheme(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        if (core == null) return;
        try
        {
            core.Profile.PreferredColorScheme =
                ThemeService.Instance.IsDark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
        }
        catch
        {
            // 旧版运行时可能不支持, 忽略
        }
    }

    /// <summary>读取 bilibili 域下的 Cookie(名称 -> 值)</summary>
    public static async Task<Dictionary<string, string>> ReadBiliCookiesAsync(WebView2 webView)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var core = webView.CoreWebView2;
            if (core == null) return result;

            var cookies = await core.CookieManager.GetCookiesAsync("https://www.bilibili.com");
            foreach (var c in cookies)
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                result[c.Name] = c.Value ?? "";
            }
        }
        catch
        {
            // 忽略
        }
        return result;
    }

    /// <summary>清空浏览器 Profile 中全部 Cookie(退出登录用)。隐藏控件需先显示再初始化</summary>
    public async Task ClearProfileCookiesAsync(WebView2 webView)
    {
        try
        {
            webView.Visibility = System.Windows.Visibility.Visible;
            await InitAsync(webView);
            webView.CoreWebView2.CookieManager.DeleteAllCookies();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            webView.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}