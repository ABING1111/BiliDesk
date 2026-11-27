using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 设置页「快捷键」Tab 里的按键录制框(2026-11-28)。
///
/// 交互:
///   · 点一下 / 聚焦后按 Space/Enter → 进入录制, 显示「请按下新的快捷键…」;
///   · 按下任意组合键 → 写回 <see cref="Gesture"/> 并退出录制;
///   · Esc → 取消录制(不改动);
///   · Backspace / Delete → 清空绑定(存空串, 显示「未设置」);
///   · 只按修饰键 → 继续等待主键;
///   · 系统/文本编辑保留组合(Ctrl+C 等) → 留在录制态并提示换一个, 不会静默失败。
///
/// ★ 为什么做成控件而不是在 DataTemplate 里挂事件: SettingsTabs.xaml 是纯 ResourceDictionary,
///   模板里挂不了 Click/KeyDown 处理器。把捕获逻辑收进控件后, 模板只写一行绑定。
/// ★ 值通过 TwoWay 依赖属性回写 ViewModel; ViewModel 校验冲突失败时会显式 Raise
///   PropertyChanged, 绑定随即把这里的新值刷回旧值 —— 所以被拒绝的组合不会留在界面上。
/// </summary>
public class ShortcutBox : Button
{
    /// <summary>当前是否有录制框处于"等待按键"态。主窗口的 F5 / Esc 处理器据此让路。</summary>
    public static bool IsAnyCapturing => _captureCount > 0;

    private static int _captureCount;

    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(ShortcutBox),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ShortcutBox)d).UpdateContent()));

    private static readonly DependencyPropertyKey IsCapturingPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsCapturing), typeof(bool), typeof(ShortcutBox),
            new PropertyMetadata(false, (d, _) => ((ShortcutBox)d).UpdateContent()));

    public static readonly DependencyProperty IsCapturingProperty = IsCapturingPropertyKey.DependencyProperty;

    /// <summary>规范格式的组合键("Ctrl+Shift+K"), 空串 = 未绑定。TwoWay 绑定到设置页的 item。</summary>
    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingPropertyKey, value);
    }

    /// <summary>录制中的临时提示(比如按了保留组合)</summary>
    private string _hint = "";

    public ShortcutBox()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        UpdateContent();
        // 页面切走 / Tab 切走时模板会卸载, 必须把静态计数还回去, 否则主窗口会一直以为在录制
        Unloaded += (_, _) => EndCapture();
    }

    protected override void OnClick()
    {
        base.OnClick();
        BeginCapture();
    }

    private void BeginCapture()
    {
        if (IsCapturing) return;

        _hint = "";
        IsCapturing = true;
        _captureCount++;
        Focus();
        Keyboard.Focus(this);
        UpdateContent();
    }

    private void EndCapture()
    {
        if (!IsCapturing) return;

        IsCapturing = false;
        _hint = "";
        _captureCount = Math.Max(0, _captureCount - 1);
        UpdateContent();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        EndCapture();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // 录制期间所有按键都归这里管: 不能让 Button 把 Space/Enter 当成"再点一次",
        // 也不能让冒泡阶段的主窗口处理器抢走 F5 / Esc。
        e.Handled = true;

        var key = ShortcutService.RealKey(e);

        // 只按了修饰键: 继续等主键(否则 Ctrl 一按下就被录成一个无效组合)
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            // 空串 = 明确"清空绑定"; 与"恢复默认"是两件事(见 ShortcutService.TryAssign)
            SetCurrentValue(GestureProperty, "");
            EndCapture();
            return;
        }

        if (key == Key.None) return;

        if (!ShortcutService.IsAllowedKey(key))
        {
            _hint = "这个键不支持, 请换一个";
            UpdateContent();
            return;
        }

        var gesture = ShortcutService.Instance.ToGesture(key, Keyboard.Modifiers);
        if (ShortcutGesture.IsReserved(gesture))
        {
            _hint = "系统保留组合, 请换一个";
            UpdateContent();
            return;   // 留在录制态, 用户可以直接再按
        }

        SetCurrentValue(GestureProperty, gesture.ToCanonicalString());
        EndCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 双保险: 即使平台把 PreviewKeyDown 放行给基类, 录制期间也不能触发 Click
        if (IsCapturing)
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsCapturing)
        {
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    private void UpdateContent()
    {
        if (IsCapturing)
        {
            Content = string.IsNullOrEmpty(_hint) ? "请按下新的快捷键…" : _hint;
            return;
        }

        Content = ShortcutGesture.TryParse(Gesture, out var gesture)
            ? gesture.ToDisplayString()
            : "未设置";
    }
}
