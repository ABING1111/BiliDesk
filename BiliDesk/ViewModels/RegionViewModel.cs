using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 分区页 ViewModel: 一个分区 = 一份 ranking/v2 分区榜(60~95 条, 排口自带 stat 全套计数)。
/// 同一个分区已加载过就复用, 换分区才重拉。
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

    public RegionViewModel()
    {
        RetryCommand = new RelayCommand(() => _ = LoadAsync());
        // 挂上/清空列表时刷新 HasItems(卡片墙的显隐绑的是它)
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>切到某个分区。同一个分区且已加载过 → 直接返回(不重拉, 保留滚动位置也无从谈起了)</summary>
    public async Task OpenAsync(string name, int tid)
    {
        if (name == RegionName && tid == _tid && _loaded) return;
        RegionName = name;
        _tid = tid;
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
            var (ok, err, items) = await Svc.Api.GetRegionRankAsync(_tid);
            Items.Clear();
            if (items == null || items.Count == 0)
            {
                Error = ok ? "这个分区暂时没有榜单数据" : (err ?? "加载失败");
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
