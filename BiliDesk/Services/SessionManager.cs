using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>登录会话管理: 负责登录 Cookie 的持久化与分发</summary>
public class SessionManager
{
    public static SessionManager Instance { get; } = new();

    private static string FilePath => Path.Combine(AppPaths.DataDir, "session.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public Session Current { get; private set; } = new();

    /// <summary>会话变化(登录/退出)事件</summary>
    public event Action? Changed;

    public bool HasLogin => !string.IsNullOrEmpty(Current.SessData);

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath), JsonOpts) ?? new Session();
            }
        }
        catch
        {
            Current = new Session();
        }
    }

    public void Save()
    {
        try
        {
            // 先写临时文件再原子替换: 直接覆写的话, 写入瞬间崩溃/断电会留下半个
            // session.json —— JSON 解析失败 = 登录态丢失, 用户要重新扫码。
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>从 WebView2 读取到的 Cookie 集合更新会话(登录成功后调用)</summary>
    public void UpdateFromCookies(IReadOnlyDictionary<string, string> cookies)
    {
        string? Get(string name) => cookies.TryGetValue(name, out var v) ? v : null;

        Current.SessData = Get("SESSDATA") ?? Current.SessData;
        Current.BiliJct = Get("bili_jct") ?? Current.BiliJct;
        Current.DedeUserID = Get("DedeUserID") ?? Current.DedeUserID;
        Current.DedeUserIDCkMd5 = Get("DedeUserID__ckMd5") ?? Current.DedeUserIDCkMd5;
        Current.Buvid3 = Get("buvid3") ?? Current.Buvid3;
        Save();
        Changed?.Invoke();
    }

    public void SetBuvid3(string buvid3)
    {
        if (!string.IsNullOrEmpty(Current.Buvid3)) return;
        Current.Buvid3 = buvid3;
        Save();
    }

    /// <summary>退出登录: 清空本地会话</summary>
    public void Clear()
    {
        Current = new Session();
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 忽略
        }
        Changed?.Invoke();
    }

    /// <summary>构造请求用的 Cookie 头</summary>
    public string BuildCookieHeader()
    {
        var parts = new List<string>();
        void Add(string? name, string? value)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                parts.Add($"{name}={value}");
        }
        Add("SESSDATA", Current.SessData);
        Add("bili_jct", Current.BiliJct);
        Add("DedeUserID", Current.DedeUserID);
        Add("DedeUserID__ckMd5", Current.DedeUserIDCkMd5);
        Add("buvid3", Current.Buvid3);
        return string.Join("; ", parts);
    }
}