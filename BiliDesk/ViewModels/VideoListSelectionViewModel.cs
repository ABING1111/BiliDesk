using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「视频列表 + 编辑模式(多选 / 全选 / 批量操作)」的公共底座。
///
/// 历史、收藏、稍后再看三个页面的这套逻辑原本是**逐字复制的三份**: 选中态派生属性、
/// 批量改选时压住逐条通知、集合增删时挂/退每一条的 PropertyChanged。
///
/// 复制的代价已经现形了 —— 三份慢慢长歪:
///   · 历史 / 收藏 的"全选"基数是**可见条数**(被全局屏蔽或本页搜索筛掉的条目用户看不见,
///     不该被"全选"顺带删掉), 稍后再看那份却按**原集合**计数, 全选按钮点一下会把
///     看不见的条目也选中(那个页面目前还没接屏蔽, 所以今天看不出来; 一旦接上就是 bug)。
///   · 收藏页在集合增删时还要顺带刷一次顶栏计数, 另外两份没有。
/// 收进基类之后, "全选的基数到底是什么"只剩一处定义, 不会再各自漂移。
///
/// 子类要做的只有两件: 交出 <see cref="SelectionSource"/>(我管这个集合),
/// 以及在 <see cref="RaiseSelectionExtra"/> 里补自己特有的绑定属性。
/// </summary>
public abstract class VideoListSelectionViewModel : ObservableObject
{
    /// <summary>
    /// 本页面参与选择管理的集合(历史叫 Entries, 收藏/稍后再看叫 Items)。
    ///
    /// 抽象属性而不是构造注入: 这些集合在子类里都是 `public ... { get; } = new()` 的
    /// 字段初始化器, 基类构造函数先于子类字段初始化器执行, 构造注入根本拿不到。
    /// 所以改为由子类在构造函数末尾调 <see cref="HookSelectionSource"/> 完成挂接。
    /// </summary>
    protected abstract ObservableCollection<VideoItem> SelectionSource { get; }

    /// <summary>
    /// 当前屏蔽条件下真正可见的条目 —— 全选 / 删除所选一律以它为准。
    ///
    /// 走 <see cref="FilterService.Visible{T}"/> 而不是原集合: 被屏蔽掉的条目用户
    /// 根本看不见, 却因为"全选"被一起选中/删除, 是最典型的"我明明没选它"。
    /// </summary>
    protected List<VideoItem> VisibleItems() => FilterService.Visible(SelectionSource).ToList();

    /// <summary>可见条数(已叠加全局屏蔽与页面自己的搜索)</summary>
    public int TotalCount => VisibleItems().Count;

    /// <summary>已选条数</summary>
    public int SelectedCount => VisibleItems().Count(x => x.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// 是否已经全选。
    ///
    /// ★ 基数必须是**可见项**(TotalCount), 不能是 SelectionSource.Count: 一旦有条目
    ///   被全局屏蔽或被本页搜索筛掉, SelectedCount 就永远追不平原集合的条数, 于是
    ///   AllSelected 恒为 false —— 按钮一直显示"全选"、点它只是把可见项**再选一遍**,
    ///   用户根本取消不掉(2026-09-30 在历史页踩过并修掉, 当时收藏页用的是同一个写法)。
    /// </summary>
    public bool AllSelected => TotalCount > 0 && SelectedCount == TotalCount;

    public string SelectedText => SelectedCount > 0 ? $"已选 {SelectedCount} 项" : "未选择";

    /// <summary>全选按钮的文案。已全选时点它就是取消, 所以文案要跟着变。</summary>
    public string SelectAllText => AllSelected ? "取消全选" : "全选";

    /// <summary>批量改选期间的静默标志: 压住逐条通知, 结束后统一报一次。</summary>
    private bool _bulkSel;

    /// <summary>
    /// 批量修改 IsSelected, 期间只在结束时通知一次。
    ///
    /// 不压住的话, 每翻一条都会重算一遍 <see cref="VisibleItems"/> 并触发一串绑定刷新,
    /// 几百条就是几万次谓词调用。
    /// </summary>
    protected void SetSelectionBulk(IEnumerable<VideoItem> items, bool value)
    {
        _bulkSel = true;
        try
        {
            foreach (var it in items) it.IsSelected = value;
        }
        finally
        {
            _bulkSel = false;
        }
        RaiseSelection();
    }

    /// <summary>取消全选(退出编辑模式时用)</summary>
    protected void ClearSelection() => SetSelectionBulk(SelectionSource, false);

    /// <summary>
    /// 开始监听选择集合的增删。**子类构造函数末尾必须调一次**(集合字段就绪之后)。
    ///
    /// 为什么不在基类构造函数里直接订阅: 见 <see cref="SelectionSource"/> 的说明 ——
    /// 那时子类的集合字段还是 null。
    /// </summary>
    protected void HookSelectionSource() => SelectionSource.CollectionChanged += OnSourceChanged;

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 逐条挂/退 PropertyChanged: 集合自己不会因为 IsSelected 变化而发通知,
        // 只能靠监听每个 VideoItem 自己发出来的那个。
        if (e.OldItems != null)
            foreach (var it in e.OldItems.OfType<VideoItem>()) it.PropertyChanged -= OnItemChanged;
        if (e.NewItems != null)
            foreach (var it in e.NewItems.OfType<VideoItem>()) it.PropertyChanged += OnItemChanged;

        OnSourceItemsChanged();
        RaiseSelection();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoItem.IsSelected)) return;
        if (_bulkSel) return;   // 批量改选中: 结束时会统一报一次
        RaiseSelection();
    }

    /// <summary>集合增删后、刷新选择态之前的一次挂钩。默认什么都不做。</summary>
    protected virtual void OnSourceItemsChanged() { }

    /// <summary>刷新选择态相关的全部绑定属性。</summary>
    protected void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SelectAllText));
        RaiseSelectionExtra();
    }

    /// <summary>子类补充自己要通知的属性(搜索命中数、空态提示、顶栏计数等)。默认无。</summary>
    protected virtual void RaiseSelectionExtra() { }
}
