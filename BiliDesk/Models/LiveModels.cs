using System.Collections.Generic;
using System.ComponentModel;

namespace BiliDesk.Models;

/// <summary>
/// 直播父分区(网游 / 手游 / 单机游戏 / 娱乐 …)。
///
/// ★ 带 <see cref="IsSelected"/> 的变更通知: 分区胶囊的选中态是**从代码侧**改的
///   (点一个就把上一个的选中清掉), 没有 INPC 时界面不会跟着变 —— 表现就是"点哪个都不亮"。
/// </summary>
public class LiveAreaGroup : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; init; }
    public string Name { get; init; } = "";
    public List<LiveArea> Children { get; } = new();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>直播子分区(英雄联盟 / CS2 / 三角洲行动 …)。<see cref="Id"/> = 0 表示「全部」。</summary>
public class LiveArea : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; init; }
    public int ParentId { get; init; }
    public string Name { get; init; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
