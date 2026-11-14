using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 最大化时根内容的四边内缩量(DIP)。四个值都 ≥ 0。
/// </summary>
public readonly record struct WindowInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// 计算"最大化窗口矩形"与"可见目标矩形"之间的差值, 供根内容做内缩补偿。
///
/// ★★★ 为什么需要它(2026-11-14, 用户报"全屏后窗口边缘显示少了一点点"):
///   Windows 执行最大化时, 会把**带缩放边框的窗口**撑成"工作区 + 每边一条不可见边框"
///   (本机 1920x1080@125% 实测: 工作区 1920x1020, 最大化窗口 (-9,-9)-(1929,1029))。
///   对普通窗口无所谓 —— 那条边框由系统非客户区吃掉, 客户区仍是工作区;
///   但本工程是 `WindowStyle=None + WindowChrome`, WindowChrome 在 WM_NCCALCSIZE 里
///   把非客户区压成 0 ⇒ **客户区 = 整个窗口矩形**。于是 WPF 的根内容比工作区每边大
///   7.2 DIP(9 物理像素/1.25), 最外圈那一条正好落在屏幕外 —— 用户看到的就是
///   "四周各少了一点点"。
///   ★ 播放器全屏例外: 它在最大化前摘掉了 WS_CAPTION, 系统会把窗口精确摆到整块显示器上,
///     所以这里算出来是 0; 这条通路保留只为兜住系统版本/显示配置的差异。
///
/// ★ 这里的做法: 窗口几何一概不动(改了也会被 WPF 自己的最大化逻辑压回去),
///   只把这圈多出来的量算出来, 由 FluentWindow 加到根内容的 Margin 上 ——
///   内容正好落在可见区域里, 坐标原点仍是屏幕 (0,0)。
///
/// ★ 输入全部是**物理像素**; 输出除以 DPI 缩放换成 DIP(与 WPF 的 Margin 同一单位)。
///   负值一律夹成 0: 目标比窗口大属于异常输入(或不需要补偿的状态), 绝不能反向外扩。
///
/// ★ 必须是纯函数(不碰 WPF 类型): 测试工程明确不引用 WPF, 只测纯逻辑。
/// </summary>
public static class WindowMaximizeInset
{
    public static WindowInsets Compute(
        int windowLeft, int windowTop, int windowRight, int windowBottom,
        int targetLeft, int targetTop, int targetRight, int targetBottom,
        double scaleX, double scaleY)
    {
        // DPI 拿不到/异常时按 1:1 处理 —— 少补一点总比抛异常让窗口起不来强
        if (!IsValidScale(scaleX)) scaleX = 1;
        if (!IsValidScale(scaleY)) scaleY = 1;

        return new WindowInsets(
            Inset(targetLeft - windowLeft, scaleX),
            Inset(targetTop - windowTop, scaleY),
            Inset(windowRight - targetRight, scaleX),
            Inset(windowBottom - targetBottom, scaleY));
    }

    private static bool IsValidScale(double scale)
        => scale > 0 && double.IsFinite(scale);

    private static double Inset(int physicalPixels, double scale)
        => physicalPixels <= 0 ? 0 : physicalPixels / scale;
}
