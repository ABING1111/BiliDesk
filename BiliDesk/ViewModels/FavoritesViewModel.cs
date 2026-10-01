using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 收藏页 ViewModel。
///
/// 这里**只有云端收藏**了: 本机收藏夹(本地 JSON)已按需求整体移除。
/// 之前的"本机/云端"双模式看着像多一种保险, 实际上是两套互不相通的数据 ——
/// 用户在播放页点"收藏"存进本地 JSON, 换台机器或者去网页端都看不到, 反而更像 bug。
/// 现在统一以 B 站账号的收藏夹为准, 语义单一、跨端一致。
///
/// 编辑模式(多选 + 批量移出 + 清空当前收藏夹)同样走云端接口, 不做本地假删。
/// </summary>
public class FavoritesViewModel : VideoListSelectionViewModel
{
    private bool _loading;
    private bool _loadingMore;
    private bool _hasMore;
    private string _error = "";
    private int _pn = 1;
    private long _currentFolderId;

    private bool _isEditMode;
    private bool _busy;
    private string _busyText = "";

    public ObservableCollection<FavFolder> Folders { get; } = new();
    public ObservableCollection<VideoItem> Items { get; } = new();

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>已加载条目数(顶栏显示)</summary>
    public string CountText => Items.Count > 0 ? $"({Items.Count})" : "";

