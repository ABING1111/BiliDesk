using System;

namespace BiliDesk.Playback;

/// <summary>
/// 播放引擎工厂(2026-11-22, feat/mpv-core)。
///
/// ★ 当前只有 mpv 实现 —— 迁移的终点就是它。VLC 实现不会再补, 旧的 PlayerWindow
///   会在 P2 直接切到本接口, 原来的 VLC 代码随 P4 一起删除。
/// </summary>
public static class PlayerEngineFactory
{
    /// <summary>该引擎在当前机器上是否可用(mpv 需要 libmpv-2.dll 就位)。</summary>
    public static bool IsAvailable(PlayerEngineKind kind) => kind switch
    {
        PlayerEngineKind.Mpv => MpvEngine.IsAvailable,
        _ => false,
    };

    public static IPlayerEngine Create(PlayerEngineKind kind, PlayerEngineOptions options) => kind switch
    {
        PlayerEngineKind.Mpv => new MpvEngine(options),
        _ => throw new NotSupportedException($"播放引擎 {kind} 不可用(当前只支持 mpv)"),
    };
}
