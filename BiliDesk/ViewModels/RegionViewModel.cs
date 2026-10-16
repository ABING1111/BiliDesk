using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 分区页 ViewModel: 一个分区 = 一条可翻页的内容流。
///
/// ★★★ 2026-10-17 整体重做(用户报"分区视频数据有问题, 重新做该功能")。
///
///   旧实现走 <c>ranking/v2?rid=旧tid</c>(美食=211), 实测返回的是**约 564 天前**的陈旧榜单 ——
///   这就是"数据有问题"的根因。现在换成 B站网页版分区页(/c/food/)真正的数据源
///   <c>region/feed/rcmd</c> + **v2 tid**(美食=1020), 实测中位年龄 3~9 天。
///   接口细节与实测数据见 <see cref="ApiClient.GetRegionFeedAsync"/>。
///
/// ★ 翻页是真的翻页(旧实现做不到): <c>display_id</c> 是游标, 从 1 递增即可持续取新内容
///   (实测美食 25 页 = 500 条不重复)。所以这里接"滚动到底自动加载更多",
///   和首页/搜索/稍后再看同一套写法。
/// </summary>
public class RegionViewModel : ObservableObject
{
    private string _regionName = "";
    private int _tid;
    private bool _loading;
    private bool _loadingMore;
    private string _error = "";
    private bool _loaded;

