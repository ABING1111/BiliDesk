using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BiliDesk.Helpers;

/// <summary>枚举相等比较(用于 RadioButton 的 IsChecked 双向绑定, 参数传枚举值名称)</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string s && Enum.TryParse(targetType, s, out var result))
            return result;
        return Binding.DoNothing;
    }
}

/// <summary>取反(Visibility)</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反(bool => bool, 用于绑定是否可交互等)</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>字符串非空 => Visible, 空/Null => Collapsed</summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串为空 => Visible(用于错误提示/空状态)</summary>
public class InverseStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>数字 > 0 => Visible(列表非空)</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool => Visible/Collapsed 取反(IsLoading 控制 spinner)</summary>
public class InverseBoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 十六进制颜色字符串 => SolidColorBrush(番剧角标: 大会员粉 #FB7299 / 出品蓝 #00C0FF …)。
///
/// ★ 为什么不用 XAML 里的 <c>{Binding BadgeColor}</c> 直接喂给 Background:
///   Brush 可以用类型转换器从 "#FB7299" 这样的字符串构造, 但**转换失败时抛的是
///   运行时异常而不是绑定错误**, 而且是空值时才炸 —— 角标底色恰好经常是空的
///   (接口有时只给 badge 文字不给 badge_info), 于是整个列表渲染直接崩掉。
///   转换器把"解析不了"降级成 fallback 颜色, 比让绑定去抛异常稳得多。
///
/// 参数写法: <c>"#FB7299"</c> 作为参数给回退色; 解析不了就用它, 也不给就用参数里的默认值。
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fallback = (parameter as string) ?? "#FB7299";
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) s = fallback;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.Trim());
            // 冻结构造, 不然每张卡片每次刷新都会新建一个 Brush 实例
            return new System.Windows.Media.SolidColorBrush(c);
        }
        catch
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(fallback);
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch
            {
                return System.Windows.Media.Brushes.Gray;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 按"档位"取一个值，供**非动画**用途（典型是 <c>UniformGrid.Columns</c>）。
///
/// 参数写法：<c>"Compact=2|4"</c> —— 前半段是"当值等于 Compact 时用 2"，`|` 后面是其余档位的值。
/// 反过来写（"其它档位=4，Compact=2"）也能用，只要把 <c>Compact=2</c> 放前半段。
///
/// ★ 为什么不能改用 <c>ObjectAnimationUsingKeyFrames</c> 去动 <c>Columns</c>：
///   <c>DiscreteObjectKeyFrame.Value</c> 的声明类型是 **object**，XAML 里的字面量 <c>2</c> 会被解析成
///   **字符串 "2"**（没有目标类型可供转换）。动画算出的当前值因此是 string，而
///   <c>UniformGrid.Columns</c> 带 ValidateValueCallback，直接拒收 ⇒ 运行时
///   AnimationException「应用于"Columns"属性的动画计算"2"的当前值，而该值不是此属性的有效值」，
///   整个窗口被拖崩（2026-09-30 实测）。绑定走的是正常的类型转换，没有这个问题。
/// </summary>
public class WidthClassToValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var current = value?.ToString() ?? string.Empty;
        var spec = (parameter as string ?? string.Empty).Split('|');

        // 默认值 = `|` 后面那段；没写 `|` 就退回第一段
        var picked = spec.Length > 1 ? spec[1] : (spec.Length > 0 ? spec[0] : string.Empty);

        if (spec.Length > 0)
        {
            var head = spec[0];
            var eq = head.IndexOf('=');
            if (eq > 0 && string.Equals(head[..eq], current, StringComparison.Ordinal))
                picked = head[(eq + 1)..];
        }

        try
        {
            return string.IsNullOrEmpty(picked)
                ? Binding.DoNothing
                : System.Convert.ChangeType(picked, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            // 参数写错(值转不成目标类型)时保持原值, 而不是抛进绑定系统
            return Binding.DoNothing;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