    /// <summary>是否处于编辑模式(每张卡片出现选中圆标, 顶部换成操作条)</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value)) return;
            if (!value) ClearSelection();
        }
    }

    /// <summary>正在执行批量移出: 期间禁用按钮, 避免重复点击把同一批删两遍</summary>
    public bool Busy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanOperate)); }
    }

    /// <summary>操作进度文案(如"正在移出 40/120…")</summary>
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    /// <summary>当前收藏夹名称, 编辑模式下显示"正在编辑: xxx"</summary>
    public string CurrentFolderTitle { get; private set; } = "";

    // 选中计数一律基于**当前可见项** —— 被屏蔽掉的条目用户看不见, 不该被"全选"带走。
    // (实现见基类 VideoListSelectionViewModel: 全选的基数只有一处定义, 不会与历史/稍后再看漂移)
    public bool CanOperate => !Busy;

    public ICommand ReloadCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearFolderCommand { get; }

    /// <summary>
    /// 附加到视频卡片右键菜单里的"移出收藏"项。
    ///
    /// 为什么放进 ViewModel 而不是写在 XAML 里:
    /// 菜单项需要拿到"被右键的那张卡片"对应的 VideoItem 才能删除。菜单挂在卡片上时,
    /// 卡片的 DataContext 就是那个 VideoItem, 所以命令的参数由卡片侧注入即可 ——
    /// 比在代码后置里靠 sender 反查更直接、也不依赖可视树结构。
    /// </summary>
    public ObservableCollection<CardMenuItem> CardMenuItems { get; } = new();

    /// <summary>
    /// 预留: 「我的」页点收藏夹卡片时想打开的那个夹。
    /// 若页面还没做过首次加载, MainWindow 会先把要开的夹存在这里,
    /// LoadAsync 完成后优先选它而不是默认的第一夹 —— 否则用户点"化学"夹,
    /// 进收藏页看到的却是"默认收藏夹"。
    /// </summary>
    public FavFolder? PendingFolder { get; set; }

    public FavoritesViewModel()
    {
        CardMenuItems.Add(new CardMenuItem
        {
            Header = "移出收藏",
            Glyph = "\uE74D",
            Command = new RelayCommand(p =>
            {
                if (p is VideoItem v) Remove(v);
            })
        });

        ReloadCommand = new RelayCommand(() => _ = LoadAsync());
        SelectFolderCommand = new RelayCommand(p =>
        {
            if (p is FavFolder f) _ = SelectFolderAsync(f);
        });

        ToggleEditCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            IsEditMode = !IsEditMode;
        });

        SelectAllCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            // 只作用于**当前可见项** —— 被屏蔽掉的条目用户看不见, 不该被"全选"带走
            SetSelectionBulk(VisibleItems(), !AllSelected);
        });

        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync);
        ClearFolderCommand = new AsyncRelayCommand(ClearFolderAsync);

        HookSelectionSource();
    }

    // ------------------------------------------------------------ 选择状态
    // 下列实现(选中态派生属性、批量改选、集合增删时挂/退每一条的 PropertyChanged)
    // 全部来自基类 VideoListSelectionViewModel; 这里只留收藏页自己特有的部分。

    protected override ObservableCollection<VideoItem> SelectionSource => Items;

    /// <summary>集合增删时顺带刷一次顶栏的收藏夹条数(本页面特有, 另两个列表页没有)</summary>
    protected override void OnSourceItemsChanged() => UpdateCount();

    // ------------------------------------------------------------ 加载

    /// <summary>
    /// 加载: 先取收藏夹列表, 再自动打开第一个收藏夹。
    /// 默认落在第一个夹上是有意的 —— 进入收藏页就该立刻看到内容,
    /// 而不是面对一片空白等用户去点文件夹。
    /// </summary>
    public async Task LoadAsync()
    {
        if (!Svc.Session.HasLogin)
        {
            Error = "云端收藏需要登录后查看";
            HasMore = false;
            return;
        }
        if (Loading) return;

        Loading = true;
        Error = "";
        Folders.Clear();
        Items.Clear();
        _currentFolderId = 0;
        _pn = 1;
        IsEditMode = false;
        UpdateCount();

        var (ok, err, folders) = await Svc.Api.GetFavFoldersAsync();
        Loading = false;

        if (!ok || folders == null)
        {
            Error = err ?? "云端收藏夹加载失败";
            return;
        }
        if (folders.Count == 0)
        {
            Error = "账号下还没有创建收藏夹";
            return;
        }

        foreach (var f in folders) Folders.Add(f);

        // 默认打开第一个收藏夹(B 站返回顺序里第一个就是"默认收藏夹");
        // 「我的」页预留了要开的夹时, 优先选那个
        var target = PendingFolder != null
            ? Folders.FirstOrDefault(f => f.Id == PendingFolder.Id) ?? Folders[0]
            : Folders[0];
        PendingFolder = null;
        await SelectFolderAsync(target);
    }

    /// <summary>切换收藏夹并加载其内容</summary>
    public async Task SelectFolderAsync(FavFolder folder)
    {
        if (folder == null) return;
        _currentFolderId = folder.Id;
        CurrentFolderTitle = folder.Title;
        OnPropertyChanged(nameof(CurrentFolderTitle));

        // chip 高亮跟着数据走: 用户点击时 RadioButton 自己会选上, 但"程序切夹"
        // (我的页点卡片 / PendingFolder)不经过点击, 高亮必须在这里同步,
        // 否则就是"点了 A、视频是 A 的、高亮却停在 B"(2026-09-26 用户实测踩中)。
        foreach (var f in Folders)
        {
            if (f.IsCurrent) f.IsCurrent = false;
        }
        folder.IsCurrent = true;

        // 换夹时退出编辑模式: 上一夹的勾选对新夹没有意义
        IsEditMode = false;

        Items.Clear();
        UpdateCount();
        _pn = 1;
        HasMore = false;
        await LoadPageAsync();
    }

    /// <summary>列表滚动到底部时加载下一页</summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingMore || !HasMore || _currentFolderId <= 0) return;
        _pn++;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (_currentFolderId <= 0) return;
        LoadingMore = true;
        Error = "";
        var (ok, err, items, hasMore) = await Svc.Api.GetFavResourcesAsync(_currentFolderId, _pn, 20);
        LoadingMore = false;

        if (!ok || items == null)
        {
            Error = err ?? "收藏夹内容加载失败";
            HasMore = false;
            return;
        }

        // 去重(分页边界可能重复返回同一条)。
        //
        // 注意: 用来判重的集合必须从"**已经**在列表里的项"播种, 而不是从本页返回的 items 播种。
        // 之前写成 new HashSet(items.Select(...)) 是个致命错误 —— 本页所有 bvid 都被提前塞进
        // seen 了, 后面 seen.Add(bvid) 必然返回 false, 于是**一条都加不进列表**,
        // 表现就是"收藏夹列表能看到, 但每个收藏夹都显示'还是空的'"。
        var seen = new HashSet<string>(Items.Select(x => x.Bvid));
        foreach (var it in items)
        {
            if (!string.IsNullOrEmpty(it.Bvid) && seen.Add(it.Bvid))
                Items.Add(it);
        }
        UpdateCount();
        HasMore = hasMore;
        if (Items.Count == 0) Error = "这个收藏夹还是空的";
    }

    private void UpdateCount() => OnPropertyChanged(nameof(CountText));

    /// <summary>把当前收藏夹在本地的 media_count 减掉已移出的条数(顶栏的"(N)"跟着变)</summary>
    private void ShrinkFolderCount(int removed)
    {
        if (removed <= 0) return;
        var folder = Folders.FirstOrDefault(f => f.Id == _currentFolderId);
        // FavFolder.MediaCount 自己会通知 DisplayText 刷新, 这里**不要**去动 Folders 集合:
        // 移除再插回来会重建 RadioButton 容器, 把收藏夹标签的选中高亮弄丢。
        if (folder != null) folder.MediaCount = System.Math.Max(0, folder.MediaCount - removed);
    }

    // ------------------------------------------------------------ 右键菜单: 单条移出

    /// <summary>右键菜单: 从当前收藏夹移出</summary>
    public void Remove(VideoItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Bvid)) return;
        _ = RemoveFromCloudAsync(item);
    }

    /// <summary>
    /// 调服务端接口取消收藏, 成功后再从列表移除。
    ///
    /// deal 接口只认 avid, 而部分列表来源(搜索/推荐)不返回 aid, 所以这里按需补查一次详情。
    /// 只从**当前**收藏夹移出而不是"从所有含它的夹里删": 用户在这个夹里点的右键,
    /// 期望就是"从我看的这个夹里拿掉", 顺手删掉别的夹反而会造成意外丢失。
    /// </summary>
    private async Task RemoveFromCloudAsync(VideoItem item)
    {
        if (_currentFolderId <= 0) return;

        var aid = await EnsureAidAsync(item);
        if (aid <= 0)
        {
            Svc.Toast.Show("无法获取视频 avid, 移出失败");
            return;
        }

        var (ok, err) = await Svc.Api.DealFavoriteAsync(aid, delIds: new[] { _currentFolderId });
        if (!ok)
        {
            Svc.Toast.Show("移出收藏失败: " + (err ?? "未知错误"));
            return;
        }
        Items.Remove(item);
        UpdateCount();
        ShrinkFolderCount(1);
        Svc.Toast.Show("已移出收藏");
    }

    /// <summary>拿到条目对应的 avid; 列表没带 aid 就按 bvid 补查一次详情</summary>
    private async Task<long> EnsureAidAsync(VideoItem item)
    {
        if (item.Aid > 0) return item.Aid;
        var (_, _, detail) = await Svc.Api.GetVideoAsync(item.Bvid);
        if (detail != null && detail.Aid > 0)
        {
            item.Aid = detail.Aid; // 回填, 免得同一条反复补查
            return detail.Aid;
        }
        return 0;
    }

    // ------------------------------------------------------------ 编辑模式: 批量移出

    /// <summary>
    /// 把选中的条目批量移出当前收藏夹。
    ///
    /// 走 /x/v3/fav/resource/batch-del, 一次请求可以带多个 `avid:2`,
    /// 所以这里按 20 个一批发 —— 既不用逐条请求, 也不会把 URL 撑得过大。
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        if (Busy) return;
        if (_currentFolderId <= 0) return;

        var targets = VisibleItems().Where(x => x.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Svc.Toast.Show("还没有选择要移出的内容");
            return;
        }

        var r = MessageBox.Show($"确定要把选中的 {targets.Count} 个视频移出「{CurrentFolderTitle}」吗?", "BiliDesk",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        Busy = true;
        var removed = 0;
        var fail = 0;
        try
        {
            // 逐条补 avid(列表接口通常会带上, 少数情况为 0)
            var withAid = new List<VideoItem>();
            for (var i = 0; i < targets.Count; i++)
            {
                BusyText = $"正在准备 {i + 1}/{targets.Count}…";
                if (await EnsureAidAsync(targets[i]) > 0) withAid.Add(targets[i]);
                else fail++;
            }

            var batches = withAid.Chunk(20).ToList();
            for (var b = 0; b < batches.Count; b++)
            {
                BusyText = $"正在移出 {removed}/{withAid.Count}…";
                var batch = batches[b];
                var (ok, err) = await Svc.Api.BatchDelFavResourcesAsync(_currentFolderId, batch.Select(x => x.Aid));
                if (!ok)
                {
                    fail += batch.Length;
                    continue;
                }
                foreach (var it in batch)
                {
                    Items.Remove(it);
                    removed++;
                }
            }
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        UpdateCount();
        ShrinkFolderCount(removed);
        RaiseSelection();

        if (fail == 0) Svc.Toast.Show($"已移出 {removed} 个视频");
        else if (removed == 0) Svc.Toast.Show("移出失败, 请稍后重试");
        else Svc.Toast.Show($"已移出 {removed} 个, {fail} 个失败");

        if (Items.Count == 0)
        {
            Error = "这个收藏夹还是空的";
            IsEditMode = false;
        }
    }

    /// <summary>
    /// 清空当前收藏夹(移出**全部**内容, 不只是已加载的那几条)。
    ///
    /// 做法是"反复取第 1 页 → 批量移出 → 再取第 1 页": 每批删掉之后后面的内容会自动补到第 1 页,
    /// 所以循环几轮就能把整个夹掏空, 不需要先把全部条目拉进内存。
    /// 两个保险: 硬上限轮数, 以及"这一轮和上一轮拿到的内容完全一样就停" ——
    /// 后者防的是"删除请求返回成功但服务端其实没删"时死循环。
    /// </summary>
    private async Task ClearFolderAsync()
    {
        if (Busy) return;
        if (_currentFolderId <= 0) return;

        var r = MessageBox.Show(
            $"确定要清空收藏夹「{CurrentFolderTitle}」吗?\n\n" +
            "夹内**全部**内容都会被移出(不只是当前已加载的), 且无法撤销。",
            "BiliDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        const int MaxRounds = 80;   // 每轮最多 30 条 → 上限约 2400 条, 远超正常收藏夹规模
        Busy = true;
        var removed = 0;
        string? lastSig = null;
        var stopped = "";
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var (ok, err, items, _) = await Svc.Api.GetFavResourcesAsync(_currentFolderId, 1, 30);
                if (!ok || items == null)
                {
                    stopped = err ?? "读取收藏夹内容失败";
                    break;
                }

                var batch = items.Where(x => x.Aid > 0).ToList();
                if (batch.Count == 0) break;   // 空了, 收工

                // 和上一轮拿到的是同一批 → 说明删了没生效, 再循环下去就是死循环
                var sig = string.Join(",", batch.Select(x => x.Aid));
                if (sig == lastSig)
                {
                    stopped = "服务端没有真正删除内容, 已停止";
                    break;
                }
                lastSig = sig;

                BusyText = $"正在清空, 已移出 {removed} 个…";
                var (ok2, err2) = await Svc.Api.BatchDelFavResourcesAsync(_currentFolderId, batch.Select(x => x.Aid));
                if (!ok2)
                {
                    stopped = err2 ?? "移出失败";
                    break;
                }
                removed += batch.Count;
            }
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        Items.Clear();
        UpdateCount();
        ShrinkFolderCount(removed);
        IsEditMode = false;

        if (stopped.Length > 0 && removed == 0)
        {
            Error = "清空失败: " + stopped;
            return;
        }

        Error = "这个收藏夹还是空的";
        Svc.Toast.Show(stopped.Length > 0
            ? $"已移出 {removed} 个, 但中途停止: {stopped}"
            : $"已清空「{CurrentFolderTitle}」, 共移出 {removed} 个");
    }
}
