using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BiliDesk.Mpv;

/// <summary>
/// libmpv 的 C API 薄封装(2026-11-22, feat/mpv-core)。
///
/// 只包这一层能用到的: 生命周期 / 选项 / 属性 / 命令 / 事件循环。
/// 不做任何业务判断 —— 业务在 <see cref="MpvPlayer"/> 与调用方。
///
/// ★ 原生库位置: &lt;app&gt;/libmpv/win-x64/libmpv-2.dll, 与 libvlc 的布局保持同构。
///   用 DllImportResolver 按绝对路径加载, 不依赖 PATH / 当前目录。
/// </summary>
internal static class MpvNative
{
    private const string LibraryName = "libmpv-2";

    // ---- event ids (client.h) ----
    public const int EventNone = 0;
    public const int EventShutdown = 1;
    public const int EventLogMessage = 2;
    public const int EventGetPropertyReply = 3;
    public const int EventSetPropertyReply = 4;
    public const int EventCommandReply = 5;
    public const int EventStartFile = 6;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;
    public const int EventVideoReconfig = 17;
    public const int EventAudioReconfig = 18;
    public const int EventSeek = 20;
    public const int EventPlaybackRestart = 21;
    public const int EventPropertyChange = 22;
    public const int EventQueueOverflow = 24;

    // ---- format (client.h) ----
    public const int FormatNone = 0;
    public const int FormatString = 1;
    public const int FormatFlag = 3;
    public const int FormatInt64 = 4;
    public const int FormatDouble = 5;
    public const int FormatNode = 6;

    // ---- end file reasons ----
    public const int EndFileEof = 0;
    public const int EndFileStop = 2;
    public const int EndFileQuit = 3;
    public const int EndFileError = 4;
    public const int EndFileRedirect = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    private static bool _resolverInstalled;
    private static readonly bool _libPresent;

    static MpvNative()
    {
        try
        {
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);
                _resolverInstalled = true;
            }
        }
        catch
        {
            // 已经有别的 resolver: 不影响下面 LibraryPath 的判断
        }

        _libPresent = File.Exists(LibraryPath);
    }

    /// <summary>libmpv-2.dll 的预期位置。</summary>
    public static string LibraryPath =>
        Path.Combine(AppContext.BaseDirectory, "libmpv", "win-x64", "libmpv-2.dll");

    /// <summary>原生库是否就位(不保证能初始化, 只保证文件存在)。</summary>
    public static bool IsLibraryPresent => _libPresent;

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.StartsWith(LibraryName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        var path = LibraryPath;
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int mpv_get_property_double(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out double data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int mpv_get_property_long(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out long data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    public static extern int mpv_set_property_double(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref double data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    public static extern int mpv_set_property_long(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref long data);

    // FLAG 在 C 里是 int(4 字节), 不是 int64 —— 用 long 读写会踩到高位垃圾
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int mpv_get_property_int(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out int data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    public static extern int mpv_set_property_int(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, ref int data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr args);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    /// <summary>★ 返回的是指向 mpv 内部事件结构的**指针**, 只在下一次 wait_event 前有效。</summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_request_log_messages(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);
}
