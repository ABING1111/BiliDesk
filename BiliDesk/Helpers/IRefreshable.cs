namespace BiliDesk.Helpers;

/// <summary>
/// 页面支持 F5 刷新(2026-11-12 用户要求"所有的刷新绑定到 F5")。
/// 各页自己决定刷哪一块: 首页刷当前 Tab 并滚回顶部, 我的页强制重拉收藏夹……
/// 主窗口只把 F5 路由给当前可见的页面, 页面内部仍走原来那条刷新路径。
/// </summary>
public interface IRefreshable
{
    /// <summary>按 F5 时调用: 等价于点该页右上角的刷新按钮。</summary>
    void Refresh();
}
