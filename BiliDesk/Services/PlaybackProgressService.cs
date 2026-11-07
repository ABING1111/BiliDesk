using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BiliDesk.Helpers;

namespace BiliDesk.Services;

/// <summary>一条本机播放进度记录。</summary>
public sealed class PlaybackProgressEntry
{
    /// <summary>上次看到的位置(毫秒)</summary>
    public long PosMs { get; set; }

    /// <summary>视频总时长(毫秒; 0 = 当时还不知道)</summary>
    public long DurMs { get; set; }

    /// <summary>最后更新时间(Unix 秒)</summary>
    public long UpdatedAt { get; set; }
}

/// <summary>续播进度来源(只用于 Toast 文案, 不参与逻辑)。</summary>
public enum ResumeSource
{
    None = 0,
    Local = 1,
    Cloud = 2,
}

/// <summary>选定的续播方案。</summary>
public sealed class ResumePlan
{
    public long PosMs { get; init; }
    public ResumeSource Source { get; init; }
}

/// <summary>
/// 本机播放进度记录: 把"播到哪"落盘, 下次打开同一条视频时自动续播。
///
/// 为什么在云端上报之外还要存本地:
///   · 未登录 / 网络失败时云端历史不可用, 本机记录仍然能续;
///   · B 站云端上报名义上 15 秒一次, 关窗/崩溃时最多可能丢十几秒, 本机记录更准;
///   · B 站 playurl 返回的 last_play_time 是**上次离开时**的位置, 本机记录可以做到
///     暂停/拖动/关窗都立刻落盘。
///
/// 键 = bvid + cid: 多 P 视频每个分 P 各存一条, 不会互相覆盖。
/// 落盘位置: %LocalAppData%\BiliDesk\playback_progress.json。
/// 所有读写失败都静默忽略 —— 进度记录是锦上添花, 绝不能影响播放。
/// </summary>
public sealed class PlaybackProgressService
{
    public static PlaybackProgressService Instance { get; } = new();

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playback_progress.json");

    /// <summary>低于这个位置不续播(刚点开就关掉的噪声)。</summary>
    private const long MinResumeMs = 5_000;

    /// <summary>距结尾小于这个值就不续播/直接算看完, 避免下次一打开就播完。</summary>
    private const long TailGuardMs = 10_000;

    /// <summary>内存里最多保留多少条(超出按最后更新时间淘汰)。</summary>
    private const int MaxEntries = 1_000;

    /// <summary>超过多少天的记录清理掉。</summary>
    private const int MaxAgeDays = 90;

    /// <summary>同一位置附近多少毫秒内不重复写内存。</summary>
    private const long SaveGranularityMs = 3_000;

    /// <summary>两次真正写盘至少间隔多久(播放中的心跳每 500ms 一次)。</summary>
    private const long FlushIntervalSeconds = 10;

    private readonly object _gate = new();
    private Dictionary<string, PlaybackProgressEntry>? _map;
    private bool _dirty;
    private long _lastFlushAt;

    private Dictionary<string, PlaybackProgressEntry> Map
    {
        get
        {
            if (_map != null) return _map;
            _map = Load();
            return _map;
        }
    }

    private static string Key(string bvid, long cid) => bvid + ":" + cid;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>取本机记录; 没有/不可用返回 null。</summary>
    public PlaybackProgressEntry? GetLocal(string bvid, long cid)
    {
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return null;
        lock (_gate)
        {
            return Map.TryGetValue(Key(bvid, cid), out var e) ? e : null;
        }
    }

    /// <summary>
    /// 选择本次要从哪里续播。
    ///
    /// 优先级: 本机记录 &gt; B 站 playurl 返回的 last_play_time。
    /// 本机记录是"这台设备最后一次看到的位置", 比云端上报更及时; 云端只在本机没有记录时兜底。
    /// </summary>
    public ResumePlan? ChooseResume(string bvid, long cid, long durationMs,
        long cloudMs, long cloudCid, bool autoResume)
    {
        if (!autoResume) return null;
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return null;

        var local = GetLocal(bvid, cid);
        var localValid = local != null && IsValidPosition(local.PosMs, durationMs);
        var cloudValid = cloudCid == cid && cloudMs > 0 && IsValidPosition(cloudMs, durationMs);

        if (localValid)
        {
            // 本机记录通常更及时; 但如果它已经很久没更新, 而 B 站云端进度明显不同,
            // 说明用户可能中途在网页端看过 —— 这时以云端为准, 才叫"与 B 站同步"。
            var localAge = Now - local!.UpdatedAt;
            if (cloudValid && localAge > 3_600 && Math.Abs(cloudMs - local.PosMs) > 30_000)
                return new ResumePlan { PosMs = cloudMs, Source = ResumeSource.Cloud };
            return new ResumePlan { PosMs = local.PosMs, Source = ResumeSource.Local };
        }
        if (cloudValid)
            return new ResumePlan { PosMs = cloudMs, Source = ResumeSource.Cloud };
        return null;
    }

    /// <summary>进度是否值得续播: 不能太靠前, 也不能已经播到片尾。</summary>
    private static bool IsValidPosition(long posMs, long durationMs)
    {
        if (posMs < MinResumeMs) return false;
        if (durationMs <= 0) return true;
        if (posMs >= durationMs - TailGuardMs) return false;
        // 兜底: 某些接口给的时长偏短/偏长, 超过 98% 一律算看完。
        return posMs <= durationMs * 0.98;
    }