    /// <summary>
    /// 当前错误文本(空 = 无错误)。
    /// ★ 用自定义 setter 而不是自动属性: 两个派生属性(ShowFirstError / ShowMoreError)
    ///   要跟着变, 页面绑的是它们而不是 Error 本身。
    /// </summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(ShowFirstError));
            OnPropertyChanged(nameof(ShowMoreError));
        }
    }

    /// <summary>下一个要请求的游标(display_id)。从 1 开始, 每成功取一页 +1。</summary>
    private int _nextPage = 1;

    /// <summary>服务端明确说了"没有更多"(62013) —— 到这一页就不再自动拉了。</summary>
    private bool _noMore;

    public ObservableCollection<VideoItem> Items { get; } = new();

    public string RegionName { get => _regionName; private set => SetProperty(ref _regionName, value); }
    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }

    /// <summary>补下一页中(与 Loading 分开: 首屏转圈盖整页, 补页只在底部转, 内容不闪)</summary>
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }

    /// <summary>
    /// 错误条该以"整页居中"还是"列表底部一行"呈现。
    /// ★ 为什么必须分开(2026-10-17, 探针 bd-probe-zone-shot 抓到的): 卡片墙与错误块是
    ///   同一个 Grid 的兄弟节点, 居中那个会**盖在已有卡片上**。首屏失败时这是对的
    ///   (本来就没内容, 屏幕上就该只有一句话 + 重试); 但"补下一页"失败时,
    ///   用户手里已经有 20~200 条内容, 在上面糊一层居中的报错既挡内容又像整页崩了。
    ///   ⇒ 有内容时只在底部补一行小字。
    /// </summary>
    public bool ShowFirstError => Items.Count == 0 && !string.IsNullOrEmpty(Error);

    /// <summary>补页失败(已有内容, 只在列表底部提示一行)</summary>
    public bool ShowMoreError => Items.Count > 0 && !string.IsNullOrEmpty(Error);

    /// <summary>当前分区 id(v2 tid; 页面用它判断"是不是同一个分区", 决定要不要重拉)</summary>
    public int Tid => _tid;

    public bool HasItems => Items.Count > 0;

    /// <summary>还能不能往下翻: 没到头且当前没有请求在飞</summary>
    public bool HasMore => !_noMore && HasItems;

    public ICommand RetryCommand { get; }

    /// <summary>
    /// 补页失败后的重试: 重拉**同一页**。
    /// ★ 与 RetryCommand 的区别很重要 —— 那个错在首屏(列表本来就是空的), 刷新即重来;
    ///   而补页失败时用户手里已经有内容, 走 RefreshCommand 会把列表清空从头拉,
    ///   用户翻到的位置全丢。这里只补下一页。
    /// </summary>
    public ICommand RetryMoreCommand { get; }

    /// <summary>刷新当前分区: 回到游标 1 重拉第一页(内容会是服务端此刻的快照)</summary>
    public ICommand RefreshCommand { get; }

    public RegionViewModel()
    {
        RetryCommand = new RelayCommand(() => _ = RefreshAsync());
        RetryMoreCommand = new RelayCommand(() => _ = LoadMoreAsync());
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        // 挂上/清空列表时刷新 HasItems / HasMore(卡片墙的显隐与"加载更多"的可用性绑的是它们)
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasMore));
            // 内容有无决定错误条该居中还是贴底, 所以一起通知(见 ShowFirstError 的说明)
            OnPropertyChanged(nameof(ShowFirstError));
            OnPropertyChanged(nameof(ShowMoreError));
        };
    }

    /// <summary>切到某个分区。同一个分区且已加载过 → 直接返回</summary>
    public async Task OpenAsync(string name, int tid)
    {
        if (name == RegionName && tid == _tid && _loaded) return;
        RegionName = name;
        _tid = tid;
        _loaded = false;
        _nextPage = 1;
        _noMore = false;
        await LoadAsync();
    }

    /// <summary>刷新: 游标归位后重拉第一页(列表会被整体换掉)</summary>
    public async Task RefreshAsync()
    {
        _loaded = false;
        _nextPage = 1;
        _noMore = false;
        await LoadAsync();
    }

    /// <summary>
    /// 滚到底部时补下一页。
    ///
    /// ★ 与首页/搜索同款的多道闸: 请求在飞 / 已到头 / 尚未加载出首屏 都直接返回。
    ///   没有它们, 内容不足一屏时会"补了还是短"→ 立刻再来一次, 表现是页面自己在不停加载。
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (_tid <= 0 || _loading || _loadingMore || _noMore || Items.Count == 0) return;

        LoadingMore = true;
        try
        {
            var (ok, err, items) = await Svc.Api.GetRegionFeedAsync(_tid, _nextPage);
            if (!ok)
            {
                // 补页失败不改首屏内容, 只把错误挂出来(页面在底部显示一句, 不盖掉已有卡片)
                Error = err ?? "加载失败";
                return;
            }

            if (items == null || items.Count == 0)
            {
                _noMore = true;
                OnPropertyChanged(nameof(HasMore));
                return;
            }

            // 游标只在**成功取到一页**时推进: 失败重试时应该还请求同一页
            _nextPage++;

            // 去重: 服务端在滚动推荐池, 深页会跟前面重复(实测第 20 页仍有 2 条新的, 说明
            // 重复是常态而不是异常)。不去重会在列表里出现两张一模一样的卡片。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in Items) seen.Add(it.Bvid);
            var added = 0;
            foreach (var it in items)
                if (seen.Add(it.Bvid)) { Items.Add(it); added++; }

            Error = "";

            // 到头的两个判据: 不足一页(池子见底), 或整页全是重复(池子已经滚回原处)。
            // ★ 后一条是必需的: 实测深页会变成"每页 20 条里只有 2 条新的",
            //   没有它就会一直拉下去 —— 用户滚到底看到的是"页面自己在不停加载"。
            if (items.Count < ApiClient.RegionPageSize || added == 0) _noMore = true;

            OnPropertyChanged(nameof(HasMore));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            LoadingMore = false;
        }
    }

    private async Task LoadAsync()
    {
        if (_tid <= 0) return;
        Loading = true;
        Error = "";
        Items.Clear();
        try
        {
            var (ok, err, items) = await Svc.Api.GetRegionFeedAsync(_tid, 1);
            if (items == null || items.Count == 0)
            {
                // ★ 两种"空"要分开说: ok=true 而 0 条 = 服务端这个分区此刻就是空的;
                //   ok=false = 请求失败(风控/网络)。混成一句会让人查错方向。
                Error = ok ? "这个分区暂时没有内容" : (err ?? "加载失败");
            }
            else
            {
                foreach (var it in items) Items.Add(it);
                _loaded = true;
                _nextPage = 2;                                  // 第一页已消费
                if (items.Count < ApiClient.RegionPageSize) _noMore = true;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
            OnPropertyChanged(nameof(HasMore));
        }
    }
}
