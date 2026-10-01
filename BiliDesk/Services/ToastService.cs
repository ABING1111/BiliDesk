using System;

namespace BiliDesk.Services;

/// <summary>轻量 Toast 通知(主窗口右上角浮动提示)</summary>
public class ToastService
{
    public static ToastService Instance { get; } = new();

    public event Action<string>? Shown;

    public void Show(string message) => Shown?.Invoke(message);
}