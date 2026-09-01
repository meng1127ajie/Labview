using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace V3RttMonitor.CanAdapters;

internal static class NativeCanRuntimeResolver
{
    private const ushort PeMachineAmd64 = 0x8664;
    private static readonly ConcurrentDictionary<string, nint> LoadedLibraries = new(StringComparer.OrdinalIgnoreCase);

    public static string Prepare(string endpoint)
    {
        if (!endpoint.StartsWith("zlg://", StringComparison.OrdinalIgnoreCase))
            return "由已安装的适配器运行库解析";

        const string libraryName = "zlgcan.dll";
        if (LoadedLibraries.ContainsKey(libraryName)) return $"{libraryName} 已加载";
        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "Drivers", "CAN", "ZLG", "x64", "runtime");
        var fullPath = Path.Combine(runtimeDirectory, libraryName);
        if (!File.Exists(fullPath))
            throw new DllNotFoundException($"发布包缺少官方ZLGCAN运行库：{fullPath}");
        var machine = ReadPeMachine(fullPath);
        if (machine != PeMachineAmd64)
            throw new BadImageFormatException($"ZLGCAN运行库不是x64版本（PE Machine=0x{machine:X4}）：{fullPath}");

        PrependPath(runtimeDirectory);
        PrependPath(Path.Combine(runtimeDirectory, "kerneldlls"));
        if (!NativeLibrary.TryLoad(fullPath, out var handle))
            throw new DllNotFoundException($"无法加载官方x64 ZLGCAN运行库或其依赖：{fullPath}");
        LoadedLibraries.TryAdd(libraryName, handle);
        return fullPath;
    }

    private static ushort ReadPeMachine(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        stream.Position = peOffset + 4;
        return reader.ReadUInt16();
    }

    private static void PrependPath(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Contains(directory, StringComparer.OrdinalIgnoreCase)) return;
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
    }
}

public sealed record CanDriverRuntimeStatus(bool IsReady, string Architecture, string Path, string Message);

public static class CanDriverRuntimeProbe
{
    public static CanDriverRuntimeStatus ValidateZlg()
    {
        try
        {
            var path = NativeCanRuntimeResolver.Prepare("zlg://runtime-probe");
            return new CanDriverRuntimeStatus(true, "x64", path, "官方ZLGCAN运行库已加载");
        }
        catch (Exception exception)
        {
            return new CanDriverRuntimeStatus(false, "x64", string.Empty, exception.Message);
        }
    }
}
