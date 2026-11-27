using System.Collections.Generic;
using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 快捷键自定义(2026-11-28): 组合键解析 / 生效映射 / 冲突回落。
/// ★ 这一层刻意不碰 WPF, 所以能直接穷举"手改配置 + 改绑"的各种边角;
///   与真实 Key 的换算在 Services/ShortcutService 里, 只做一次 Enum 转换。
/// </summary>
public class ShortcutGestureTests
{
    private static ShortcutGesture Parse(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out var gesture), $"应能解析: {text}");
        return gesture;
    }

    [Theory]
    [InlineData("Space", "Space", ShortcutModifiers.None)]
    [InlineData("Left", "Left", ShortcutModifiers.None)]
    [InlineData("shift+left", "Left", ShortcutModifiers.Shift)]
    [InlineData("ctrl+SHIFT+k", "K", ShortcutModifiers.Control | ShortcutModifiers.Shift)]
    [InlineData("Alt+F5", "F5", ShortcutModifiers.Alt)]
    [InlineData("oemplus", "OemPlus", ShortcutModifiers.None)]
    public void TryParse_Normalizes_Modifiers_And_Key(
        string input, string key, ShortcutModifiers modifiers)
    {
        var gesture = Parse(input);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(modifiers, gesture.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+K+L")]
    [InlineData("NotAKey")]
    [InlineData("Ctrl+NotAKey")]
    public void TryParse_Rejects_Invalid(string input)
        => Assert.False(ShortcutGesture.TryParse(input, out _));

    [Fact]
    public void CanonicalString_Orders_Modifiers()
        => Assert.Equal("Ctrl+Alt+Shift+K", Parse("shift+ctrl+alt+k").ToCanonicalString());

    [Theory]
    [InlineData("Shift+Left", "Shift+←")]
    [InlineData("Alt+Right", "Alt+→")]
    [InlineData("Space", "Space")]
    [InlineData("OemPlus", "+")]
    public void DisplayString_Uses_Symbols(string canonical, string expected)
        => Assert.Equal(expected, Parse(canonical).ToDisplayString());

    [Fact]
    public void Empty_Displays_As_Unbound()
    {
        Assert.True(ShortcutGesture.Empty.IsEmpty);
        Assert.Equal("未设置", ShortcutGesture.Empty.ToDisplayString());
        Assert.Equal("", ShortcutGesture.Empty.ToCanonicalString());
    }

    [Theory]
    [InlineData("Ctrl+C", true)]
    [InlineData("Ctrl+V", true)]
    [InlineData("Alt+F4", true)]
    [InlineData("Escape", true)]
    [InlineData("Ctrl+Shift+C", false)]
    [InlineData("F5", false)]
    public void Reserved_Combinations(string input, bool reserved)
        => Assert.Equal(reserved, ShortcutGesture.IsReserved(Parse(input)));

    [Fact]
    public void Keys_Whitelist_Rejects_Modifier_Keys()
    {
        Assert.False(ShortcutKeys.IsKnown("LeftCtrl"));
        Assert.False(ShortcutKeys.IsKnown("ImeProcessed"));
        Assert.False(ShortcutKeys.IsKnown("None"));
        Assert.True(ShortcutKeys.IsKnown("Space"));
        Assert.Equal("Space", ShortcutKeys.Normalize("space"));
    }

    [Theory]
    [InlineData("Enter", "Return")]
    [InlineData("PageDown", "Next")]
    [InlineData("OemSemicolon", "Oem1")]
    [InlineData("OemTilde", "Oem3")]
    [InlineData("OemPipe", "Oem5")]
    [InlineData("OemCloseBrackets", "Oem6")]
    public void Key_Aliases_Normalize_To_Wpf_ToString_Names(string input, string canonical)
    {
        Assert.Equal(canonical, ShortcutKeys.Normalize(input));
        Assert.Equal(canonical, Parse(input).Key);
    }

    [Theory]
    [InlineData("Return", "Enter")]
    [InlineData("Next", "PageDown")]
    [InlineData("Oem1", ";")]
    [InlineData("Oem6", "]")]
    public void Canonical_Aliases_Display_Friendly_Names(string key, string display)
        => Assert.Equal(display, ShortcutKeys.DisplayOf(key));
}

public class ShortcutMapTests
{
    private static ShortcutGesture Parse(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out var gesture), $"应能解析: {text}");
        return gesture;
    }

    private static ShortcutMap Build(Dictionary<string, string>? overrides = null)
        => ShortcutMap.Build(overrides);

    [Fact]
    public void Defaults_Are_Applied()
    {
        var map = Build();

        Assert.Equal(Parse("Space"), map.GestureOf(ShortcutAction.PlayPause));
        Assert.Equal(Parse("Alt+Left"), map.GestureOf(ShortcutAction.PreviousVideo));
        Assert.Equal(Parse("Escape"), map.GestureOf(ShortcutAction.Back));
        Assert.Equal(Parse("F5"), map.GestureOf(ShortcutAction.Refresh));

        Assert.Equal(ShortcutAction.PlayPause, map.Match(Parse("Space")));
        Assert.Equal(ShortcutAction.NextVideo, map.Match(Parse("Alt+Right")));
    }

    [Fact]
    public void Ctrl_Combinations_Are_Not_Stolen()
    {
        var map = Build();
        Assert.Null(map.Match(Parse("Ctrl+C")));
        Assert.Null(map.Match(Parse("Ctrl+V")));
        Assert.Null(map.Match(Parse("Ctrl+A")));
    }

    [Fact]
    public void Shift_Falls_Back_When_No_Exact_Binding()
    {
        var map = Build();

        Assert.Equal(ShortcutAction.VolumeUp, map.Match(Parse("Shift+Up")));
        Assert.Equal(ShortcutAction.VolumeDown, map.Match(Parse("Shift+Down")));
        Assert.Equal(ShortcutAction.SeekBackward, map.Match(Parse("Shift+Left")));
        Assert.Equal(ShortcutAction.SeekForward, map.Match(Parse("Shift+Right")));
    }

    [Fact]
    public void Exact_Shift_Binding_Wins_Over_Fallback()
    {
        var map = Build(new Dictionary<string, string> { ["player.volumeUp"] = "Shift+Up" });

        Assert.Equal(ShortcutAction.VolumeUp, map.Match(Parse("Shift+Up")));
        // 原默认的裸 Up 被这次改绑整体替换, 不再另行保留
        Assert.Null(map.Match(Parse("Up")));
    }

    [Fact]
    public void Override_Replaces_Default_And_Writes_Canonical_Form()
    {
        var map = ShortcutMap.Build(
            new Dictionary<string, string> { ["player.mute"] = "k" },
            out var cleaned);

        Assert.Equal(Parse("K"), map.GestureOf(ShortcutAction.Mute));
        Assert.Equal(ShortcutAction.Mute, map.Match(Parse("K")));
        // 原来的 M 被让出来, 不再被静音占用
        Assert.Null(map.Match(Parse("M")));
        Assert.Equal("K", cleaned["player.mute"]);
    }

    [Fact]
    public void Empty_String_Means_Unbound()
    {
        var map = ShortcutMap.Build(
            new Dictionary<string, string> { ["player.mute"] = "" },
            out var cleaned);

        Assert.True(map.GestureOf(ShortcutAction.Mute).IsEmpty);
        Assert.Null(map.Match(Parse("M")));
        Assert.Equal("", cleaned["player.mute"]);
    }

    [Fact]
    public void Override_Equal_To_Default_Is_Not_Stored()
    {
        var map = ShortcutMap.Build(
            new Dictionary<string, string> { ["player.mute"] = "M" },
            out var cleaned);

        Assert.Equal(Parse("M"), map.GestureOf(ShortcutAction.Mute));
        Assert.False(cleaned.ContainsKey("player.mute"));
    }

    [Fact]
    public void Conflicting_Overrides_Keep_The_Catalog_First_One()
    {
        // 目录顺序里静音在点赞之前 —— 冲突时前者赢, 后者回落自己的默认 L
        var map = ShortcutMap.Build(
            new Dictionary<string, string>
            {
                ["player.like"] = "K",
                ["player.mute"] = "K",
            },
            out var cleaned);

        Assert.Equal(Parse("K"), map.GestureOf(ShortcutAction.Mute));
        Assert.Equal(Parse("L"), map.GestureOf(ShortcutAction.Like));
        Assert.Equal("K", cleaned["player.mute"]);
        Assert.False(cleaned.ContainsKey("player.like"));
    }

    [Fact]
    public void Reserved_And_Unknown_Overrides_Are_Dropped()
    {
        var map = ShortcutMap.Build(
            new Dictionary<string, string>
            {
                ["player.like"] = "Ctrl+C",
                ["no.such.action"] = "K",
                ["player.mute"] = "NotAKey",
            },
            out var cleaned);

        Assert.Equal(Parse("L"), map.GestureOf(ShortcutAction.Like));
        Assert.Equal(Parse("M"), map.GestureOf(ShortcutAction.Mute));
        Assert.Empty(cleaned);
    }

    [Fact]
    public void Override_Can_Use_A_Default_Key_After_It_Is_Freed()
    {
        var map = ShortcutMap.Build(
            new Dictionary<string, string>
            {
                ["player.mute"] = "",
                ["player.like"] = "M",
            },
            out var cleaned);

        Assert.True(map.GestureOf(ShortcutAction.Mute).IsEmpty);
        Assert.Equal(Parse("M"), map.GestureOf(ShortcutAction.Like));
        Assert.Equal("", cleaned["player.mute"]);
        Assert.Equal("M", cleaned["player.like"]);
    }

    [Fact]
    public void FindConflict_Sees_Other_Actions()
    {
        var map = Build();

        Assert.Equal(ShortcutAction.Mute, map.FindConflict(ShortcutAction.Like, Parse("M")));
        Assert.Null(map.FindConflict(ShortcutAction.Mute, Parse("M")));
        Assert.Null(map.FindConflict(ShortcutAction.Like, ShortcutGesture.Empty));
    }
}
