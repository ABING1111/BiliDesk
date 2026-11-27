using System;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 快捷键运行时服务: 持有当前生效的 <see cref="ShortcutMap"/>, 负责改绑 / 重置 / 按键匹配。
///
/// ★ 纯映射规则在 <see cref="ShortcutMap"/> 里(可单测); 这里只做三件带副作用的事:
///   1. 把 WPF 的 <see cref="Key"/> + <see cref="ModifierKeys"/> 换算成 ShortcutGesture;
///   2. 改绑后落盘并重建映射(已打开的播放器下一次按键就生效, 不用重启);
///   3. 把保存失败/冲突的原因以字符串交给设置页提示。
/// </summary>
public sealed class ShortcutService
{
    public static ShortcutService Instance { get; } = new();

    private ShortcutMap _map = ShortcutMap.Build(null);

    private ShortcutService() { }

    public ShortcutMap Map => _map;

    /// <summary>启动时(设置读完后)与每次改绑后重建映射</summary>
    public void Reload() => _map = ShortcutMap.Build(SettingsStore.Instance.Shortcuts);

    public ShortcutGesture GestureOf(ShortcutAction action) => _map.GestureOf(action);

    /// <summary>这个操作是否还是默认键(改过 / 清空过都为 false, 决定"恢复默认"按钮显不显示)</summary>
    public bool IsDefault(ShortcutAction action)
    {
        var def = ShortcutCatalog.ByAction(action);
        return def == null || !SettingsStore.Instance.Shortcuts.ContainsKey(def.Id);
    }

    /// <summary>
    /// 改绑。raw 为空串 = 清空绑定; 等于默认键 = 删除覆盖项(恢复默认)。
    /// 冲突 / 系统保留组合会返回 false, 并把原因放进 <paramref name="error"/>。
    /// </summary>
    public bool TryAssign(ShortcutAction action, string? raw, out string error)
    {
        error = "";
        var def = ShortcutCatalog.ByAction(action);
        if (def == null)
        {
            error = "未知的快捷键操作";
            return false;
        }

        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            SetOverride(def, "");
            return true;
        }

        if (!ShortcutGesture.TryParse(text, out var gesture))
        {
            error = "无法识别这个按键";
            return false;
        }

        if (ShortcutGesture.IsReserved(gesture))
        {
            error = "这个组合被系统或文本编辑占用, 请换一个";
            return false;
        }

        // ★ 冲突检查必须排在"是否等于默认键"之前: 如果这个键现在被别的自定义项占着,
        //   即使它正好是本操作的默认键, 也不能悄悄恢复默认 —— 那会让用户看到"点了没反应"。
        if (_map.FindConflict(action, gesture) is { } owner)
        {
            var ownerTitle = ShortcutCatalog.ByAction(owner)?.Title ?? "其它操作";
            error = $"与「{ownerTitle}」冲突";
            return false;
        }

        if (ShortcutGesture.TryParse(def.DefaultGesture, out var defaultGesture)
            && gesture == defaultGesture)
        {
            SetOverride(def, null);
            return true;
        }

        SetOverride(def, gesture.ToCanonicalString());
        return true;
    }

    /// <summary>单项恢复默认</summary>
    public void Reset(ShortcutAction action)
    {
        var def = ShortcutCatalog.ByAction(action);
        if (def != null) SetOverride(def, null);
    }

    /// <summary>全部恢复默认</summary>
    public void ResetAll()
    {
        var settings = SettingsStore.Instance;
        if (settings.Shortcuts.Count == 0) return;
        settings.Shortcuts.Clear();
        settings.Save();
        Reload();
    }

    private void SetOverride(ShortcutDefinition def, string? value)
    {
        var settings = SettingsStore.Instance;
        var has = settings.Shortcuts.TryGetValue(def.Id, out var old);

        if (value == null)
        {
            if (!has) return;
            settings.Shortcuts.Remove(def.Id);
        }
        else
        {
            if (has && string.Equals(old, value, StringComparison.Ordinal)) return;
            settings.Shortcuts[def.Id] = value;
        }

        settings.Save();
        Reload();
    }

    // ------------------------------------------------------------ WPF 按键换算

    /// <summary>当前按键事件对应的操作(没有命中返回 null)</summary>
    public ShortcutAction? Match(KeyEventArgs e) => Match(RealKey(e), Keyboard.Modifiers);

    public ShortcutAction? Match(Key key, ModifierKeys modifiers)
        => _map.Match(ToGesture(key, modifiers));

    public ShortcutGesture ToGesture(Key key, ModifierKeys modifiers)
        => new(key.ToString(), ToModifiers(modifiers));

    public static ShortcutModifiers ToModifiers(ModifierKeys modifiers)
    {
        var result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) result |= ShortcutModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= ShortcutModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= ShortcutModifiers.Shift;
        return result;
    }

    /// <summary>
    /// 这一次按键的**真实键**。
    ///
    /// ★★★ 输入法激活时 WPF 报上来的 e.Key 是 <see cref="Key.ImeProcessed"/>,
    ///   真正按下的键在 <see cref="KeyEventArgs.ImeProcessedKey"/> 里。中文输入法处于激活状态时,
    ///   **不在输入框里**的按键也会整批变成 ImeProcessed(2026-11-25 探针实测) ——
    ///   直接按 e.Key 匹配的话所有快捷键一起哑掉。
    /// ★ Alt 组合(Alt+←/→)的 e.Key 是 <see cref="Key.System"/>, 真键在 SystemKey 里。
    /// ★ 捕获框、播放器、主界面刷新必须共用这一份换算, 否则"设置里能录、实际按了不生效"。
    /// </summary>
    public static Key RealKey(KeyEventArgs e)
    {
        var key = e.Key;
        if (key == Key.ImeProcessed && e.ImeProcessedKey != Key.None) return e.ImeProcessedKey;
        if (key == Key.System && e.SystemKey != Key.None) return e.SystemKey;
        if (key == Key.DeadCharProcessed && e.DeadCharProcessedKey != Key.None) return e.DeadCharProcessedKey;
        return key;
    }

    /// <summary>规范 Key 名 → WPF Key(匹配前用; 未绑定返回 Key.None)</summary>
    public static Key KeyOf(ShortcutGesture gesture)
        => gesture.IsEmpty ? Key.None : (Enum.TryParse<Key>(gesture.Key, out var key) ? key : Key.None);

    /// <summary>给设置页捕获框用: 这个 Key 是否允许作为快捷键主键</summary>
    public static bool IsAllowedKey(Key key)
        => key != Key.None && ShortcutKeys.IsKnown(key.ToString());
}
