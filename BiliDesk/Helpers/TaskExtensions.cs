using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliDesk.Helpers;

/// <summary>
/// fire-and-forget 的统一入口: 把"后台跑一个任务、异常记日志"这件事收敛到一处。
///
/// 为什么需要它: 仓库里散着 47 处 `async void` 和十几处 `_ = SomeAsync()`。
/// `async void` 的异常逃不到调用方, 只能靠全局处理器(记日志 + **弹框**);
/// fire-and-forget 的 Task 若无人观察, 异常会在 Finalizer 里变成未观察异常, 同样进全局处理器。
/// 两者的问题都是"弹框"—— 后台预热/轮询失败弹框是骚扰, 用户什么都没点。
/// 这里统一改成"记日志、不弹框", 让全局弹框只留给用户主动操作产生的错误。
///
/// 用法:
///   · `_ = XAsync()` 改成 `XAsync().Forget();`          —— 语义完全等价, 多了异常记录;
///   · 事件处理器里的 `async void` 优先改成普通方法 + `SomeAsync().Forget()`;
///     改不动(需要 sender/e)的, 方法体首尾套 try/catch { App.ReportError } 也可。
/// </summary>
public static class TaskExtensions
{
    /// <summary>观察并吞掉 Task 的异常(记录到 errors.txt)。任务本身照常跑完。</summary>
    public static void Forget(this Task task)
    {
        if (task == null) return;
        // ContinueWith 而不是 await: 不捕捉调用方上下文, 纯粹作为完成回调挂在任务上
        task.ContinueWith(
            t =>
            {
                // 只关心 Faulted; Canceled 是调用方主动取消(窗口关闭等), 不是错误
                if (t.IsFaulted && t.Exception is { } agg)
                {
                    // AggregateException 展开成真正的异常再记, 日志里少一层包裹
                    var inner = agg.InnerExceptions.Count == 1 ? agg.InnerExceptions[0] : agg;
                    App.ReportError(inner);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
