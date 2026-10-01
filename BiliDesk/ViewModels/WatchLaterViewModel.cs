using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「稍后再看」页 ViewModel(2026-09-28 新增)。
///
/// 数据全部来自 B 站账号的云端「稍后再看」(`Svc.Api.GetWatchLaterAsync`), 本地不存一份 ——
/// 和历史/收藏一样, 跟着账号走才不会两头对不上。
///
/// ★ **展示是分页 + 分帧的**, 不是一口气全铺出来(2026-09-28 改):
///   这个清单可以有两三百条(实测某账号 249 条), 而列表是 ItemsControl + WrapPanel、
///   **没有任何虚拟化** —— 249 张 VideoCard 在一次布局里同步生成, 表现就是"进页面卡死一下";
///   更糟的是每张卡片还攥着一张解码后的封面位图, 内存直接上百 MB。
///   于是: 服务端一次拿全(避免多次请求), 但**每页只铺 60 张**(`PageSize`), 滚到底部再补下一页;
///   每一页内部又按每帧 24 张分帧插入(`InsertChunkSize`, 与首页 DeferredFill 同一个量级),
///   让"生成容器 + 解码封面"摊到多帧里去, 进页面立刻可见。
/// </summary>
public class WatchLaterViewModel : VideoListSelectionViewModel
{
    private bool _loading;
    private string _error = "";
    private bool _isEditMode;
    private bool _busy;
    private string _busyText = "";
    private int _total;

    /// <summary>服务端一次给全的完整清单; Items 只是它的"已展示部分"</summary>
    private readonly List<VideoItem> _all = new();

    /// <summary>已经(或正在排队)铺进 Items 的条数 —— 即 _all[0.._shown) 的展示游标</summary>
    private int _shown;

    /// <summary>每页铺多少张。60 张 ≈ 十几 MB 封面位图, 再多单次布局的容器生成就开始可感知。</summary>
    private const int PageSize = 60;

    /// <summary>分帧插入: 每帧最多补几张(与首页分帧填充同一个量级)。</summary>
    private const int InsertChunkSize = 24;

    /// <summary>待插入队列(恒等于 _all[_shown..] 里还没上屏的那段)。</summary>
    private readonly Queue<VideoItem> _pending = new();

    /// <summary>是否已排了一个补帧回调(避免重复排队)。</summary>
    private bool _pumpScheduled;

    public ObservableCollection<VideoItem> Items { get; } = new();

