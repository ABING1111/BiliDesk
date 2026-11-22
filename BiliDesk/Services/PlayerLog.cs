using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BiliDesk.Services;

/// <summary>
/// 播放器诊断日志(2026-11-22, mpv 迁移)。
///
/// 为什么重做:
///   · 换成 mpv 之后, 原来的 `vlc-debug.txt` 名字已经不准确, 而且**没有时间戳** ——
///     出问题时无法把 App 侧的事件与 mpv 原生日志对齐;
///   · 原来超过 256KB 直接删文件, 关键上下文会丢; 现在改成 4MB 滚动, 保留上一份;
///   · mpv 的 warn/error 日志(重连、TLS reset 等)以前只有设置环境变量才看得到,
///     现在默认就落到这里 —— 用户反馈问题时只要发一份 player-debug.txt。
///
/// 兼容: 仍然写在 %LOCALAPPDATA%\BiliDesk\ 下, 只是文件名换成 player-debug.txt。
/// </summary>
public static class PlayerLog
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppPaths.DataDir, "player-debug.txt");

    /// <summary>写入多行文本; 每行自动加时间戳(App 侧日志与 mpv 日志共用一条时间轴)。</summary>
    public static void Write(string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        var stamped = WithTimestamps(content, DateTime.Now);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) Rotate();
                File.AppendAllText(FilePath, stamped, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响播放
        }
    }

    /// <summary>结构化快照: 每行 key=value, 一次写一组。</summary>
    public static void Snapshot(string tag, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(tag).Append(']');
        foreach (var kv in fields) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
        sb.Append('\n');
        Write(sb.ToString());
    }

    /// <summary>会话头: 版本/引擎/原生库位置, 排查时先看这几行。</summary>
    public static void SessionHeader(string engine, string engineVersion, string nativePath)
    {
        Write($"\n===== session {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n" +
              $"app={App.AppVersion} os={Environment.OSVersion}\n" +
              $"engine={engine} version={engineVersion}\n" +
              $"native={nativePath} exists={File.Exists(nativePath)}\n" +
              $"base={AppContext.BaseDirectory}\n");
    }

    /// <summary>
    /// 给每一行加 "[HH:mm:ss.fff] " 前缀; 空行原样保留。
    /// 抽成纯函数是为了能单测(日志格式坏了, 排查时最难受)。
    /// </summary>
    public static string WithTimestamps(string content, DateTime now)
    {
        var stamp = "[" + now.ToString("HH:mm:ss.fff") + "] ";
        var sb = new StringBuilder(content.Length + 16 * 4);
        var start = 0;
        while (start <= content.Length)
        {
            var end = content.IndexOf('\n', start);
            var line = end < 0 ? content.Substring(start) : content.Substring(start, end - start);
            line = line.TrimEnd('\r');
            if (line.Length > 0)
            {
                sb.Append(stamp).Append(line);
                if (end >= 0) sb.Append('\n');
            }
            else if (end >= 0)
            {
                sb.Append('\n');
            }

            if (end < 0) break;
            start = end + 1;
        }
        return sb.ToString();
    }

    private static void Rotate()
    {
        try
        {
            var backup = FilePath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch
        {
            try { File.Delete(FilePath); } catch { }
        }
    }
}
