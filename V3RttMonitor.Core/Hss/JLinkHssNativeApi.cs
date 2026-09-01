using System.Runtime.InteropServices;
using System.Text;

namespace V3RttMonitor.Core.Hss;

/// <summary>Dynamic bindings matching J-Link SDK V6.94 JLinkARMDLL.h.</summary>
internal sealed class JLinkHssNativeApi : IDisposable
{
    private const int SwdInterface = 1;
    private const int TimestampUsFlag = 1;
    private readonly IntPtr _library;
    private readonly OpenDelegate _open;
    private readonly CloseDelegate _close;
    private readonly ExecCommandDelegate _execCommand;
    private readonly TifSelectDelegate _tifSelect;
    private readonly SetSpeedDelegate _setSpeed;
    private readonly ConnectDelegate _connect;
    private readonly GoDelegate _go;
    private readonly HssGetCapsDelegate _hssGetCaps;
    private readonly HssStartDelegate _hssStart;
    private readonly HssStopDelegate _hssStop;
    private readonly HssReadDelegate _hssRead;
    private bool _opened;
    private bool _disposed;

    public JLinkHssNativeApi(string dllPath)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("HSS需要64位JustFloat Studio。");
        if (!File.Exists(dllPath)) throw new FileNotFoundException("找不到J-Link x64 DLL。", dllPath);
        _library = NativeLibrary.Load(dllPath);
        try
        {
            _open = Load<OpenDelegate>("JLINK_Open");
            _close = Load<CloseDelegate>("JLINK_Close");
            _execCommand = Load<ExecCommandDelegate>("JLINK_ExecCommand");
            _tifSelect = Load<TifSelectDelegate>("JLINK_TIF_Select");
            _setSpeed = Load<SetSpeedDelegate>("JLINK_SetSpeed");
            _connect = Load<ConnectDelegate>("JLINK_Connect");
            _go = Load<GoDelegate>("JLINK_Go");
            _hssGetCaps = Load<HssGetCapsDelegate>("JLINK_HSS_GetCaps");
            _hssStart = Load<HssStartDelegate>("JLINK_HSS_Start");
            _hssStop = Load<HssStopDelegate>("JLINK_HSS_Stop");
            _hssRead = Load<HssReadDelegate>("JLINK_HSS_Read");
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public static IReadOnlyList<string> RequiredExports { get; } =
    [
        "JLINK_Open", "JLINK_Close", "JLINK_ExecCommand", "JLINK_TIF_Select",
        "JLINK_SetSpeed", "JLINK_Connect", "JLINK_Go", "JLINK_HSS_GetCaps",
        "JLINK_HSS_Start", "JLINK_HSS_Stop", "JLINK_HSS_Read",
    ];

    public static IReadOnlyList<string> ValidateExports(string dllPath)
    {
        if (!File.Exists(dllPath)) return [$"DLL不存在：{dllPath}"];
        var errors = new List<string>();
        var handle = NativeLibrary.Load(dllPath);
        try
        {
            foreach (var name in RequiredExports)
            {
                if (!NativeLibrary.TryGetExport(handle, name, out _)) errors.Add($"缺少导出：{name}");
            }
        }
        finally { NativeLibrary.Free(handle); }
        return errors;
    }

    public void OpenAndConnect(string device, int speedKhz)
    {
        ThrowIfDisposed();
        // Supported by the SDK before Open; prevents DLL-owned modal dialogs.
        TryExec("SuppressGUI = 1");
        var errorPointer = _open();
        var error = errorPointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(errorPointer) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException($"J-Link打开失败：{error}");
        _opened = true;

        Exec($"Device = {device}");
        var tifResult = _tifSelect(SwdInterface);
        if (tifResult < 0) throw new InvalidOperationException($"选择SWD失败：{tifResult}");
        _setSpeed(checked((uint)speedKhz));
        var connectResult = _connect();
        if (connectResult < 0) throw new InvalidOperationException($"连接目标失败：{connectResult}");
    }

    public HssCapabilities GetCapabilities()
    {
        var native = new NativeCaps();
        var result = _hssGetCaps(ref native);
        if (result < 0) throw new InvalidOperationException($"读取HSS能力失败：{result}");
        return new HssCapabilities(native.MaxBlocks, native.MaxFrequency, native.Capabilities);
    }

    public void Start(IReadOnlyList<HssVariableSelection> variables, int periodUs)
    {
        var blocks = variables.Select(item => new NativeBlock
        {
            Address = item.Address,
            NumBytes = checked((uint)item.ByteCount),
        }).ToArray();
        var handle = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        try
        {
            var result = _hssStart(handle.AddrOfPinnedObject(), blocks.Length, periodUs, TimestampUsFlag);
            if (result < 0) throw new InvalidOperationException($"启动HSS失败：{DescribeStartError(result)} ({result})");
        }
        finally { handle.Free(); }
        _go();
    }

    public int Read(byte[] buffer)
    {
        var result = _hssRead(buffer, checked((uint)buffer.Length));
        if (result < 0) throw new InvalidOperationException($"读取HSS失败：{result}");
        return result;
    }

    public void Stop()
    {
        if (!_opened) return;
        try { _hssStop(); } catch { }
    }

    private void Exec(string command)
    {
        var output = new StringBuilder(512);
        var result = _execCommand(command, output, output.Capacity);
        if (result < 0) throw new InvalidOperationException($"J-Link命令失败：{command}；{output}");
    }

    private void TryExec(string command)
    {
        try { _execCommand(command, new StringBuilder(64), 64); } catch { }
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static string DescribeStartError(int value) => value switch
    {
        -1 => "未指定错误",
        -2 => "探针端HSS缓冲区分配失败",
        -3 => "变量/内存块数量超过探针能力",
        -4 => "目标硬件不支持HSS后台访问",
        _ => "J-Link全局错误",
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_opened)
        {
            Stop();
            try { _close(); } catch { }
            _opened = false;
        }
        NativeLibrary.Free(_library);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlock
    {
        public uint Address;
        public uint NumBytes;
        public uint Flags;
        public uint Dummy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCaps
    {
        public uint MaxBlocks;
        public uint MaxFrequency;
        public uint Capabilities;
        public uint Dummy0;
        public uint Dummy1;
        public uint Dummy2;
        public uint Dummy3;
        public uint Dummy4;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr OpenDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CloseDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] private delegate int ExecCommandDelegate(string input, StringBuilder output, int size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int TifSelectDelegate(int interfaceIndex);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void SetSpeedDelegate(uint speedKhz);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ConnectDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GoDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int HssGetCapsDelegate(ref NativeCaps caps);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int HssStartDelegate(IntPtr blocks, int count, int periodUs, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int HssStopDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int HssReadDelegate([Out] byte[] buffer, uint bufferSize);
}