    /// <summary>卡片右键菜单里追加的「移出稍后再看」</summary>
    public ObservableCollection<CardMenuItem> CardMenuItems { get; } = new();

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>是否处于编辑模式(卡片点击变成勾选, 顶部换成操作条)</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value)) return;
            if (!value) ClearSelection();
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanOperate));
        }
    }

    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    /// <summary>标题右侧的条数(用服务端给的 count, 它才是权威值)</summary>
    public string CountText => _total > 0 ? $"· {_total}" : "";

    /// <summary>还有没铺完的页(滚动到底部时用来决定要不要补下一页)</summary>
    public bool HasMore => _shown < _all.Count;

    /// <summary>分帧插入进行中 —— 此时"内容不够一屏"是假象, 自动补页要暂停(与首页同一个坑)</summary>
    public bool IsFilling => _pending.Count > 0;

    public bool CanOperate => !Busy;

    // ------------------------------------------------------------ 选择状态
    // 选中态派生属性与批量改选、集合增删时挂退 PropertyChanged 的逻辑来自基类
    // VideoListSelectionViewModel(与历史页/收藏页共用同一份)。
    //
    // ★ 行为变化(修正): 原来这个页面的"全选"基数是**原集合**条数, 而历史/收藏是**可见条数**。
    //   三处现在统一按可见条数算。本页目前没接 FilterService(视图上没有 Filter 谓词,
    //   FilterService.Visible 会原样返回全部), 所以今天的实际表现完全一致;
    //   但一旦这个页面也接上屏蔽, 旧写法就会把看不见的条目一起选中/删掉。

    protected override ObservableCollection<VideoItem> SelectionSource => Items;

    // ------------------------------------------------------------ 分页 / 分帧

    /// <summary>再铺一页(每页 PageSize 张, 内部按帧插入)。滚动到底部时由页面调。</summary>
    public void ShowNextPage()
    {
        if (!HasMore) return;
        var n = Math.Min(PageSize, _all.Count - _shown);
        // 游标先推进: _pending 恒等于 _all[_shown..], 移除时按这个不变量重建
        for (var i = 0; i < n; i++) _pending.Enqueue(_all[_shown++]);
        SchedulePump();
    }

    /// <summary>把待插入队列按帧铺进 Items。</summary>
    private void SchedulePump()
    {
        if (_pumpScheduled) return;
        _pumpScheduled = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pumpScheduled = false;
            var n = Math.Min(InsertChunkSize, _pending.Count);
            for (var i = 0; i < n; i++) Items.Add(_pending.Dequeue());
            RaiseSelection();
            if (_pending.Count > 0) SchedulePump();
            else OnPropertyChanged(nameof(HasMore));
        }));
    }

    /// <summary>按当前游标重建待插入队列(移除/清空之后用 —— Queue 不支持随机删除)。</summary>
    private void RebuildPending()
    {
        _pending.Clear();
        for (var i = _shown; i < _all.Count; i++) _pending.Enqueue(_all[i]);
        OnPropertyChanged(nameof(HasMore));
    }

    /// <summary>从"完整清单 + 已展示列表 + 待插入队列"三处一起摘掉一条。</summary>
    private void RemoveEverywhere(VideoItem item)
    {
        var idx = _all.IndexOf(item);
        if (idx < 0) return;
        _all.RemoveAt(idx);
        // 已经铺出去的才需要从视图里摘; 还在队列里的直接随 RebuildPending 一起消失
        if (idx < _shown && Items.Remove(item)) _shown--;
        RebuildPending();
        OnPropertyChanged(nameof(CountText));
    }

    // ------------------------------------------------------------ 命令

    public ICommand ReloadCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearAllCommand { get; }

    public WatchLaterViewModel()
    {
        CardMenuItems.Add(new CardMenuItem
        {
            Header = "移出稍后再看",
            Glyph = "\uE74D",
            Command = new RelayCommand(p => { if (p is VideoItem v) _ = RemoveOneAsync(v); })
        });

        ReloadCommand = new RelayCommand(() => _ = LoadAsync());
        ToggleEditCommand = new RelayCommand(() => { if (!Busy) IsEditMode = !IsEditMode; });
        SelectAllCommand = new RelayCommand(() => { if (!Busy) SetSelectionBulk(VisibleItems(), !AllSelected); });
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync);
        ClearAllCommand = new AsyncRelayCommand(ClearAllAsync);

        // 分帧铺卡片时每加一条都要刷新选择态(计数与全选按钮文案), 由基类统一挂接
        HookSelectionSource();
    }

    // ------------------------------------------------------------ 加载

    /// <summary>整表重载(服务端一次给全, 没有分页); 展示侧只铺第一页, 其余滚动加载。</summary>
    public async Task LoadAsync()
    {
        if (Loading) return;

        if (!Svc.Session.HasLogin)
        {
            Reset();
            Error = "稍后再看需要登录后查看";
            return;
        }

        Loading = true;
        Error = "";
        IsEditMode = false;
        try
        {
            var (ok, err, items, count) = await Svc.Api.GetWatchLaterAsync();
            Reset();
            if (!ok || items == null)
            {
                Error = err ?? "加载失败";
                return;
            }
            _all.AddRange(items);
            _total = count;
            if (_all.Count == 0)
            {
                Error = "稍后再看是空的";
                return;
            }
            ShowNextPage();   // 只铺第一页, 其余滚动到底再补
        }
        finally
        {
            Loading = false;
            OnPropertyChanged(nameof(CountText));
            RaiseSelection();
        }
    }

    /// <summary>清空展示与数据(重载 / 退出登录共用)。</summary>
    private void Reset()
    {
        _all.Clear();
        _pending.Clear();
        _shown = 0;
        _total = 0;
        Items.Clear();
        Error = "";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasMore));
        RaiseSelection();
    }

    // ------------------------------------------------------------ 移除 / 清空

    /// <summary>右键菜单: 单条移出。成功才从列表移除, 失败保留(下次同步又冒出来更让人困惑)</summary>
    private async Task RemoveOneAsync(VideoItem item)
    {
        if (Busy || item == null) return;
        if (item.Aid <= 0)
        {
            // 列表接口没给 avid 时移不掉(接口只认 aid), 明说而不是静默失败
            Svc.Toast.Show("这条没有 avid, 移不掉");
            return;
        }

        Busy = true;
        BusyText = "正在移出…";
        try
        {
            var (ok, err) = await Svc.Api.RemoveWatchLaterAsync(item.Aid);
            if (!ok)
            {
                Svc.Toast.Show("移出失败: " + (err ?? "未知错误"));
                return;
            }
            RemoveEverywhere(item);
            if (_total > 0) _total--;
            if (_all.Count == 0) Error = "稍后再看是空的";
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }
    }

    /// <summary>编辑模式: 批量移出所选。逐条调接口(服务端只支持单条 aid), 成功一条移一条</summary>
    private async Task RemoveSelectedAsync()
    {
        if (Busy) return;
        var targets = Items.Where(x => x.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Svc.Toast.Show("还没有选择要移出的视频");
            return;
        }

        var r = MessageBox.Show($"确定要从「稍后再看」移出选中的 {targets.Count} 个视频吗?", "BiliDesk",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        Busy = true;
        var done = 0;
        var fail = 0;
        try
        {
            foreach (var it in targets)
            {
                if (it.Aid <= 0) { fail++; continue; }
                BusyText = $"正在移出 {done + fail + 1}/{targets.Count}…";
                var (ok, _) = await Svc.Api.RemoveWatchLaterAsync(it.Aid);
                if (ok) { RemoveEverywhere(it); done++; if (_total > 0) _total--; }
                else fail++;
            }
            OnPropertyChanged(nameof(CountText));
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        if (fail == 0) Svc.Toast.Show($"已移出 {done} 个视频");
        else if (done == 0) Svc.Toast.Show("移出失败, 请稍后重试");
        else Svc.Toast.Show($"已移出 {done} 个, {fail} 个失败");
        if (_all.Count == 0) Error = "稍后再看是空的";
    }

    /// <summary>清空整个稍后再看(服务端不可撤销, 必须二次确认)</summary>
    private async Task ClearAllAsync()
    {
        if (Busy) return;
        if (_all.Count == 0)
        {
            Svc.Toast.Show("稍后再看已经是空的");
            return;
        }

        var r = MessageBox.Show(
            "确定要清空**全部**「稍后再看」吗?\n\n这会删除账号下整个列表, 且无法撤销。",
            "BiliDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        Busy = true;
        BusyText = "正在清空…";
        (bool ok, string? err) res;
        try
        {
            res = await Svc.Api.ClearWatchLaterAsync();
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        if (!res.ok)
        {
            Svc.Toast.Show("清空失败: " + (res.err ?? "未知错误"));
            return;
        }

        Reset();
        IsEditMode = false;
        Error = "稍后再看是空的";
        Svc.Toast.Show("已清空稍后再看");
    }

    /// <summary>播放(页面代码后置调用)</summary>
    public void Play(VideoItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Bvid)) return;
        Svc.Player.PlayVideo(item.Bvid, item.Title);
    }
}
