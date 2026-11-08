using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 视频下载器: 把用户选中的清晰度存成用户「下载」文件夹里的一个 MP4。
///
/// 几个关键取舍:
///   1. **两条下载路径**:
///      · DASH 双流(默认) —— 视频/音频各下一路, 再用 <see cref="DashRemuxer"/> 纯拷贝合成
///        MP4。这样 1080P / 4K / 8K 都能下, 不再受 durl 的 720P 上限约束;
///      · durl 单流(回退) —— 音视频已封装在一起, 下载完即可播放, 但上限 720P。
///   2. **必须先请求播放地址再下**: B 站的 CDN 直链带时效签名, 不能提前存起来复用。
///   3. **下载到 .part 再改名**: 中途失败/取消时不会留下一个"看起来正常但其实是半截"的
///      mp4 文件 —— 这是下载器最容易让人踩坑的地方。
///   4. 直链同样有 Referer 校验, 必须带上, 否则会拿到 403。
///   5. **候选地址逐个重试**: 单条 CDN 偶发 403/超时不该让整个任务失败(见 <see cref="DownloadToFileAsync"/>)。
/// </summary>
public static class VideoDownloader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        // 整段视频可能几百兆, 不能用短超时; 这里只约束"连接建立"阶段
        Timeout = Timeout.InfiniteTimeSpan
    };

    static VideoDownloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    /// <summary>
    /// 按用户在下载选择窗里的选择下载视频。返回最终文件路径, 失败返回 null。
    /// 下载进度交给 <see cref="DownloadProgressService"/> 合并显示(见 issue #38),
    /// 完成/失败仍用 Toast 提示。
    /// </summary>
    public static async Task<string?> DownloadAsync(VideoDownloadRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Bvid))
        {
            Svc.Toast.Show("缺少视频信息, 无法下载");
            return null;
        }

        var name = string.IsNullOrWhiteSpace(req.Title) ? req.Bvid : req.Title;
        var path = BuildTargetPath(name);
        var part = path + ".part";

        // 进度不再走全局 Toast —— 多个任务同时下载时同一块 Toast 会被互相覆盖,
        // 而且每次 Show 都重播淡入(每 1% 闪一下)。这里登记到 DownloadProgressService,
        // 由右上角独立进度条合并显示。using 保证成功/失败/取消都会注销任务。
        using var job = Svc.DownloadProgress.Begin(name);

        try
        {
            if (req.UseMuxed)
            {
                // ---- 路径 A: durl 单流(音视频已合并, 上限 720P) ----
                var ok = await DownloadToFileAsync(req.MuxedUrls, part, req.Bvid, job,
                    req.EstimatedBytes, 0, CancellationToken.None);
                if (!ok) return null;
            }
            else
            {
                // ---- 路径 B: DASH 双流 → 本地合流 ----
                if (req.VideoUrls.Count == 0 || req.AudioUrls.Count == 0)
                {
                    Svc.Toast.Show("缺少可用的音视频地址, 无法下载");
                    return null;
                }

                var dir = Path.GetDirectoryName(part) ?? AppPaths.DownloadDir;
                var stem = Path.GetFileName(path);
                var vPart = Path.Combine(dir, stem + ".v.part");
                var aPart = Path.Combine(dir, stem + ".a.part");

                try
                {
                    // 串行下载两路(不并行): 进度条只有一个百分比, 并行时两个任务各报各的
                    // 会让数字来回跳 —— 那正是 issue #38 里被修掉的现象。
                    // 顺序固定"先视频后音频", 视频占绝大部分体积, 用户能更早看到有意义的变化。
                    var vOk = await DownloadToFileAsync(req.VideoUrls, vPart, req.Bvid, job,
                        req.EstimatedBytes, 0, CancellationToken.None);
                    if (!vOk) return null;

                    // 音频接在视频后面报进度, 总长仍是估算的总字节。
                    // ★ 估算拿不到(接口没给 timelength)时不要硬凑百分比: 改成 total=0,
                    //   进度条会显示累计 MB —— 那比一个每次都从 0 重新走到 100% 的假进度诚实。
                    var aTotal = req.EstimatedBytes;
                    var aOffset = req.EstimatedVideoBytes > 0
                        ? req.EstimatedVideoBytes
                        : new FileInfo(vPart).Length;
                    var aOk = await DownloadToFileAsync(req.AudioUrls, aPart, req.Bvid, job,
                        aTotal, aOffset, CancellationToken.None);
                    if (!aOk) return null;

                    // 合流是纯拷贝, 很快; 进度停在 100% 之前由进度条自己收尾
                    if (!DashRemuxer.TryMerge(vPart, aPart, part, out var err))
                    {
                        App.ReportError(new InvalidOperationException(
                            $"下载合流失败 bvid={req.Bvid} qn={req.Qn}: {err}"));

                        // ★ 兜底: 片源结构不认识(如一个 moof 里多 traf)时不要直接失败,
                        //   改下 durl 单流(音视频已合并, 上限 720P) —— 用户要的是"拿到文件",
                        //   清晰度降一档远好于"下载失败"。
                        if (!await TryMuxedFallbackAsync(req, part, job))
                        {
                            Svc.Toast.Show("音视频合成失败: " + (err ?? "未知原因"));
                            return null;
                        }
                    }
                }
                finally
                {
                    TryDelete(vPart);
                    TryDelete(aPart);
                    TryDelete(part + ".tmp");
                }
            }

            // 全部写完才改成正式名字(保证"看得见的文件一定是完整的")
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);

            // 先收掉进度条, 再弹"已保存": 两件事不要抢同一时刻。
            job.Dispose();
            Svc.Toast.Show("已保存 " + Path.GetFileName(path));
            // 系统通知: 下载是后台长任务, 用户这期间多半切去了别的窗口,
            // 应用内 Toast 只在本程序的界面里可见 —— 系统气泡能到他面前。
            // 正文给**目录**(用户要求"提示下载的位置"), 文件名在应用内 Toast 里已经有了。
            var folder = Path.GetDirectoryName(path) ?? AppPaths.DownloadDir;
            TrayService.Instance.Notify("视频下载完成", "已保存到 " + folder);

            if (req.OpenFolderWhenDone) RevealInExplorer(path);
            return path;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            Svc.Toast.Show("下载失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// DASH 合流失败后的兜底: 改下 durl 单流。
    ///
    /// ★ 为什么要有这一步: <see cref="DashRemuxer"/> 只认"一个 moof 一个 traf"这种结构,
    ///   片源换了封装就会失败。这时用户已经在下载窗里选了清晰度、等了半天,
    ///   直接报"合成失败"等于白等 —— 换一条**一定能播**的单流(上限 720P)更符合预期。
    /// ★ 单流地址要重新请求: CDN 直链带时效签名, 不能拿下载选项里那份(可能已过期)。
    /// </summary>
    private static async Task<bool> TryMuxedFallbackAsync(
        VideoDownloadRequest req, string part, DownloadProgressService.Job job)
    {
        try
        {
            var (ok, _, muxedUrl, _) = await Svc.Api.GetDownloadUrlAsync(req.Bvid, req.Cid);
            if (!ok || string.IsNullOrEmpty(muxedUrl)) return false;

            // 合流已经失败, 先把半截输出清掉, 免得下面的下载写进同一个文件
            TryDelete(part);
            return await DownloadToFileAsync(
                new[] { muxedUrl }, part, req.Bvid, job, 0, 0, CancellationToken.None);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把一条流下到指定文件, 按候选顺序逐个重试。
    /// 返回 false 表示所有候选都失败(此时已经弹过提示)。
    ///
    /// ★ 为什么要逐个重试: 接口给的 baseUrl/backupUrl 分属不同 CDN, 实测同一时刻
    ///   有的能拉、有的 403。只试第一条时, 一次偶发失败就等于"下载失败"。
    /// </summary>
    /// <param name="progressOffset">
    /// 这条流开始前已经下完的字节数(音频接在视频后面报进度时用)。
    /// 进度条的总长始终是 <paramref name="estimatedTotal"/>。
    /// </param>
    private static async Task<bool> DownloadToFileAsync(
        IReadOnlyList<string> urls, string path, string bvid,
        DownloadProgressService.Job job, long estimatedTotal, long progressOffset,
        CancellationToken ct)
    {
        if (urls == null || urls.Count == 0)
        {
            Svc.Toast.Show("没有可用的下载地址");
            return false;
        }

        Exception? last = null;
        foreach (var url in urls)
        {
            if (string.IsNullOrEmpty(url)) continue;
            try
            {
                await DownloadOneAsync(url, path, bvid, job, estimatedTotal, progressOffset, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 换下一条线路前先清掉半截文件, 否则重试会接着写进同一个 .part
                TryDelete(path);
                last = ex;
            }
        }

        Svc.Toast.Show("下载失败: " + (last?.Message ?? "所有线路都不可用"));
        return false;
    }

    private static async Task DownloadOneAsync(string url, string path, string bvid,
        DownloadProgressService.Job job, long estimatedTotal, long progressOffset,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // CDN 校验来源页, 不带 Referer 会被拒
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bvid}");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");

        // 总长优先用接口的码率估算(它是两路之和, 单条 Content-Length 只覆盖自己那一半);
        // 估算拿不到时才退回 Content-Length。
        var total = estimatedTotal > 0
            ? estimatedTotal
            : progressOffset + (resp.Content.Headers.ContentLength ?? 0);
        job.Report(progressOffset, total);

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buf = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            // 服务内部按 300ms 节流, 这里每块数据都报即可。
            job.Report(progressOffset + done, total);
        }
    }

    /// <summary>生成不重名的目标路径(标题里的非法字符由 FileNameUtil 清理)</summary>
    private static string BuildTargetPath(string title)
    {
        var dir = AppPaths.DownloadDir;
        try { Directory.CreateDirectory(dir); } catch { /* 后面写入会报错 */ }
        return FileNameUtil.UniquePath(dir, title, ".mp4", "video");
    }

    /// <summary>在资源管理器里定位到刚下好的文件(用户勾了"完成后打开文件夹"时)</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 定位失败不影响下载结果, 静默忽略
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响结果 */ }
    }
}
