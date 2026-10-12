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

    /// <summary>
    /// 磁盘上的那一份是否已经读进内存了。
    ///
    /// ★ 这是防"把登录态写没"的关键闸门: `Current` 的初值是全 null 的 `new Session()`,
    ///   只要没 Load 过, 任何一次 Save() 都会把磁盘上完好的登录态覆盖成空。
    ///   (踩过两次: 一次是探针直接在真数据目录上跑, 一次是守卫只挡了"三者全空"那种情形,
    ///    挡不住"Current 里只有新补的设备指纹、SessData 是 null"这种更隐蔽的覆盖。)
    /// </summary>
    private bool _loaded;

    /// <summary>
    /// 磁盘上那份文件在 Load() 时**读不懂**(JSON 坏了 / 被手改坏)。
    ///
    /// ★★ 它和 `!_loaded` 是两回事, 必须分开记, 否则会造出一个"用户永远登不上"的死局:
    ///   解析失败时既不置 _loaded(怕拿空会话覆盖那份还能人工救的文件), 又会把之后
    ///   **真实登录**的写入一起挡掉 —— 扫码明明成功了, 凭据却一个字落不了盘, 重启还是未登录,
    ///   而用户自己没有任何办法(得手动去 `%LocalAppData%\BiliDesk` 删那个文件)。
    ///   (2026-10-02 探针 `bd-probe-sessionguard` 场景 G 实测: SessData 长度 0、文件始终是坏的那段。)
    ///
    ///   真正的解法是"把坏文件挪到一边、然后正常写": 坏数据保留下来(人工还有救),
    ///   用户也能重新登录 —— 既没有覆盖, 也没有把人卡死。
    /// </summary>
    private bool _diskUnreadable;

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
                _loaded = true;
                _diskUnreadable = false;
            }
            else
            {
                // 文件不存在 = 确实还没登录过, 这份空会话就是"真相", 允许往后落盘
                Current = new Session();
                _loaded = true;
                _diskUnreadable = false;
            }
        }
        catch
        {
            // 解析失败: **不**置 _loaded(别立刻拿空会话去覆盖), 但记下"这份读不懂"。
            // 磁盘文件先留着 —— 等真的有一次要落盘的写入时, 由 ParkUnreadable() 挪走它。
            Current = new Session();
            _diskUnreadable = true;
        }
    }

    /// <summary>
    /// 把读不懂的那份 session.json 挪到一边(改名保留), 让后续写入能正常进行。
    ///
    /// 为什么是"改名"而不是"删掉": 那份文件可能是用户**唯一**的登录凭据,
    /// 只是恰好坏了 —— 留着它, 用户/作者还有机会从里面把 SESSDATA 捡回来。
    /// 改名之后磁盘上就没有可被覆盖的"好数据"了, 闸门一/二也不该再拦(见 Save)。
    /// </summary>
    private void ParkUnreadable()
    {
        if (!_diskUnreadable) return;
        try
        {
            if (File.Exists(FilePath))
            {
                var parked = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(FilePath, parked, overwrite: true);
            }
        }
        catch
        {
            // 挪不动也不能把用户卡死: 下面照样允许写入(本来那份就已经读不懂了)
        }
        _diskUnreadable = false;
        _loaded = true;   // 磁盘上那份已经不在了, 现在这份空会话就是"真相"
    }

    public void Save()
    {
        try
        {
            // 磁盘上那份读不懂 -> 先挪开再写。不这么做的话, 下面的闸门一会把
            // **真实登录**的写入也挡掉, 用户就是"提示登录成功、重启仍未登录"且无法自救。
            ParkUnreadable();

            // ★★ 闸门一: 没 Load 过就绝不落盘。
            //   调用方想改会话, 前提是先知道当前会话是什么。没读过就写, 一定是覆盖。
            if (!_loaded)
            {
                App.ReportError(new InvalidOperationException(
                    "SessionManager.Save() 在 Load() 之前被调用, 已拒绝(防止覆盖磁盘上的登录态)"));
                return;
            }

            // ★★ 闸门二: 内存里没有登录态, 但磁盘上**有** —— 这是"要把别人登出"的形状, 拒绝。
            //
            //   为什么这条必须有: 上一版只挡了"SessData/Buvid3/Buvid4 三者全空",
            //   而真实事故里的状态是 **SessData=null + Buvid3/Buvid4 有值**
            //   (某个流程只补了设备指纹就 Save) —— 那时三者不全空, 守卫放行了,
            //   于是磁盘上好好的登录态被一份"未登录"覆盖掉, 用户被登出。
            //
            //   合法地想清空登录只有一条路: Clear()(删文件)。而"用户本来就是未登录"
            //   这一情形在磁盘上也是没有 SessData 的, 不会命中这条。
            if (string.IsNullOrEmpty(Current.SessData) && OnDiskHasLogin())
            {
                App.ReportError(new InvalidOperationException(
                    "SessionManager.Save() 试图用一份未登录的会话覆盖磁盘上已登录的会话, 已拒绝"));
                return;
            }

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

    /// <summary>磁盘上那份 session.json 里有没有登录态(读不动/不存在都算没有)</summary>
    private static bool OnDiskHasLogin()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath), JsonOpts);
            return s != null && !string.IsNullOrEmpty(s.SessData);
        }
        catch
        {
            return false;
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
        Current.Buvid4 = Get("buvid4") ?? Current.Buvid4;
        // ★★★ 写操作风控 Cookie(2026-11-12): 登录时浏览器里明明有, 这里以前直接丢了,
        //   于是"换了扫码登录之后点赞报风控"。见 Models.Session 里那一段的说明。
        //   用 ?? 保留旧值: 新一轮登录没带这几个键时不该把已有值清掉。
        Current.BNut = Get("b_nut") ?? Current.BNut;
        Current.Sid = Get("sid") ?? Current.Sid;
        Current.BuvidFp = Get("buvid_fp") ?? Current.BuvidFp;
        Current.Blsid = Get("b_lsid") ?? Current.Blsid;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// 写操作风控 Cookie 是否已经齐了(见 <see cref="Models.Session"/> 里那一段说明)。
    ///
    /// ★★ 2026-10-12 修: 原来这里是 `b_nut` 和 `sid` **都**非空才算齐, 而这个判据被
    ///   <see cref="BackfillRiskCookies"/> 当入口守卫用 ⇒ 老用户 session.json 里
    ///   `sid` 一直有值(旧登录流程就存了), 于是恒为 true, **整个补齐方法从上线那天起
    ///   就没执行过一次**(包括上一轮新加的"自造 b_nut"分支, 也是死代码)。
    ///   用户的现象正是"换了扫码登录后点赞报风控, 而代码明明补了 Cookie"。
    /// ⇒ 判据收紧到 `b_nut` 一项: 它才是写接口风控最常被点名、且唯一可能缺失的那一个。
    /// </summary>
    public bool HasRiskCookies => !string.IsNullOrEmpty(Current.BNut);

    /// <summary>
    /// 从一份浏览器 Cookie 里**只补风控那几项**, 不动登录凭据。
    ///
    /// ★ 为什么需要它(2026-11-12): 风控 Cookie 是后来才加的, 而用户的 session.json
    ///   是**上一版登录流程**写的 —— 里面根本没有这些字段。若不补, 就得让用户
    ///   "退出登录再登一次"才能用上点赞, 那对这个修复来说太贵。
    ///   而这些值在 WebView2 的 Profile 里本来就躺着(扫码/账密登录都写过),
    ///   启动时读一次即可, 用户无感。
    /// </summary>
    public void BackfillRiskCookies(IReadOnlyDictionary<string, string> cookies)
    {
        if (!HasLogin || HasRiskCookies) return;
        string? Get(string name) => cookies.TryGetValue(name, out var v) ? v : null;

        var bNut = Get("b_nut");
        var sid = Get("sid");

        // ★★ b_nut 是"登录时刻的 Unix 时间戳", **不是随机值** —— 服务端拿它算
        //   "这个会话存在多久了"。所以 Profile 里没有时也能自己造一个:
        //   本次启动时刻就是合法的登录时刻(老用户的 session.json 缺这个字段,
        //   本质上就是"上一版登录流程没记", 而不是"用户没有登录时刻")。
        //   (2026-10-12: 实测用户 session.json 里 b_nut 为空、sid 有值,
        //     而 b_nut 恰好是写接口风控最常被点名的那一项。)
        if (string.IsNullOrEmpty(bNut) && string.IsNullOrEmpty(Current.BNut))
            bNut = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        if (string.IsNullOrEmpty(bNut) && string.IsNullOrEmpty(sid)) return;  // Profile 里也没有, 放弃

        Current.BNut = bNut ?? Current.BNut;
        Current.Sid = sid ?? Current.Sid;
        Current.BuvidFp = Get("buvid_fp") ?? Current.BuvidFp;
        Current.Blsid = Get("b_lsid") ?? Current.Blsid;
        Save();
        Changed?.Invoke();
    }

    public void SetBuvid3(string buvid3)
    {
        if (!string.IsNullOrEmpty(Current.Buvid3)) return;
        Current.Buvid3 = buvid3;
        Save();
    }

    /// <summary>
    /// 记下设备指纹的第二半(buvid4)。
    ///
    /// ★ 和 buvid3 不同: **每次拿到新的都要覆盖存下来**, 不能像 SetBuvid3 那样"有就跳过"。
    ///   因为 `x/frontend/finger/spi` 每次都给一对全新的(实测不回吐已有的), 我们启动时优先
    ///   复用本地存的那份; 一旦又拿到新的(本地那份丢了/被清了), 就得把新的落盘,
    ///   否则内存与磁盘会分叉 —— 下次启动又变成另一台设备。
    /// </summary>
    public void SetBuvid4(string buvid4)
    {
        if (string.IsNullOrEmpty(buvid4)) return;
        if (string.Equals(Current.Buvid4, buvid4, StringComparison.Ordinal)) return;
        Current.Buvid4 = buvid4;
        Save();
    }

    /// <summary>
    /// 退出登录: 清空本地会话。
    ///
    /// 它是**唯一**合法的"把登录态弄没"的入口, 所以走的是"删文件"而不是"Save 一份空的" ——
    /// Save() 的两道闸门(没 Load 过不写 / 不许用未登录覆盖已登录)专门挡那种误写,
    /// 而这里是用户明确的意图, 不该被挡。删完之后 `_loaded` 保持 true:
    /// 文件没了 = 真相就是"未登录", 之后补设备指纹等写入都是合法的。
    /// </summary>
    public void Clear()
    {
        Current = new Session();
        _loaded = true;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 忽略
        }

        // ★★ 退出登录时必须同时清掉 ApiClient 内存里的设备指纹(2026-10-12 16:33 实测):
        //   `EnsureBuvidAsync()` 第一句就是"内存里这一对齐全就直接返回"。
        //   于是"启动时领过一次 → 退出登录 → 重新扫码登录"这条路上,
        //   磁盘上的 buvid 已被 Clear() 删掉, 而 ApiClient 单例里的 `_buvid3/_buvid4` **还在**
        //   ⇒ EnsureBuvidAsync 直接短路, 既不重新领也不落盘
        //   ⇒ 用户重新登录后 session.json 里 buvid 是空的, 点赞照旧报风控。
        //   (用户原话: "退出登录, 在最新的 debug 上重新登录, 风控问题出现")
        Svc.Api.ResetBuvidCache();

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
        Add("buvid4", Current.Buvid4);
        // ★ 写操作(点赞/投币/收藏)的风控 Cookie。Add 内部已过滤空值,
        //   所以老配置/未登录时它们不在, 行为与加这几个字段之前完全一致。
        Add("b_nut", Current.BNut);
        Add("sid", Current.Sid);
        Add("buvid_fp", Current.BuvidFp);
        Add("b_lsid", Current.Blsid);
        return string.Join("; ", parts);
    }
}