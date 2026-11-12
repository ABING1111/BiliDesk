using System;
using System.Windows;

namespace BiliDesk.Services;

/// <summary>
/// 系统托盘图标(基于 WinForms NotifyIcon)。
///
/// 职责边界:
///   - 双击托盘: 显示/激活主界面;
///   - 右键菜单: 显示主界面 / 最小化到托盘 / 退出;
///   - 主窗口的关闭键默认就是"收进托盘"(设置里「关闭时最小化到托盘」默认打开,
///     关掉之后关闭键才是真的退出程序) —— 判断在 MainWindow.OnClosing 里,
///     这里只提供"收起来"和"真退出"两个动作。
///
/// 两个容易踩的点:
///   1. NotifyIcon 的事件都在自己的消息窗口线程上触发, 回调里必须用 Dispatcher 派发回
///      UI 线程再碰 WPF 对象;
///   2. 退出前必须 Dispose(并 Visible=false), 否则图标会以"幽灵"形式留在托盘区,
///      鼠标划过才消失 —— 那是 Explorer 还缓存着没人认领的 NOTIFYICONDATA。
/// </summary>
public class TrayService : IDisposable
{
    public static TrayService Instance { get; } = new();

    private System.Windows.Forms.NotifyIcon? _icon;
    private bool _disposed;

    public void Initialize()
    {
        if (_disposed || _icon != null) return;
        try
        {
            // NotifyIcon 的图标/气泡渲染仍需要 WinForms 的视觉样式(与下面的 WPF 菜单无关)
            System.Windows.Forms.Application.EnableVisualStyles();

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Text = "BiliDesk",
                Visible = true,
                Icon = LoadAppIcon()
            };
            _icon.DoubleClick += (_, _) => ShowMainWindow();
            // ★ 右键菜单改用 WPF 的 ContextMenu(主题里有全局 ContextMenu/MenuItem 样式):
            //   WinForms 的 ContextMenuStrip 是系统旧风格, 与应用内其它界面明显不一致。
            _icon.MouseUp += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowTrayMenu();
            };
        }
        catch (Exception ex)
        {
            // 托盘是锦上添花: 初始化失败只记日志, 绝不能挡住启动
            App.ReportError(ex);
            Dispose();
        }
    }

    private System.Windows.Controls.ContextMenu? _trayMenu;

    /// <summary>
    /// 在鼠标位置弹出 WPF 托盘菜单(主题样式来自 Themes/Styles.xaml 的全局 ContextMenu 样式)。
    /// ★ 必须在 UI 线程构造/打开菜单: NotifyIcon 的事件跑在它自己的消息线程上。
    /// </summary>
    private void ShowTrayMenu()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            _trayMenu ??= BuildTrayMenu();
            _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _trayMenu.IsOpen = true;
        }));
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(MakeTrayItem("显示主界面", ShowMainWindow));
        menu.Items.Add(MakeTrayItem("最小化到托盘", MinimizeToTray));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MakeTrayItem("退出", ExitApp));
        return menu;
    }

    private static System.Windows.Controls.MenuItem MakeTrayItem(string header, Action onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>图标直接从 exe 里抽(ApplicationIcon 就是 app.ico), 不用关心 pack URI 是否打包了 ico</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null; // 抽不到就显示默认空白图标, 功能不受影响
        }
    }

    /// <summary>
    /// 把主窗口显示到前台(托盘双击 / 用户再次双击 exe 两处共用)。
    /// ★ 必须是 public: 单实例的"请显形"通知(App.OnStartup 里订阅)也要复用它 ——
    ///   两处各写一份必然出现"托盘能叫出来、双击 exe 叫不出来"这种不一致。
    /// </summary>
    public void ShowMainWindow()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            // SvcWindow.Main 是我们显式登记的主窗口, 比 Application.MainWindow 可靠
            // (后者可能还指着早已关闭的免责声明窗口)
            if (SvcWindow.Main is not Window win) return;
            if (win.WindowState == WindowState.Minimized)
                win.WindowState = WindowState.Normal;
            win.Show();
            win.Activate();
        }));
    }

    /// <summary>
    /// 把主窗口收进托盘(窗口 Hide, 进程继续跑)。
    ///
    /// 两条路径都走这里: 点右上角关闭键(MainWindow.OnClosing 在"关闭到托盘"开启时调),
    /// 以及设置页/托盘菜单里的「最小化到托盘」按钮 —— 行为必须完全一致, 否则会出现
    /// "按钮点一下收得进去, 关闭键收不进去"这种自相矛盾的表现。
    /// </summary>
    public void MinimizeToTray()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var win = SvcWindow.Main;
            if (win == null) return;
            win.Hide();
        }));
    }

    /// <summary>
    /// 发一条**系统通知**(托盘气泡)。
    ///
    /// ★ 为什么用它而不是再做一套通知: 项目已经为了"关闭到托盘"持有 WinForms 的
    ///   NotifyIcon(见类注释), 气泡就是它自带的能力, 复用不会多占一个托盘图标。
    ///   再引一套 UWP toast / CommunityToolkit 只为了这一条通知不划算。
    ///
    /// ★ 必须在托盘初始化过之后调才有用(<see cref="Initialize"/> 失败时 _icon 为 null)。
    ///   失败一律静默 —— 通知是锦上添花, 绝不能因为它把下载流程带崩。
    /// </summary>
    /// <param name="title">标题, 建议 ≤ 40 字(气泡标题超长会被系统截断)</param>
    /// <param name="message">正文, 建议 ≤ 80 字</param>
    /// <param name="icon">图标类型, 默认 Info</param>
    public void Notify(string title, string message,
        System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
    {
        try
        {
            _icon?.ShowBalloonTip(5000, title, message, icon);
        }
        catch
        {
            // 系统策略关掉气泡 / 托盘未初始化: 静默忽略(应用内 Toast 已经给过反馈了)
        }
    }

    private void ExitApp()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            // 标记"真的要退出": 关闭键那条"收进托盘"的分支靠它让路, 否则 Shutdown 会被
            // MainWindow.OnClosing 拦下来, 用户点了退出却什么都没发生。
            App.IsExiting = true;
            Dispose(); // 先摘图标再退出, 别给托盘区留幽灵
            app.Shutdown();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
        }
        catch { /* 退出阶段忽略 */ }
        _icon = null;
    }
}
