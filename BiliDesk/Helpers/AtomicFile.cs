using System;
using System.IO;

namespace BiliDesk.Helpers;

/// <summary>
/// 原子文件写: 先写临时文件、再 <see cref="File.Move(string,string,bool)"/> 原子替换。
///
/// 为什么需要统一的一份: 直接覆写 JSON 文件的话, 写入瞬间崩溃/断电会留下半个文件 ——
/// 下次启动解析失败, 轻则设置回默认、重则登录态丢失。仓库里已有三处各自实现了这个
/// 惯用法(SettingsStore.WriteAtomic / SessionManager.Save / PlaybackProgressService),
/// 临时文件名还各不相同(固定 .tmp 会并发互踩), 收敛到一个入口。
///
/// ★ 临时名带唯一后缀: 两个线程同时写同一个目标(如 SettingsStore 的后台写与退出 Flush)
///   用同一个 .tmp 名会互相覆盖; 唯一后缀 + finally 清残留, 互踩与垃圾文件都不会有。
/// </summary>
public static class AtomicFile
{
    /// <summary>原子写入全部文本(UTF-8)。写入/替换失败会抛出, 由调用方决定是否吞。</summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // Move 失败时把临时文件扫掉, 免得数据目录里攒一堆 .tmp-xxx
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 尽力而为 */ }
        }
    }
}
