using System;
using System.Collections.Generic;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 设置页「快捷键」Tab 里的一行(2026-11-28)。
///
/// ★ 值不缓存在本地字段里, 而是每次直接从 ShortcutService 的生效映射读:
///   改绑成功后由 <see cref="SettingsViewModel"/> 统一通知所有行, 避免"这一行改了、
///   与它冲突/让出默认键的另一行还显示旧值"。
/// </summary>
public sealed class ShortcutItemViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private readonly RelayCommand _resetCommand;

    public ShortcutItemViewModel(SettingsViewModel owner, ShortcutDefinition definition, bool isFirst)
    {
        _owner = owner;
        Definition = definition;
        IsFirst = isFirst;
        _resetCommand = new RelayCommand(
            () => _owner.ResetShortcut(this),
            canExecute: () => !IsDefault);
    }

    public ShortcutDefinition Definition { get; }

    public ShortcutAction Action => Definition.Action;

    public string Title => Definition.Title;

    public string Description => Definition.Description;

    /// <summary>分组里的第一行不画顶部细分隔线</summary>
    public bool IsFirst { get; }

    /// <summary>是否仍是默认键(改过 / 清空过都为 false)</summary>
    public bool IsDefault => Svc.Shortcuts.IsDefault(Action);

    /// <summary>
    /// 当前绑定(规范格式)。TwoWay 绑定到 ShortcutBox。
    /// setter 里做三件事: 校验 + 落盘 + 重建映射(Svc.Shortcuts.TryAssign 一并完成);
    /// 失败时显式发一次变更通知, 让绑定把控件上的新组合刷回原值。
    /// </summary>
    public string Gesture
    {
        get => Svc.Shortcuts.GestureOf(Action).ToCanonicalString();
        set
        {
            if (string.Equals(value, Gesture, StringComparison.Ordinal)) return;

            if (!_owner.TryAssignShortcut(this, value, out var error))
            {
                if (!string.IsNullOrEmpty(error)) Svc.Toast.Show(error);
                // ★ 必须显式推: ViewModel 的值没有变, 但 TwoWay 绑定已经把控件写成新值了。
                //   不发通知的话, 被拒绝的组合会留在框里, 用户会以为改成功了。
                OnPropertyChanged();
                return;
            }
        }
    }

    public ICommand ResetCommand => _resetCommand;

    /// <summary>改绑/重置后刷新本行显示(由父 VM 统一调用)</summary>
    public void RaiseGestureChanged()
    {
        OnPropertyChanged(nameof(Gesture));
        OnPropertyChanged(nameof(IsDefault));
        _resetCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>设置页「快捷键」Tab 的一个分组(卡片 + 组标题)</summary>
public sealed class ShortcutGroupViewModel
{
    public ShortcutGroupViewModel(string title, IReadOnlyList<ShortcutItemViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }

    public IReadOnlyList<ShortcutItemViewModel> Items { get; }
}
