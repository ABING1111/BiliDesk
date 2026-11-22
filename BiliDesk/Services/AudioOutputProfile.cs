using System;
using System.Runtime.InteropServices;

namespace BiliDesk.Services;

/// <summary>
/// 当前**默认播放端点**的一次性快照(MMDevice): 名称 + 枚举器, 用来判断"是不是蓝牙"。
///
/// ★ 为什么需要它: 1.8.3 强制 speex 后, 蓝牙耳机"一卡一卡"而直连音响正常。
///   VLC 的 aout 重采样器**不是只在需要变比时才建**, 而且这个对象同时是它的时钟漂移
///   补偿器 —— 蓝牙是独立时钟、端点延迟大且抖, 只有它会长期开着漂移补偿, speex 于是
///   每块重算一遍滤波器。所以"要不要强制 speex"必须按端点类型决定, 不能只看采样率
///   (蓝牙 48k 端点照样会走 speex + 漂移补偿)。完整推导见 VlcCore.BuildCoreArguments。
///
/// ★ 探测失败一律当"非蓝牙"(= 维持 speex), 绝不因为拿不到 COM/属性就改变播放行为。
/// ★ 结果缓存 2 秒: 起播路径会多次问, 但用户中途插耳机后下一次起播也要能立刻生效。
/// </summary>
public static class AudioOutputProfile
{
    private const int CacheMs = 2000;
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int StgmRead = 0;
    private const int VT_LPWSTR = 31;

    private static readonly object Gate = new();
    private static Snapshot _cached = Snapshot.Unknown;
    private static long _cachedAt = long.MinValue / 2;

    /// <summary>端点的判定结果。Bluetooth 为 null = 探测不到(按非蓝牙处理)。</summary>
    public readonly record struct Snapshot(bool? IsBluetooth, string Name, string Enumerator)
    {
        public static readonly Snapshot Unknown = new(null, "", "");

        public string Kind => IsBluetooth switch
        {
            true => "蓝牙",
            false => "非蓝牙",
            null => "未知",
        };
    }

    /// <summary>默认播放端点是不是蓝牙。探测不到时返回 false(保持现状)。</summary>
    public static bool IsDefaultBluetooth() => Current().IsBluetooth == true;

    /// <summary>取当前默认播放端点的快照(带 2 秒缓存; 任何异常都不外抛)。</summary>
    public static Snapshot Current()
    {
        lock (Gate)
        {
            if (Environment.TickCount64 - _cachedAt < CacheMs) return _cached;
            _cached = Probe();
            _cachedAt = Environment.TickCount64;
            return _cached;
        }
    }

    /// <summary>诊断日志用的一行: 选了哪个重采样模块、当前端点是什么、为什么。</summary>
    public static string Describe(string resampler)
    {
        var s = Current();
        var name = string.IsNullOrWhiteSpace(s.Name) ? "默认设备" : s.Name;
        var enc = string.IsNullOrWhiteSpace(s.Enumerator) ? "枚举器未知" : s.Enumerator;
        return $"resampler={resampler} endpoint=\"{name}\" ({s.Kind}, {enc})";
    }

    /// <summary>
    /// 端点分类(**纯函数**, 便于单测)。
    /// EnumeratorName 是权威判据(BTHENUM\... = 蓝牙); 拿不到时退回设备名关键字。
    /// 两者都拿不到 ⇒ null(未知)。
    /// </summary>
    public static bool? ClassifyBluetooth(string? enumeratorName, string? friendlyName)
    {
        var enc = (enumeratorName ?? "").Trim();
        if (enc.Length > 0)
        {
            // Windows 的蓝牙音频端点枚举器是 BTHENUM\{...}(免提还有 BTHHFENUM)。
            return enc.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
                || enc.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase);
        }

        var name = (friendlyName ?? "").Trim();
        if (name.Length == 0) return null;   // 什么都拿不到: 未知

        return name.Contains("蓝牙", StringComparison.Ordinal)
            || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || name.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || name.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Handsfree", StringComparison.OrdinalIgnoreCase)
            || name.Contains("无线立体声", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ MMDevice 探测

    private static Snapshot Probe()
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        try
        {
            if (!EnsureCom()) return Snapshot.Unknown;

            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0
                || device == null)
                return Snapshot.Unknown;

            if (device.OpenPropertyStore(StgmRead, out store) != 0 || store == null)
                return Snapshot.Unknown;

            var name = ReadString(store, PkeyFriendlyName);
            var enc = ReadString(store, PkeyEnumeratorName);
            return new Snapshot(ClassifyBluetooth(enc, name), name, enc);
        }
        catch
        {
            // COM 未注册 / 没有音频设备 / 属性读不到: 一律"未知", 不改变播放行为
            return Snapshot.Unknown;
        }
        finally
        {
            Release(store);
            Release(device);
            Release(enumeratorObject);
        }
    }

    [ThreadStatic] private static bool _comReady;

    /// <summary>
    /// 每个线程只需成功一次。RPC_E_CHANGED_MODE(已在别的 apartment 初始化过)也算可用 ——
    /// MMDevice 只要求"这个线程初始化过 COM", 不挑 STA/MTA。
    /// </summary>
    private static bool EnsureCom()
    {
        if (_comReady) return true;
        var hr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        // S_OK=0 / S_FALSE=1 / RPC_E_CHANGED_MODE=0x80010106
        _comReady = hr == 0 || hr == 1 || hr == unchecked((int)0x80010106);
        return _comReady;
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        var k = key;
        if (store.GetValue(ref k, out var value) != 0) return "";
        try
        {
            return value.VarType == VT_LPWSTR
                ? Marshal.PtrToStringUni(value.Pointer) ?? ""
                : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject == null || !Marshal.IsComObject(comObject)) return;
        try { Marshal.ReleaseComObject(comObject); } catch { /* 退出/已释放: 忽略 */ }
    }

    // 属性键: 都是 A45C254E-DF1C-4EFD-8020-67D146A850E0 这个 format id
    private static readonly Guid DevicePropertyFormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private static readonly PropertyKey PkeyFriendlyName = new() { FormatId = DevicePropertyFormatId, PropertyId = 14 };
    private static readonly PropertyKey PkeyEnumeratorName = new() { FormatId = DevicePropertyFormatId, PropertyId = 24 };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    /// <summary>
    /// PROPVARIANT 只需要认 VT_LPWSTR(名称) 与"其余一律清掉"。
    /// ★ 尾部 Padding 不是凑数: 原生 PROPVARIANT 的 union 在 x64 是 16 字节,
    ///   结构体少一格会让 out 参数写越界。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore? store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
