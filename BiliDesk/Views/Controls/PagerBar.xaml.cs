using System.Windows.Controls;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 底部页码栏: `上一页 | 1 2 3 … N | 下一页`(2026-11-19 从 UP 主主页抽出的公共控件)。
///
/// ★ 没有代码逻辑: 全部靠 DataContext 绑定(宿主 VM 实现 <see cref="ViewModels.IPagedViewModel"/>)。
///   之所以还是做成 UserControl 而不是把 XAML 复制到每个页面 —— 页码栏的样式、禁点规则、
///   "翻页在途显示转圈"这些细节一旦有两个副本就会慢慢长歪(UP 主主页和收藏页各一份)。
/// </summary>
public partial class PagerBar : UserControl
{
    public PagerBar()
    {
        InitializeComponent();
    }
}
