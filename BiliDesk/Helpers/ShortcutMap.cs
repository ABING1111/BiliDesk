using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// "当前生效的快捷键表": 默认值 + 用户覆盖项合并后的结果。
///
/// ★ 纯逻辑(不摸 WPF / 不读文件), 这样 BiliDesk.Tests 能直接穷举:
///   · 改绑 / 清空 / 恢复默认后的最终绑定
///   · 两条自定义互相冲突时谁生效
///   · 手改 settings.json 塞进非法键 / 保留组合时怎么回落
///   · Shift 兼容(Shift+↑ 仍算音量+)与 Alt+→ 的精确匹配
/// 运行时只有 ShortcutService 持有它的一份实例。
/// </summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<ShortcutGesture, ShortcutAction> _exact;
    private readonly Dictionary<ShortcutAction, ShortcutGesture> _byAction;

    private ShortcutMap(Dictionary<ShortcutAction, ShortcutGesture> byAction)
    {
        _byAction = byAction;
        _exact = new Dictionary<ShortcutGesture, ShortcutAction>();
        foreach (var (action, gesture) in byAction)
        {
            // 空 = 用户主动清空绑定, 不参与匹配
            if (!gesture.IsEmpty) _exact[gesture] = action;
        }
    }

    /// <summary>每个操作的最终绑定(包括"未绑定"= Empty)</summary>
    public IReadOnlyDictionary<ShortcutAction, ShortcutGesture> ByAction => _byAction;

    public ShortcutGesture GestureOf(ShortcutAction action)
        => _byAction.TryGetValue(action, out var gesture) ? gesture : ShortcutGesture.Empty;

    /// <summary>
    /// 用用户覆盖项构建生效表。
    ///
    /// 规则(顺序很关键):
    ///   1. **先放用户改过的项** —— 自定义优先于默认。以后默认键表调整时, 不会把用户
    ///      已经用了很久的绑定悄悄顶掉;
    ///   2. 覆盖项之间冲突(两条都指向同一个组合)时, 目录里靠前的赢, 靠后的丢弃并回落默认;
    ///   3. 最后补默认值; 默认键若已被某个自定义占用, 该操作只能变成"未绑定"
    ///      (正常改绑会被 UI 拦下, 只有手改配置才会走到这里);
    ///   4. 非法键 / 系统保留组合 / 未知 action id 直接丢弃。
    ///
    /// <paramref name="cleaned"/> 是"清洗后的覆盖字典"(只含仍然有效的项, 已规范化),
    /// 由 SettingsStore 读盘时写回内存, 保证界面显示与运行时匹配看到的是同一份。
    /// </summary>
    public static ShortcutMap Build(IReadOnlyDictionary<string, string>? overrides)
        => Build(overrides, out _);

    public static ShortcutMap Build(
        IReadOnlyDictionary<string, string>? overrides,
        out Dictionary<string, string> cleaned)
    {
        cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        var effective = new Dictionary<ShortcutAction, ShortcutGesture>();
        var placed = new Dictionary<ShortcutGesture, ShortcutAction>();

        // 1) 用户覆盖项优先
        foreach (var def in ShortcutCatalog.All)
        {
            if (overrides == null || !overrides.TryGetValue(def.Id, out var raw) || raw == null) continue;

            var text = raw.Trim();
            if (text.Length == 0)
            {
                // 空串 = 明确清空绑定(与"没出现过"= 默认 key 是两件事, 必须保留在存储里)
                effective[def.Action] = ShortcutGesture.Empty;
                cleaned[def.Id] = "";
                continue;
            }

            if (!ShortcutGesture.TryParse(text, out var gesture)) continue;
            if (ShortcutGesture.IsReserved(gesture)) continue;

            // 与默认相同 = 不需要存(顺便让 IsDefault 的判断保持直觉)
            if (ShortcutGesture.TryParse(def.DefaultGesture, out var defaultGesture)
                && gesture == defaultGesture)
                continue;

            if (placed.ContainsKey(gesture)) continue;
            placed[gesture] = def.Action;
            effective[def.Action] = gesture;
            cleaned[def.Id] = gesture.ToCanonicalString();
        }

        // 2) 再补默认值
        foreach (var def in ShortcutCatalog.All)
        {
            if (effective.ContainsKey(def.Action)) continue;

            if (!ShortcutGesture.TryParse(def.DefaultGesture, out var gesture) || gesture.IsEmpty)
            {
                effective[def.Action] = ShortcutGesture.Empty;
                continue;
            }

            if (placed.ContainsKey(gesture))
            {
                effective[def.Action] = ShortcutGesture.Empty;
                continue;
            }

            placed[gesture] = def.Action;
            effective[def.Action] = gesture;
        }

        return new ShortcutMap(effective);
    }

    /// <summary>
    /// 这次按键命中了哪个操作。
    ///
    /// ★ 精确匹配优先: Alt+→ 是"下一个视频", 不会被"快进"抢走; Ctrl+C 没有任何默认绑定,
    ///   因此继续放行给文本复制(播放器里只读文本能正常复制)。
    /// ★ Shift 兼容回退: 只有当"多按了 Shift"且精确组合不存在时才回退 ——
    ///   Shift+↑ / Shift+↓ 仍是音量, Shift+← / Shift+→ 仍走"快退/快进"分支
    ///   (由播放器再按 Shift 把步长换成 30 秒)。Ctrl / Alt 不做回退, 避免误吞系统组合。
    /// </summary>
    public ShortcutAction? Match(ShortcutGesture pressed)
    {
        if (pressed.IsEmpty) return null;
        if (_exact.TryGetValue(pressed, out var action)) return action;

        if ((pressed.Modifiers & ShortcutModifiers.Shift) != 0)
        {
            var withoutShift = pressed with
            {
                Modifiers = pressed.Modifiers & ~ShortcutModifiers.Shift,
            };
            if (_exact.TryGetValue(withoutShift, out action)) return action;
        }

        return null;
    }

    /// <summary>
    /// 把 <paramref name="candidate"/> 绑给 <paramref name="action"/> 会不会和别的操作撞车。
    /// 返回占用它的操作; 没冲突返回 null。空绑定永远不冲突。
    /// </summary>
    public ShortcutAction? FindConflict(ShortcutAction action, ShortcutGesture candidate)
    {
        if (candidate.IsEmpty) return null;
        return _exact.TryGetValue(candidate, out var owner) && owner != action ? owner : null;
    }
}
