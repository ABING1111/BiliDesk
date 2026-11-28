using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Services;

/// <summary>
/// 诊断用的**全局状态快照 + 最近事件流水**(2026-11-28 新增)。
///
/// ★ 为什么需要它:
///   用户报"窗口冻住 → 进程没了"时, 事后能拿到的证据极少 —— errors.txt 里可能一个字
///   都没有(原生崩溃不是 .NET 异常), 事件查看器只有一句"应用程序错误"。
///   唯一能在**当场**自动留下现场的办法, 就是在别的线程上把"此刻程序在干什么"记下来:
///   页面停在哪儿、播放器在放什么、刚刚发生过什么。
///
/// ★ 为什么状态与流水分开:
///   · <see cref="Set"/>/<see cref="Get"/> 是**当前状态**(键值对, 覆盖写)——
///     "现在停在第 3 页""正在放 BVxxx, qn=80"。
///   · <see cref="Note"/> 是**事件流水**(环形缓冲, 只追加)——
///     "切到动态页""播放出错""恢复完成"。冻住的那一刻最能说明"它刚才干了什么"。
///
/// ★ 线程安全: 写它的可能是 UI 线程(状态更新)也可能是看门狗线程(读取快照),
///   所以全部走锁。快照返回的是**副本**, 调用方拿着慢慢读也不会被并发写坏。
/// </summary>
public static class DiagnosticsState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> State = new(StringComparer.Ordinal);

    /// <summary>事件流水的容量(只留最近这么多条)。再多对定位没帮助, 白占内存。</summary>
    public const int JournalCapacity = 300;

    private static readonly Queue<string> Journal = new();

    /// <summary>设置一个状态项(覆盖写)。值传 null 等于删掉这一项。</summary>
    public static void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (Gate)
        {
            if (value == null) State.Remove(key);
            else State[key] = value;
        }
    }

    /// <summary>
    /// 标记"UI 线程现在正在做某件可能很久的事"。
    ///
    /// ★ 为什么需要它:
    ///   UI 线程被冻住时, 别的线程**拿不到它卡在哪一行**(.NET 没有跨线程抓栈的公开手段)。
    ///   但我们可以反过来: 在那些"已知可能很久"的活儿开始前打个标记、做完撤掉。
    ///   冻结发生时看一眼这个标记, 就知道它大概率是卡在这件事上 ——
    ///   比"未知"强得多, 也比"猜"靠谱得多。
    ///
    /// ★ 用法: <c>using (DiagnosticsState.BeginUiOperation("卡片墙排版")) { ... }</c>
    ///   ★ 标记是**栈式**的(后进先出): 嵌套调用时, 内层的标记先撤掉。
    ///   ★ 同一个名字重复进入不会互相拆掉 —— 用计数而不是布尔。
    /// </summary>
    public static IDisposable BeginUiOperation(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return NoOpScope.Instance;
        lock (Gate)
        {
            _uiOps.Add(name);
            WriteCurrentUiOperation();
        }
        return new UiOperationScope(name);
    }

    /// <summary>当前正在进行的 UI 操作(最内层那个)。</summary>
    public static string? CurrentUiOperation
    {
        get { lock (Gate) return _uiOps.Count == 0 ? null : _uiOps[^1]; }
    }

    /// <summary>所有正在进行的 UI 操作(从外到内)。</summary>
    public static IReadOnlyList<string> ActiveUiOperations
    {
        get { lock (Gate) return _uiOps.Count == 0 ? Array.Empty<string>() : _uiOps.ToArray(); }
    }

    /// <summary>
    /// 正在进行的 UI 操作, **按进入顺序**(末尾 = 最内层)。
    ///
    /// ★★★ 不用 Dictionary(2026-11-28 审查时改的):
    ///   Dictionary 的枚举顺序**不保证**插入顺序, 删除后重新插入还会复用旧槽位。
    ///   而这里输出的恰恰是"冻结时 UI 线程卡在**哪一层**", 最内层(最细粒度)才有价值 ——
    ///   顺序一乱, 报告里就可能把外层的大步骤当成"当时在做的事", 把人往错的方向带。
    ///   ⇒ 用一个 List 显式维持顺序: 进去时 Add, 出来时 RemoveAt。
    /// ★ 同一个名字可以重复出现(嵌套)-> 不去重, 直接按栈记录。
    /// </summary>
    private static readonly List<string> _uiOps = new();

    /// <summary>把当前 UI 操作写进状态快照(供导出报告时直接看到)。</summary>
    private static void WriteCurrentUiOperation()
    {
        if (_uiOps.Count == 0)
        {
            State.Remove("UI 线程当前操作");
            return;
        }
        State["UI 线程当前操作"] = _uiOps[^1];
    }

    /// <summary>一个"什么也不做"的 IDisposable(给空名字用)。</summary>
    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// UI 操作标记的作用域(Dispose 时撤掉标记)。
    /// ★ 按**名字**从后往前找第一处匹配, 而不是假定"最后一个就是自己":
    ///   嵌套同名时(外层 A、内层 A), 内层先 Dispose, 从后往前删才不会把外层那条也删掉。
    /// </summary>
    private sealed class UiOperationScope : IDisposable
    {
        private readonly string _name;
        private bool _disposed;

        public UiOperationScope(string name) => _name = name;

        public void Dispose()
        {
            if (_disposed) return;   // Dispose 可能被调两次
            _disposed = true;
            lock (Gate)
            {
                var index = _uiOps.LastIndexOf(_name);
                if (index >= 0) _uiOps.RemoveAt(index);
                WriteCurrentUiOperation();
            }
        }
    }

    /// <summary>读一个状态项; 没有这一项返回 null。</summary>
    public static string? Get(string key)
    {
        lock (Gate)
            return State.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// 追加一条事件流水。
    /// ★ 不要在这里写盘 —— 冻结现场下写盘可能正卡在出错的路径上;
    ///   流水是纯内存, 由看门狗/导出时统一取走。
    /// </summary>
    public static void Note(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Gate)
        {
            Journal.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Journal.Count > JournalCapacity) Journal.Dequeue();
        }
    }

    /// <summary>当前状态的副本(键值对)。</summary>
    public static IReadOnlyDictionary<string, string> SnapshotState()
    {
        lock (Gate)
            return new Dictionary<string, string>(State, StringComparer.Ordinal);
    }

    /// <summary>最近事件流水的副本(按时间顺序)。</summary>
    public static IReadOnlyList<string> SnapshotJournal()
    {
        lock (Gate)
            return Journal.ToArray();
    }

    /// <summary>
    /// 把状态与流水拼成一段人类可读的文本(导出诊断报告时用)。
    /// </summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("== 当前状态 ==");
        var state = SnapshotState();
        if (state.Count == 0) sb.AppendLine("(空)");
        else
            foreach (var kv in state.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{kv.Key} = {kv.Value}");

        sb.AppendLine();
        sb.AppendLine("== 最近事件 ==");
        var journal = SnapshotJournal();
        if (journal.Count == 0) sb.AppendLine("(空)");
        else foreach (var line in journal) sb.AppendLine(line);
        return sb.ToString();
    }
}
