using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 快捷键的修饰键集合。
///
/// ★ 为什么不直接用 WPF 的 <see cref="System.Windows.Input.ModifierKeys"/>:
///   <see cref="ShortcutGesture"/> / <see cref="ShortcutMap"/> 要能在 BiliDesk.Tests 里
///   脱离 WPF 直接单测(测试工程刻意不引用 UseWPF, 见 BiliDesk.Tests.csproj 的说明)。
///   与 WPF 的换算只发生在 ShortcutService(UI 边界)那一个地方。
/// </summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// 一个组合键 = 一个主键 + 修饰键。存储/序列化格式是 "Ctrl+Shift+K"。
///
/// ★ 主键用 WPF <c>Key</c> 枚举的名字(如 "Space" / "OemPlus" / "D1"), 而不是字符:
///   Key 名称是稳定的, 字符会随键盘布局变化。解析时只接受
///   <see cref="ShortcutKeys"/> 白名单里的名字 —— 白名单同时挡住 Key.None / 修饰键 /
///   ImeProcessed 这类没有实际按键含义的枚举成员。
/// </summary>
public readonly record struct ShortcutGesture(string Key, ShortcutModifiers Modifiers)
{
    public static readonly ShortcutGesture Empty = new("", ShortcutModifiers.None);

    /// <summary>没有绑定(= 这个操作不响应键盘)</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Key);

    /// <summary>
    /// 解析 "Ctrl+Shift+K" / "shift+left" / "Space" 这类文本。
    /// 修饰键顺序不限、大小写不限; 主键必须是白名单里的 Key 名称。
    /// 空串 / 只有修饰键 / 多个主键 / 未知主键一律返回 false。
    /// </summary>
    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = ShortcutModifiers.None;
        string? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ShortcutModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= ShortcutModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= ShortcutModifiers.Shift;
                    continue;
            }

            // 第二个非修饰键 = 非法组合(本应用的快捷键只支持单主键)
            if (key != null) return false;
            key = part;
        }

        if (key == null) return false;
        var normalized = ShortcutKeys.Normalize(key);
        if (normalized == null) return false;

        gesture = new ShortcutGesture(normalized, modifiers);
        return true;
    }

    /// <summary>规范化的存储格式(修饰键顺序固定: Ctrl+Alt+Shift+主键)</summary>
    public string ToCanonicalString()
    {
        if (IsEmpty) return "";

        var parts = new List<string>(4);
        if ((Modifiers & ShortcutModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>界面展示格式: 方向键/标点换成符号, 其余沿用 Key 名称</summary>
    public string ToDisplayString()
    {
        if (IsEmpty) return "未设置";

        var parts = new List<string>(4);
        if ((Modifiers & ShortcutModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(ShortcutKeys.DisplayOf(Key));
        return string.Join("+", parts);
    }

    public override string ToString() => ToCanonicalString();

    /// <summary>
    /// 系统 / 文本编辑保留的组合。这些键不能被应用抢走:
    ///   · Ctrl+C/V/X/A/Z/Y —— 播放器里的只读文本(评论、简介、字幕)要能复制,
    ///     项目曾经因为裸字母快捷键不分修饰键, 把 Ctrl+C 吃掉导致复制失效(见 PlayerWindow.Keyboard.cs);
    ///   · Alt+F4 / F10 / Tab —— 系统与焦点导航;
    ///   · Esc —— 捕获界面用它取消, 无法被捕获; 作为"返回"的默认值仍然照常工作
    ///     (默认值不经过"改绑"校验, 见 ShortcutMap.Build)。
    /// </summary>
    private static readonly HashSet<string> ReservedCanonical = new(StringComparer.Ordinal)
    {
        "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+A", "Ctrl+Z", "Ctrl+Y",
        "Alt+F4", "F10", "Tab", "Escape",
    };

    public static bool IsReserved(ShortcutGesture gesture)
        => !gesture.IsEmpty && ReservedCanonical.Contains(gesture.ToCanonicalString());
}

/// <summary>
/// 允许出现在快捷键里的主键白名单 + 界面展示名。
///
/// ★ 白名单而不是"Enum.TryParse 能过就行": 设置页的捕获框可能收到 Key.None /
///   LeftCtrl / ImeProcessed 等没有实际含义的枚举值; 手改 settings.json 也可能塞进
///   一个永远不会匹配的键名。统一在这里收口, 解析失败 = 当作没绑定。
/// ★ 这是纯字符串层的东西, 不依赖 WPF —— 与 WPF Key 的换算在 ShortcutService 里。
/// </summary>
public static class ShortcutKeys
{
    private static readonly Dictionary<string, string> ByName = BuildNames();

    private static Dictionary<string, string> BuildNames()
    {
        var names = new List<string>(80);

        for (var c = 'A'; c <= 'Z'; c++) names.Add(c.ToString());
        for (var i = 0; i <= 9; i++) names.Add("D" + i);
        for (var i = 0; i <= 9; i++) names.Add("NumPad" + i);

        names.AddRange(new[]
        {
            "Left", "Right", "Up", "Down",
            "Home", "End", "PageUp", "Next", "Insert", "Delete", "Back",
            "Return", "Space", "Escape", "Tab",
            "OemPlus", "OemMinus", "OemComma", "OemPeriod",
            "Oem1", "Oem3", "Oem5", "Oem6", "OemQuestion", "OemBackslash",
            "OemOpenBrackets", "OemQuotes",
            "Add", "Subtract", "Multiply", "Divide", "Decimal",
            "MediaPlayPause", "MediaNextTrack", "MediaPreviousTrack", "MediaStop",
        });
        for (var i = 1; i <= 12; i++) names.Add("F" + i);

        var map = new Dictionary<string, string>(names.Count + 8, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) map[name] = name;

        // ★★★ WPF 的 Key 枚举里有一批**同值别名**, Enum.ToString() 返回的是先定义的那个:
        //   Enter → Return / PageDown → Next / OemSemicolon → Oem1 / OemTilde → Oem3 /
        //   OemPipe → Oem5 / OemCloseBrackets → Oem6(实测 .NET 10 + WPF)。
        //   捕获框用 key.ToString() 生成字符串, 所以上面白名单必须收正式名;
        //   这里再把用户更熟悉的写法映射到正式名, 手改配置两种写法都能读。
        map["Enter"] = "Return";
        map["PageDown"] = "Next";
        map["OemSemicolon"] = "Oem1";
        map["OemTilde"] = "Oem3";
        map["OemPipe"] = "Oem5";
        map["OemCloseBrackets"] = "Oem6";
        return map;
    }

    public static bool IsKnown(string key) => ByName.ContainsKey(key);

    /// <summary>把用户输入/手改配置里的键名规范成 Key 枚举的正式写法; 不认识返回 null</summary>
    public static string? Normalize(string key)
        => ByName.TryGetValue(key, out var canonical) ? canonical : null;

    /// <summary>界面展示名(方向键等换成符号, 一眼能认出来)</summary>
    public static string DisplayOf(string key) => key switch
    {
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Back" => "Backspace",
        "Escape" => "Esc",
        "Return" or "Enter" => "Enter",
        "Next" or "PageDown" => "PageDown",
        "OemPlus" or "Add" => "+",
        "OemMinus" or "Subtract" => "-",
        "Multiply" => "*",
        "Divide" => "/",
        "Decimal" => ".",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "Oem1" or "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemOpenBrackets" => "[",
        "Oem6" or "OemCloseBrackets" => "]",
        "Oem5" or "OemPipe" or "OemBackslash" => "\\",
        "Oem3" or "OemTilde" => "~",
        "OemQuestion" => "/",
        "MediaPlayPause" => "媒体播放",
        "MediaNextTrack" => "下一曲",
        "MediaPreviousTrack" => "上一曲",
        "MediaStop" => "停止",
        _ => key,
    };
}
