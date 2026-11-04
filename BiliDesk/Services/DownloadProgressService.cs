using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Services;

/// <summary>
/// 下载进度中心(2026-11-04, issue #38: 同时下载多个视频时进度提示每 1% 闪烁、来回交替)。
///
/// 为什么会出问题:
///   以前每个下载任务都直接往**同一块** Toast 写"下载中 X%"。同时下两个视频时,
///   两条进度按各自的 1.5 秒节奏轮流覆盖同一个文本框 —— 百分比自然来回跳;
///   而 MainWindow 每次 Show 又重播一遍"透明度 0 → 1"的淡入, 于是每来一条更新就闪一下。
///
/// 现在的做法:
///   · 任务统一在这里登记, 进度**只存数据、不碰 UI**;
///   · 多个任务按开始顺序合并成一条稳定文案(2 个就是 "下载中 2 个 · 45% / 12%");
///   · 上报走 300ms 全局节流, 不会每个 80KB 数据块都触发一次界面刷新;
///   · UI 侧(DownloadProgressBanner)订阅 <see cref="Changed"/>, 自己负责"只在出现时淡入"。
///
/// 生命周期: <see cref="Begin"/> 返回的 <see cref="Job"/> 必须在 finally/using 里 Dispose,
/// 否则下载失败时进度条会永远停在最后一条(和以前 Toast 卡住是同一类问题)。
/// </summary>
public sealed class DownloadProgressService
{
    public static DownloadProgressService Instance { get; } = new();

    /// <summary>活动任务有变化时触发。可能在后台线程触发, 订阅方要自己切回 UI 线程。</summary>
    public event Action? Changed;

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private long _lastRaiseTick;

    private DownloadProgressService() { }

    /// <summary>当前活动任务数(0 表示没有下载在跑)。</summary>
    public int Count { get { lock (_gate) return _jobs.Count; } }

    /// <summary>给 UI 直接显示的文案。空串表示没有活动任务, 进度条应该隐藏。</summary>
    public string Summary { get { lock (_gate) return BuildSummaryLocked(); } }

    /// <summary>登记一个下载任务。返回的句柄负责上报进度和结束时注销。</summary>
    public Job Begin(string title)
    {
        var job = new Job(this, title ?? "");
        lock (_gate) _jobs.Add(job);
        RaiseChanged();
        return job;
    }

    private void Remove(Job job)
    {
        lock (_gate)
        {
            // Dispose 可重入: 成功路径会先手动收一次, 外面的 using 还会再调一次。
            if (!_jobs.Remove(job)) return;
        }
        RaiseChanged();
    }

    private void Report(Job job, long done, long total)
    {
        bool raise;
        lock (_gate)
        {
            if (!_jobs.Contains(job)) return;
            job.Done = done;
            job.Total = total;

            // 全局节流: 同时下多个视频时, 避免几块数据就刷一次界面。
            // 用 Environment.TickCount64 而不是 DateTime: 不受系统时间调整影响。
            var now = Environment.TickCount64;
            raise = now - _lastRaiseTick >= 300;
            if (raise) _lastRaiseTick = now;
        }
        if (raise) RaiseChanged();
    }

    private string BuildSummaryLocked()
    {
        if (_jobs.Count == 0) return "";
        if (_jobs.Count == 1) return _jobs[0].Text;

        // 任务太多时一行放不下: 改成"总进度", 避免文案长到把右上角撑爆。
        if (_jobs.Count > 3)
        {
            long done = 0, total = 0;
            foreach (var job in _jobs)
            {
                done += job.Done;
                total += job.Total;
            }
            return total > 0
                ? $"下载中 {_jobs.Count} 个 · 总进度 {done * 100 / total}%"
                : $"下载中 {_jobs.Count} 个…";
        }

        return $"下载中 {_jobs.Count} 个 · " + string.Join(" / ", _jobs.Select(j => j.ShortText));
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 进度显示只是反馈: 某个窗口正在关闭时抛异常, 不能把下载流程带崩。
        }
    }

    /// <summary>单个下载任务。进度上报与注销都走这里, 外部拿不到内部状态。</summary>
    public sealed class Job : IDisposable
    {
        private readonly DownloadProgressService _owner;
        internal long Done;
        internal long Total;

        internal Job(DownloadProgressService owner, string title)
        {
            _owner = owner;
            Title = title;
        }

        public string Title { get; }

        /// <summary>
        /// 上报已下载/总字节数(总长未知时 total 传 0)。内部已按 300ms 节流,
        /// 可以每读一块数据就调, 不会把 UI 打爆。
        /// </summary>
        public void Report(long done, long total) => _owner.Report(this, done, total);

        /// <summary>注销任务; 真正结束后一定要调用(using 最省事)。</summary>
        public void Dispose() => _owner.Remove(this);

        internal string Text => Total > 0
            ? $"下载中 {Done * 100 / Total}%"
            : Done > 0 ? $"下载中 {Done / 1024 / 1024} MB" : "准备下载…";

        internal string ShortText => Total > 0
            ? $"{Done * 100 / Total}%"
            : Done > 0 ? $"{Done / 1024 / 1024} MB" : "准备中";
    }
}
