using System.Collections.Generic;
using System.Reflection;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 离线缓存页顶部的"缓存位置: …"(2026-11-21)。
///
/// 这一行绑的是 <see cref="CacheViewModel.CacheDirectory"/> —— 只读计算属性,
/// 只在被通知时才会重新求值。页面实例被主窗口长期缓存, 设置里改完下载位置后
/// 若不抛 PropertyChanged, 界面上就会一直挂着 C 盘的旧路径。
/// 这里用反射触发 SettingsStore.DownloadDirChanged, 把"必须通知"这件事钉死。
/// </summary>
public class CacheViewModelTests
{
    [Fact]
    public void 下载位置变化_缓存位置文本要通知()
    {
        var vm = new CacheViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        RaiseDownloadDirChanged();

        Assert.Contains(nameof(CacheViewModel.CacheDirectory), changed);
    }

    /// <summary>字段式事件没有公开的触发方法, 反射取委托直接调用(不落盘, 不碰用户设置)。</summary>
    private static void RaiseDownloadDirChanged()
    {
        var field = typeof(SettingsStore).GetField(
            nameof(SettingsStore.DownloadDirChanged),
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handler = (System.Action?)field!.GetValue(SettingsStore.Instance);
        handler?.Invoke();
    }
}
