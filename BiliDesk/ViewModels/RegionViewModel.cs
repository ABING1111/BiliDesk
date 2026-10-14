using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 分区页 ViewModel: 一个分区 = 一份**热门**内容(ranking/v2 分区榜为主)。
///
/// ★★ 2026-10-13 两轮调下来最终采用混合源, 见
///   <see cref="ApiClient.GetRegionFeedAsync"/> 的说明。一句话:
///   ranking/v2 质量好(动画分区最低播放 33.7 万)但 rid=202(资讯)拒收,
///   所以正常分区走它, 被拒的才回退 newlist(最新投稿)。
///
/// ★ "刷新"是**重新请求**(不是翻页): 两个接口的 pn 都被服务端忽略,
///   实测 pn=1/2/3 返回完全相同的条目。所以没有"加载更多", 只有"重新拉一次"。
/// </summary>
public class RegionViewModel : ObservableObject
{
    private string _regionName = "";
    private int _tid;
    private bool _loading;
    private string _error = "";
    private bool _loaded;

    public ObservableCollection<VideoItem> Items { get; } = new();

    public string RegionName { get => _regionName; private set => SetProperty(ref _regionName, value); }
    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    /// <summary>当前分区 id(页面用它判断"是不是同一个分区", 决定要不要重拉)</summary>
    public int Tid => _tid;
    public bool HasItems => Items.Count > 0;

    public ICommand RetryCommand { get; }
    /// <summary>刷新当前分区(重新请求一次)。分区没有"加载更多"—— 接口不支持翻页。</summary>
    public ICommand RefreshCommand { get; }

    public RegionViewModel()
    {
        RetryCommand = new RelayCommand(() => _ = LoadAsync());
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        // 挂上/清空列表时刷新 HasItems(卡片墙的显隐绑的是它)
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>切到某个分区。同一个分区且已加载过 → 直接返回</summary>
    public async Task OpenAsync(string name, int tid)
    {
        if (name == RegionName && tid == _tid && _loaded) return;
        RegionName = name;
        _tid = tid;
        _loaded = false;
        await LoadAsync();
    }

    /// <summary>刷新当前分区: 清掉"已加载"标记后重拉(内容会是服务端此刻的快照)</summary>
    public async Task RefreshAsync()
    {
        _loaded = false;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_tid <= 0) return;
        Loading = true;
        Error = "";
        try
        {
            var (ok, err, items) = await Svc.Api.GetRegionFeedAsync(_tid);
            Items.Clear();
            if (items == null || items.Count == 0)
            {
                // ★ 两种"空"要分开说: ok=true 而 0 条 = 服务端这个分区就是空的
                //   (实测 ranking 下 19 个分区全有数据, newlist 下有 3 个空);
                //   ok=false = 请求失败(风控/网络)。混成一句会让人查错方向。
                Error = ok ? "这个分区暂时没有内容" : (err ?? "加载失败");
            }
            else
            {
                foreach (var it in items) Items.Add(it);
                _loaded = true;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
        }
    }
}