    /// <summary>
    /// 播放心跳里调用: 记录当前进度(内部节流, 不会每 500ms 写盘)。
    /// </summary>
    public void MaybeSave(string bvid, long cid, long posMs, long durMs,
        bool isLive, bool isLocalPlayback)
    {
        if (isLive || isLocalPlayback) return;
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return;

        var key = Key(bvid, cid);
        var now = Now;
        lock (_gate)
        {
            var map = Map;

            // 刚起播/被拖回开头: 旧进度不再成立, 删掉, 否则下次还会跳回老位置。
            if (posMs < 3_000)
            {
                if (map.Remove(key)) _dirty = true;
                return;
            }

            // 已经播到片尾附近: 算看完, 下次从头开始。
            if (durMs > 0 && posMs >= durMs - TailGuardMs)
            {
                if (map.Remove(key)) { _dirty = true; FlushLocked(); }
                return;
            }

            if (map.TryGetValue(key, out var old) &&
                Math.Abs(old.PosMs - posMs) < SaveGranularityMs &&
                now - old.UpdatedAt < 60)
                return;

            map[key] = new PlaybackProgressEntry
            {
                PosMs = posMs,
                DurMs = durMs > 0 ? durMs : old?.DurMs ?? 0,
                UpdatedAt = now,
            };
            _dirty = true;

            if (now - _lastFlushAt >= FlushIntervalSeconds)
                FlushLocked();
        }
    }

    /// <summary>暂停 / 拖动结束 / 关窗: 立即记录并落盘。</summary>
    public void SaveNow(string bvid, long cid, long posMs, long durMs,
        bool isLive, bool isLocalPlayback)
    {
        if (isLive || isLocalPlayback) return;
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return;

        var key = Key(bvid, cid);
        var now = Now;
        lock (_gate)
        {
            var map = Map;
            if (posMs < 3_000 || (durMs > 0 && posMs >= durMs - TailGuardMs))
            {
                if (map.Remove(key)) _dirty = true;
            }
            else
            {
                map[key] = new PlaybackProgressEntry
                {
                    PosMs = posMs,
                    DurMs = durMs > 0 ? durMs : 0,
                    UpdatedAt = now,
                };
                _dirty = true;
            }
            FlushLocked();
        }
    }

    /// <summary>播完 / 用户选择从头重播: 删除这条记录。</summary>
    public void MarkCompleted(string bvid, long cid)
    {
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return;
        lock (_gate)
        {
            if (Map.Remove(Key(bvid, cid)))
            {
                _dirty = true;
                FlushLocked();
            }
        }
    }

    /// <summary>把内存里的改动写盘。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        if (!_dirty) return;
        if (string.IsNullOrEmpty(AppPaths.DataDir)) return;   // 宁可不存, 也不写相对路径

        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            PruneLocked();
            var json = JsonSerializer.Serialize(Map);
            // ★ 2026-11-07 改原子写: 直接覆写时, 播放中崩溃/断电会留下半个 progress.json,
            //   下次启动解析失败 = 全部本机进度丢失。与其余 JSON 落盘同一套惯用法(AtomicFile)。
            AtomicFile.WriteAllText(FilePath, json);
            _dirty = false;
            _lastFlushAt = Now;
        }
        catch
        {
            // 写不进去只影响下次续播, 不打扰用户; _dirty 保持 true, 下次再试。
        }
    }

    private Dictionary<string, PlaybackProgressEntry> Load()
    {
        try
        {
            if (string.IsNullOrEmpty(AppPaths.DataDir)) return new Dictionary<string, PlaybackProgressEntry>();
            if (!File.Exists(FilePath)) return new Dictionary<string, PlaybackProgressEntry>();

            var map = JsonSerializer.Deserialize<Dictionary<string, PlaybackProgressEntry>>(
                File.ReadAllText(FilePath))
                ?? new Dictionary<string, PlaybackProgressEntry>();

            // 读入时就清一遍过期/超量记录: 否则 GetLocal 可能把几个月前的位置当成"上次进度"。
            var cutoff = Now - MaxAgeDays * 24 * 60 * 60;
            var expired = new List<string>();
            foreach (var kv in map)
                if (kv.Value == null || kv.Value.UpdatedAt < cutoff) expired.Add(kv.Key);
            foreach (var k in expired) map.Remove(k);

            if (map.Count > MaxEntries)
            {
                var ordered = new List<KeyValuePair<string, PlaybackProgressEntry>>(map);
                ordered.Sort((a, b) => a.Value.UpdatedAt.CompareTo(b.Value.UpdatedAt));
                var remove = map.Count - MaxEntries;
                for (var i = 0; i < remove && i < ordered.Count; i++)
                    map.Remove(ordered[i].Key);
            }

            _lastFlushAt = Now;
            return map;
        }
        catch
        {
            // 文件损坏/无权限 = 没有记录, 不影响播放。
            return new Dictionary<string, PlaybackProgressEntry>();
        }
    }

    /// <summary>清理过期记录与超量记录(都在持锁时调用)。</summary>
    private void PruneLocked()
    {
        var map = Map;
        if (map.Count == 0) return;

        var cutoff = Now - MaxAgeDays * 24 * 60 * 60;
        var expired = new List<string>();
        foreach (var kv in map)
            if (kv.Value.UpdatedAt < cutoff) expired.Add(kv.Key);
        foreach (var k in expired) map.Remove(k);

        if (map.Count <= MaxEntries) return;

        // 超出上限: 按最后更新时间从旧到新删。
        var ordered = new List<KeyValuePair<string, PlaybackProgressEntry>>(map);
        ordered.Sort((a, b) => a.Value.UpdatedAt.CompareTo(b.Value.UpdatedAt));
        var remove = map.Count - MaxEntries;
        for (var i = 0; i < remove && i < ordered.Count; i++)
            map.Remove(ordered[i].Key);
    }
}
