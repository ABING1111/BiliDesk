using System.Collections.ObjectModel;
using System.Windows.Input;

namespace BiliDesk.ViewModels;

/// <summary>
/// 页码栏的一项: 数字页或省略号。
///
/// ★ 2026-11-19 从 UserSpaceViewModel.cs 底部搬到这里: 收藏页也用同一套页码栏了,
///   再留在某个具体 VM 的文件里会变成"另一个页面的模型藏在别人文件里"。
/// </summary>
public sealed class PageItem : ObservableObject
{
    public int Page { get; init; }
    public string Display { get; init; } = "";

    /// <summary>true = 省略号(不可点)</summary>
    public bool IsEllipsis { get; init; }

    private bool _isCurrent;

    /// <summary>当前页(强调色实底)</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>
/// 「底部页码栏」宿主页面要提供的成员(2026-11-19)。
///
/// ★ 为什么要有这个接口: <c>Views/Controls/PagerBar</c> 是**一个**控件, 绑定的是它继承到的
///   DataContext(也就是页面 VM)。WPF 的绑定是运行期按名字解析的, 写错一个属性名不会编译报错,
///   只会安静地"页码栏不显示/按钮一直是灰的"。接口把这个契约写死, 加上一条单测,
///   改属性名时至少能在测试里立刻发现。
///
/// ★ 现有实现: <see cref="UserSpaceViewModel"/>(UP 主主页投稿)与
///   <see cref="FavoritesViewModel"/>(收藏夹)。后续给搜索/稍后再看接分页时同样实现它即可。
/// </summary>
public interface IPagedViewModel
{
    /// <summary>页码栏的条目(数字页或省略号), 由 <see cref="Helpers.PageBar.Build"/> 生成</summary>
    ObservableCollection<PageItem> PageItems { get; }

    /// <summary>当前页(1 基)</summary>
    int CurrentPage { get; }

    /// <summary>总页数(0 = 还不知道 / 没有内容)</summary>
    int TotalPages { get; }

    /// <summary>是否显示页码栏(只有一页时整块收起)</summary>
    bool ShowPager { get; }

    /// <summary>「上一页」是否可点</summary>
    bool CanGoPrev { get; }

    /// <summary>「下一页」是否可点</summary>
    bool CanGoNext { get; }

    /// <summary>正在翻页(页码栏显示小转圈, 并暂时禁点)</summary>
    bool IsPaging { get; }

    /// <summary>页码数字是否可点(翻页在途时禁用)</summary>
    bool CanChangePage { get; }

    /// <summary>跳转到指定页(参数是页码, int 或数字字符串)</summary>
    ICommand GoToPageCommand { get; }

    /// <summary>上一页</summary>
    ICommand PrevPageCommand { get; }

    /// <summary>下一页</summary>
    ICommand NextPageCommand { get; }
}
