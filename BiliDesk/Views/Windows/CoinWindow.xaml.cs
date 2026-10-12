using System.Windows;

namespace BiliDesk.Views;

/// <summary>
/// 投币数量选择窗(播放器"投币"按钮弹出的模态对话框)。
/// </summary>
public partial class CoinWindow : FluentWindow
{
    /// <summary>用户点了「确定」(false = 取消, 调用方直接忽略)</summary>
    public bool Confirmed { get; private set; }

    /// <summary>要投的枚数: 1 或 2</summary>
    public int Count { get; private set; } = 1;

    /// <summary>是否同时点赞(对应接口 select_like=1)</summary>
    public bool AlsoLike { get; private set; }

    public CoinWindow()
    {
        InitializeComponent();
        // 默认 1 枚: 这是最保守的一次花费, 想要 2 枚的自己去点第二张卡
        UpdateTitle();
    }

    /// <summary>两张卡片共用一个处理: 选中谁, 标题上的数字就跟着变</summary>
    private void OnCardChecked(object sender, RoutedEventArgs e)
    {
        Count = ReferenceEquals(sender, CardTwo) ? 2 : 1;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        // 构造期 XAML 里 IsChecked="True" 会先触发一次 Checked, 那时字段可能还没赋值
        if (CountText != null) CountText.Text = Count.ToString();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        AlsoLike = AlsoLikeCheck.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
