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
            // ContextMenuStrip 在 WPF 宿主里默认不开视觉样式, 菜单会是 Win2000 观感
            System.Windows.Forms.Application.EnableVisualStyles();

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Text = "BiliDesk",
                Visible = true,
                Icon = LoadAppIcon()
            };
            _icon.DoubleClick += (_, _) => ShowMainWindow();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
            menu.Items.Add("最小化到托盘", null, (_, _) => MinimizeToTray());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _icon.ContextMenuStrip = menu;
        }
        catch (Exception ex)
        {
            // 托盘是锦上添花: 初始化失败只记日志, 绝不能挡住启动
            App.ReportError(ex);
            Dispose();
        }
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

    /// <summary>已经给过"收进托盘"的气泡提示(整次运行只给一次, 免得每次关闭都弹)</summary>
    private bool _balloonShown;

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
            NotifyMinimizedToTray();
        }));
    }

    /// <summary>
    /// 收起后的气泡提示(整次运行只给一次)。
    /// 主窗口自己 Hide 的那条路径(MainWindow.OnClosing)会直接调这里 ——
    /// 那种情况下窗口已经藏好了, 只差一个提示。
    /// </summary>
    public void NotifyMinimizedToTray()
    {
        // 没有这个提示, 用户点完关闭键会觉得"程序被关了", 然后去任务管理器里找进程 ——
        // 一个气泡就能省掉这次困惑。
        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _icon?.ShowBalloonTip(3000, "BiliDesk 仍在后台运行",
                "双击托盘图标可以恢复主界面, 右键菜单可退出。", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 有些系统策略会关掉气泡, 静默忽略
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
