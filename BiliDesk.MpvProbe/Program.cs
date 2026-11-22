using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using BiliDesk.Mpv;
using BiliDesk.MpvProbe.Ui;

namespace BiliDesk.MpvProbe;

internal static class Program
{
    // mpv core 探针(2026-11-22, feat/mpv-core)
    //
    // 两种模式:
    //   无 UI(默认): 验证 libmpv 能加载/解码/seek/变速/读时间, 以及事件是否按时到来。
    //   --ui       : 打开 PoC 窗口 —— mpv 画面嵌进 HwndHost, 控制栏放在独立覆盖层窗口里。
    //
    // 用法:
    //   dotnet run --project BiliDesk.MpvProbe -- [--ui] [--url <url>] [--audio <url>]
    //                                             [--start <ms>] [--seconds 10] [--speed 2] [--verbose]
    // 默认播本地 vcache 里的短视频(不需要网络)。

    [STAThread]
    private static int Main(string[] args)
    {
        string? url = null;
        string? audio = null;
        long startMs = 0;
        var seconds = 10;
        var speed = 0.0;
        var verbose = false;
        var ui = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length: url = args[++i]; break;
                case "--audio" when i + 1 < args.Length: audio = args[++i]; break;
                case "--start" when i + 1 < args.Length: long.TryParse(args[++i], out startMs); break;
                case "--seconds" when i + 1 < args.Length: int.TryParse(args[++i], out seconds); break;
                case "--speed" when i + 1 < args.Length:
                    double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                    break;
                case "--verbose": verbose = true; break;
                case "--ui": ui = true; break;
            }
        }

        url ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliDesk", "vcache", "v4_BV1yNxk6FEZA_80.mp4");

        if (!MpvPlayer.IsAvailable)
        {
            Console.WriteLine($"[probe] libmpv 不存在: {Path.Combine(AppContext.BaseDirectory, "libmpv", "win-x64", "libmpv-2.dll")}");
            return 2;
        }

        if (ui)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MpvTestWindow(new MpvMediaRequest { Url = url, AudioUrl = audio, StartMs = startMs }));
            return 0;
        }

        return RunHeadless(url, audio, startMs, seconds, speed, verbose);
    }

    private static int RunHeadless(string url, string? audio, long startMs, int seconds, double speed, bool verbose)
    {
        Console.WriteLine($"[probe] load {url}");
        if (!string.IsNullOrEmpty(audio)) Console.WriteLine($"[probe] audio-file {audio}");

        using var player = new MpvPlayer(
            new MpvGlobalOptions { Headless = true, VerboseLog = verbose },
            msg => Console.WriteLine(msg));

        player.FileLoaded += (_, _) => Console.WriteLine("[probe] file-loaded");
        player.EndFile += (_, e) => Console.WriteLine($"[probe] end-file reason={e.Reason} eof={e.IsEof} error={e.Error}");
        player.PropertyChanged += (_, e) =>
        {
            if (e.Name is "pause" or "eof-reached" or "idle-active")
                Console.WriteLine($"[probe] prop {e.Name}={e.Value}");
        };

        var rc = player.Load(new MpvMediaRequest { Url = url, AudioUrl = audio, StartMs = startMs });
        if (rc < 0) Console.WriteLine($"[probe] loadfile failed: {MpvPlayer.ErrorText(rc)}");

        var ticks = seconds * 2;
        for (var t = 1; t <= ticks; t++)
        {
            Thread.Sleep(500);
            if (t == 6) PlayerLog("seek +10s", player.SeekAbsolute(player.TimePos + 10));
            if (t == 12 && speed > 0)
            {
                player.Speed = speed;
                Console.WriteLine($"[probe] speed -> {speed:0.##}");
            }
            if (t == 18 && speed > 0)
            {
                player.Speed = 1;
                Console.WriteLine("[probe] speed -> 1");
            }

            Console.WriteLine($"[probe] t={t / 2.0:F1}s pos={player.TimePos:F2} dur={player.Duration:F2} " +
                              $"speed={player.Speed:0.##} paused={player.IsPaused} eof={player.EofReached} " +
                              $"frames={player.GetString("estimated-frame-number")}");

            if (t == 4)
            {
                Console.WriteLine($"[probe] codec video={player.GetString("video-codec")} " +
                                  $"audio={player.GetString("audio-codec-name")} " +
                                  $"rate={player.GetString("audio-params/samplerate")} " +
                                  $"ch={player.GetString("audio-params/channel-count")} " +
                                  $"tracks={player.GetString("track-list/count")}");
            }
        }

        Console.WriteLine("[probe] done");
        return 0;
    }

    private static void PlayerLog(string what, int rc)
    {
        if (rc < 0) Console.WriteLine($"[probe] {what} failed: {MpvPlayer.ErrorText(rc)}");
        else Console.WriteLine($"[probe] {what}");
    }
}
