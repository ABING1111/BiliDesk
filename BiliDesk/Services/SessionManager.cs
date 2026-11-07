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

    /// <summary>
    /// ★★★ 全部可变状态(会话字段 + _loaded/_diskUnreadable)的唯一闸门。
    ///
    /// 为什么必须有: 本类被**两条线程**同时使用 ——
    ///   · UI 线程: UpdateFromCookies(登录成功) / Clear(退出登录) / BuildCookieHeader(每次请求);
    ///   · 线程池: App 启动预热(Task.Run → WarmUpWriteAsync → EnsureBuvidAsync → SetBuvid3/4 → Save)。
    /// 没锁时两个 Save 会固定写同一个 session.json.tmp 互相踩, BuildCookieHeader 也可能
    /// 读到"改了一半"的会话。Monitor 是可重入的, 类内方法互相调用不会自锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>真正的可变会话。对外一律经 <see cref="Current"/> 发快照, 外部拿不到可变引用。</summary>
    private Session _current = new();

    /// <summary>
    /// 当前会话的**快照**(克隆)。
    ///
    /// ★ 2026-11-07 前这里是直接暴露可变引用: 外部(ApiClient/PassportService/WebView2Service/App)
    ///   十几处只读字段, 但拿到的是"活的"对象 —— 线程池上的 EnsureBuvidAsync 正在补 buvid 时,
    ///   读取方可能看到改了一半的状态。改为每次返回克隆: 外部照旧逐字段读, 语义不变,
    ///   只是读到的一定是某一瞬间的完整值。Session 是十来个 string 的小对象, 克隆成本可忽略。
    /// </summary>
    public Session Current { get { lock (_gate) return Clone(_current); } }

    /// <summary>
    /// 磁盘上的那一份是否已经读进内存了。
    ///
    /// ★ 这是防"把登录态写没"的关键闸门: 会话的初值是全 null 的 `new Session()`,
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

    public bool HasLogin { get { lock (_gate) return !string.IsNullOrEmpty(_current.SessData); } }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    _current = JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath), JsonOpts) ?? new Session();
                    _loaded = true;
                    _diskUnreadable = false;
                }
                else
                {
                    // 文件不存在 = 确实还没登录过, 这份空会话就是"真相", 允许往后落盘
                    _current = new Session();
                    _loaded = true;
                    _diskUnreadable = false;
                }
            }
            catch
            {
                // 解析失败: **不**置 _loaded(别立刻拿空会话去覆盖), 但记下"这份读不懂"。
                // 磁盘文件先留着 —— 等真的有一次要落盘的写入时, 由 ParkUnreadable() 挪走它。
                _current = new Session();
                _diskUnreadable = true;
            }
        }
    }

    /// <summary>
    /// 把读不懂的那份 session.json 挪到一边(改名保留), 让后续写入能正常进行。
    ///
    /// 为什么是"改名"而不是"删掉": 那份文件可能是用户**唯一**的登录凭据,
    /// 只是恰好坏了 —— 留着它, 用户/作者还有机会从里面把 SESSDATA 捡回来。
    /// 改名之后磁盘上就没有可被覆盖的"好数据"了, 闸门一/二也不该再拦(见 Save)。
    /// ★ 调用方必须已持有 <see cref="_gate"/>。
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
        // ★ 全程持锁(包括文件 IO): 这里的不变量是"闸门判定 → 挪坏文件 → 落盘"必须原子,
        //   拆开的话并发 Save/Clear 会把闸门判定与实际写入之间的状态搞拧。
        //   会话文件只有几百字节、写入频率极低(登录/退出/补指纹), 锁内 IO 的代价可以忽略。
        lock (_gate)
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
                if (string.IsNullOrEmpty(_current.SessData) && OnDiskHasLogin())
                {
                    App.ReportError(new InvalidOperationException(
                        "SessionManager.Save() 试图用一份未登录的会话覆盖磁盘上已登录的会话, 已拒绝"));
                    return;
                }

                // ★ 序列化一份快照再写: 锁内拿到的是一致状态(本方法全程持锁, 这里直接序列化 _current 亦可,
                //   但克隆语义与对外 Current 保持一致, 万一日后有人把文件 IO 挪出锁外也不会踩空)。
                var json = JsonSerializer.Serialize(Clone(_current), JsonOpts);

                // 先写临时文件再原子替换: 直接覆写的话, 写入瞬间崩溃/断电会留下半个
                // session.json —— JSON 解析失败 = 登录态丢失, 用户要重新扫码。
                // ★ 临时文件名带唯一后缀: 旧版固定写 ".tmp", 两个线程同时 Save 会互相覆盖
                //   (File.Move 的 overwrite 只是兜住最后一写, 中间可能留下被写了一半的 .tmp)。
                var tmp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);
                }
                finally
                {
                    // Move 失败时把临时文件扫掉, 免得数据目录里攒一堆 .tmp-xxx
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 尽力而为 */ }
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>磁盘上那份 session.json 里有没有登录态(读不动/不存在都算没有)。调用方须持锁。</summary>
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

        lock (_gate)
        {
            _current.SessData = Get("SESSDATA") ?? _current.SessData;
            _current.BiliJct = Get("bili_jct") ?? _current.BiliJct;
            _current.DedeUserID = Get("DedeUserID") ?? _current.DedeUserID;
            _current.DedeUserIDCkMd5 = Get("DedeUserID__ckMd5") ?? _current.DedeUserIDCkMd5;
            _current.Buvid3 = Get("buvid3") ?? _current.Buvid3;
            _current.Buvid4 = Get("buvid4") ?? _current.Buvid4;
            // ★★★ 写操作风控 Cookie(2026-11-12): 登录时浏览器里明明有, 这里以前直接丢了,
            //   于是"换了扫码登录之后点赞报风控"。见 Models.Session 里那一段的说明。
            //   用 ?? 保留旧值: 新一轮登录没带这几个键时不该把已有值清掉。
            _current.BNut = Get("b_nut") ?? _current.BNut;
            _current.Sid = Get("sid") ?? _current.Sid;
            _current.BuvidFp = Get("buvid_fp") ?? _current.BuvidFp;
            _current.Blsid = Get("b_lsid") ?? _current.Blsid;
            Save();
        }
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
    public bool HasRiskCookies { get { lock (_gate) return !string.IsNullOrEmpty(_current.BNut); } }

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
        string? Get(string name) => cookies.TryGetValue(name, out var v) ? v : null;

        lock (_gate)
        {
            // ★ 原守卫 `if (!HasLogin || HasRiskCookies) return;` 的等价形式(在锁内读同一份状态):
            //   未登录不补; b_nut 已在就不重复补。
            if (string.IsNullOrEmpty(_current.SessData)) return;   // 没登录, 不补
            if (HasRiskCookies) return;                            // 已经齐了

            var bNut = Get("b_nut");
            var sid = Get("sid");

            // ★★ b_nut 是"登录时刻的 Unix 时间戳", **不是随机值** —— 服务端拿它算
            //   "这个会话存在多久了"。所以 Profile 里没有时也能自己造一个:
            //   本次启动时刻就是合法的登录时刻(老用户的 session.json 缺这个字段,
            //   本质上就是"上一版登录流程没记", 而不是"用户没有登录时刻")。
            //   (2026-10-12: 实测用户 session.json 里 b_nut 为空、sid 有值,
            //     而 b_nut 恰好是写接口风控最常被点名的那一项。)
            if (string.IsNullOrEmpty(bNut) && string.IsNullOrEmpty(_current.BNut))
                bNut = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            if (string.IsNullOrEmpty(bNut) && string.IsNullOrEmpty(sid)) return;  // Profile 里也没有, 放弃

            _current.BNut = bNut ?? _current.BNut;
            _current.Sid = sid ?? _current.Sid;
            _current.BuvidFp = Get("buvid_fp") ?? _current.BuvidFp;
            _current.Blsid = Get("b_lsid") ?? _current.Blsid;
            Save();
        }
        Changed?.Invoke();
    }

    public void SetBuvid3(string buvid3)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_current.Buvid3)) return;
            _current.Buvid3 = buvid3;
            Save();
        }
    }

    /// <summary>
    /// 强制覆盖设备指纹的第一半(buvid3)。**只有风控轮换指纹时该用**(见 ApiClient 的搜索降级链)
    /// —— 常规路径必须走 <see cref="SetBuvid3"/>, 那里有"已有就不覆盖"的保护, 保证设备身份稳定。
    /// </summary>
    public void ForceSetBuvid3(string buvid3)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(buvid3)) return;
            _current.Buvid3 = buvid3;
            Save();
        }
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
        lock (_gate)
        {
            if (string.IsNullOrEmpty(buvid4)) return;
            if (string.Equals(_current.Buvid4, buvid4, StringComparison.Ordinal)) return;
            _current.Buvid4 = buvid4;
            Save();
        }
    }

    /// <summary>强制覆盖 buvid4, 与 <see cref="ForceSetBuvid3"/> 配套(风控轮换指纹用)。</summary>
    public void ForceSetBuvid4(string buvid4)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(buvid4)) return;
            _current.Buvid4 = buvid4;
            Save();
        }
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
        // ★★ 2026-10-17 修「退出登录后无法播放, 提示 B站限流」:
        //
        //   `Current = new Session()` 会把 buvid3/buvid4 **一起清空**。于是登出之后这台设备
        //   在 B 站眼里变成"一台**从没见过的新设备**": 第一个请求还没拿到指纹就发了出去,
        //   紧接着又要现领指纹再重发 —— 一台"零历史的新设备"连着打同一个接口,
        //   正是最典型的风控特征, 于是稳定吃到 -412。
        //   (重新登录后正常, 因为 SESSDATA 本身就是被认可的凭据, 不需要靠指纹"自证"。)
        //
        //   ⇒ **登录凭据要清, 设备身份要留**。buvid 是"这台机器的匿名编号",
        //     它与"我是谁(SESSDATA)"无关; 把它连同登录态一起丢掉, 等于每次登出
        //     都给自己换一台新设备。保留它既不会泄露身份(匿名编号而已),
        //     也让登出前后的请求特征保持连续。
        lock (_gate)
        {
            var keepBuvid3 = _current.Buvid3;
            var keepBuvid4 = _current.Buvid4;
            var keepBuvidFp = _current.BuvidFp;

            _current = new Session
            {
                Buvid3 = keepBuvid3,
                Buvid4 = keepBuvid4,
                BuvidFp = keepBuvidFp,
            };
            _loaded = true;
            // ★★ 删文件 → 再把"保留下来的设备指纹"写回去(2026-10-17 实测发现漏了这步):
            //   Clear() 的原设计是"删掉整个 session.json = 未登录"。但现在我们**刻意保留**了
            //   buvid3/buvid4, 而文件已经删了 —— 若不落盘, 内存里有、磁盘上无:
            //     · 下次重启 Load() 读不到 buvid ⇒ 变成一台"新设备"重新领一对
            //       (与登出前连续的身份被打断, 重新登录后还要再换一次);
            //     · 更糟的是登出后若进程被杀, 这次保留就白做了。
            //
            // ★★ 为什么不复用 Save(): 它写的是 `Current`(字段全、含 bili_jct 等),
            //   而这里只想要"一个只含 buvid 的干净会话"; 更关键的是 `Save()` 的闸门二
            //   依赖 `OnDiskHasLogin()` 判断"要不要拒绝覆盖", 在文件刚被删、又需要
            //   写一份"未登录"回去的语境下, 语义是拧着的。⇒ 直接写, 一次到位。
            try
            {
                var keep = new Session
                {
                    Buvid3 = keepBuvid3,
                    Buvid4 = keepBuvid4,
                    BuvidFp = keepBuvidFp,
                };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(keep, JsonOpts));
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
            //
            // ★★ 2026-10-17: 这条**只适用于磁盘上指纹也已失效**的情况。
            //   上面 Clear() 已经改成**保留** buvid3/buvid4 了(登出不该让设备变成"新设备"),
            //   所以内存里这对恰恰是要**留着继续用**的 —— 这里再无条件清一次, 等于把刚保留的
            //   又扔了: 内存空、磁盘有 ⇒ EnsureBuvidAsync 会从磁盘把同一对补回来, 看似无碍;
            //   但在补齐之前的那一个请求仍然是**无指纹**的, 而"零历史的新设备"正是风控最爱拦的。
            //   ⇒ 只有磁盘上确实已经没有指纹时才需要清内存(让下一次重新领)。
            if (string.IsNullOrEmpty(_current.Buvid3) || string.IsNullOrEmpty(_current.Buvid4))
                Svc.Api.ResetBuvidCache();
        }

        Changed?.Invoke();
    }

    /// <summary>构造请求用的 Cookie 头(锁内读一致快照)</summary>
    public string BuildCookieHeader()
    {
        lock (_gate)
        {
            var s = _current;
            var parts = new List<string>();
            void Add(string? name, string? value)
            {
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                    parts.Add($"{name}={value}");
            }
            Add("SESSDATA", s.SessData);
            Add("bili_jct", s.BiliJct);
            Add("DedeUserID", s.DedeUserID);
            Add("DedeUserID__ckMd5", s.DedeUserIDCkMd5);
            Add("buvid3", s.Buvid3);
            Add("buvid4", s.Buvid4);
            // ★ 写操作(点赞/投币/收藏)的风控 Cookie。Add 内部已过滤空值,
            //   所以老配置/未登录时它们不在, 行为与加这几个字段之前完全一致。
            Add("b_nut", s.BNut);
            Add("sid", s.Sid);
            Add("buvid_fp", s.BuvidFp);
            Add("b_lsid", s.Blsid);
            return string.Join("; ", parts);
        }
    }

    /// <summary>克隆一份会话(Session 是纯 POCO, 逐字段拷贝即可)。调用方须持锁。</summary>
    private static Session Clone(Session s) => new()
    {
        SessData = s.SessData,
        BiliJct = s.BiliJct,
        DedeUserID = s.DedeUserID,
        DedeUserIDCkMd5 = s.DedeUserIDCkMd5,
        Buvid3 = s.Buvid3,
        Buvid4 = s.Buvid4,
        BNut = s.BNut,
        Sid = s.Sid,
        BuvidFp = s.BuvidFp,
        Blsid = s.Blsid,
    };
}
