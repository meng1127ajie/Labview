using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace V3RttMonitor.Core.Hss;

public sealed record ElfSymbolReaderOptions
{
    /// <summary>Optional explicit path to arm-none-eabi-nm.exe.</summary>
    public string? NmToolPath { get; init; }

    /// <summary>Optional explicit path to arm-none-eabi-readelf.exe.</summary>
    public string? ReadElfToolPath { get; init; }
}

public sealed class ElfSymbolReaderException : Exception
{
    public ElfSymbolReaderException(string message)
        : base(message)
    {
    }

    public ElfSymbolReaderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads defined RAM objects from an ELF by combining GNU readelf section and
/// symbol metadata with GNU nm's object-kind letters. No J-Link DLL is needed.
/// </summary>
public sealed partial class ElfSymbolReader
{
    private const string DefaultToolDirectory = @"C:\ToolChain\bin";

    private readonly ElfSymbolReaderOptions _options;

    public ElfSymbolReader(ElfSymbolReaderOptions? options = null)
    {
        _options = options ?? new ElfSymbolReaderOptions();
    }

    public async Task<ElfSymbolCatalog> ReadAsync(
        string elfPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elfPath);
        cancellationToken.ThrowIfCancellationRequested();

        string fullElfPath = Path.GetFullPath(elfPath);
        if (!File.Exists(fullElfPath))
        {
            throw new FileNotFoundException("找不到所选 ELF 文件。", fullElfPath);
        }

        string nmPath = ResolveTool(_options.NmToolPath, "arm-none-eabi-nm.exe");
        string readElfPath = ResolveTool(_options.ReadElfToolPath, "arm-none-eabi-readelf.exe");

        Task<ToolResult> sectionsTask = RunToolAsync(
            readElfPath,
            ["--sections", "--wide", fullElfPath],
            cancellationToken);

        Task<ToolResult> symbolsTask = RunToolAsync(
            readElfPath,
            ["--symbols", "--wide", fullElfPath],
            cancellationToken);

        Task<ToolResult> segmentsTask = RunToolAsync(
            readElfPath,
            ["--segments", "--wide", fullElfPath],
            cancellationToken);

        Task<ToolResult> nmTask = RunToolAsync(
            nmPath,
            ["--defined-only", "--print-size", "--numeric-sort", fullElfPath],
            cancellationToken);

        await Task.WhenAll(sectionsTask, symbolsTask, segmentsTask, nmTask).ConfigureAwait(false);

        IReadOnlyList<ElfLoadSegment> ramSegments = ParseRamSegments(segmentsTask.Result.StandardOutput);
        IReadOnlyList<ElfRamSection> sections = ParseRamSections(
            sectionsTask.Result.StandardOutput,
            ramSegments);
        if (sections.Count == 0)
        {
            throw new ElfSymbolReaderException(
                "ELF 中没有找到映射到可写运行时 LOAD 段的 RAM section。" +
                "请确认文件是带符号表的可执行 ELF，而不是 BIN/HEX 或已 strip 文件。");
        }

        Dictionary<NmSymbolKey, char> nmTypes = ParseNmTypes(nmTask.Result.StandardOutput);
        IReadOnlyList<ElfSymbol> symbols = ParseSymbols(
            symbolsTask.Result.StandardOutput,
            sections,
            nmTypes);

        return new ElfSymbolCatalog
        {
            ElfPath = fullElfPath,
            Symbols = symbols,
            RamSections = sections,
            NmToolPath = nmPath,
            ReadElfToolPath = readElfPath,
        };
    }

    internal static IReadOnlyList<ElfLoadSegment> ParseRamSegments(string output)
    {
        var segments = new List<ElfLoadSegment>();

        foreach (string line in EnumerateLines(output))
        {
            Match match = LoadSegmentLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string flags = match.Groups["flags"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            ulong virtualAddress = ParseHex(match.Groups["virtual"].Value);
            ulong physicalAddress = ParseHex(match.Groups["physical"].Value);
            ulong memorySize = ParseHex(match.Groups["memorySize"].Value);

            // Writable, non-executable segments are normal RAM. A RAM-resident
            // code/data segment may be RWE, but its load address then differs
            // from its runtime virtual address. This excludes a Flash RWE
            // segment such as STM32 .text/.init_array.
            bool isRuntimeRam = flags.Contains('W') &&
                                (!flags.Contains('E') || virtualAddress != physicalAddress);
            if (isRuntimeRam && memorySize > 0)
            {
                segments.Add(new ElfLoadSegment(virtualAddress, memorySize, flags));
            }
        }

        return segments;
    }

    internal static IReadOnlyList<ElfRamSection> ParseRamSections(
        string output,
        IReadOnlyList<ElfLoadSegment> ramSegments)
    {
        var sections = new List<ElfRamSection>();

        foreach (string line in EnumerateLines(output))
        {
            Match match = SectionLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string flags = match.Groups["flags"].Value;
            if (!flags.Contains('W') || !flags.Contains('A'))
            {
                continue;
            }

            ulong size = ParseHex(match.Groups["size"].Value);
            ulong address = ParseHex(match.Groups["address"].Value);
            if (size == 0 || !ramSegments.Any(segment => RangeIsInside(address, size, segment.Address, segment.Size)))
            {
                continue;
            }

            sections.Add(new ElfRamSection(
                int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                address,
                size,
                flags));
        }

        return sections;
    }

    internal static Dictionary<NmSymbolKey, char> ParseNmTypes(string output)
    {
        var result = new Dictionary<NmSymbolKey, char>();

        foreach (string line in EnumerateLines(output))
        {
            Match match = NmLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = new NmSymbolKey(
                match.Groups["name"].Value,
                ParseHex(match.Groups["address"].Value),
                ParseHex(match.Groups["size"].Value));

            result.TryAdd(key, match.Groups["type"].Value[0]);
        }

        return result;
    }

    internal static IReadOnlyList<ElfSymbol> ParseSymbols(
        string output,
        IReadOnlyList<ElfRamSection> ramSections,
        IReadOnlyDictionary<NmSymbolKey, char> nmTypes)
    {
        Dictionary<int, ElfRamSection> sectionByIndex = ramSections.ToDictionary(section => section.Index);
        var symbols = new List<ElfSymbol>();
        var seen = new HashSet<NmSymbolKey>();

        foreach (string line in EnumerateLines(output))
        {
            Match match = SymbolLineRegex().Match(line);
            if (!match.Success || !match.Groups["type"].Value.Equals("OBJECT", StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(
                    match.Groups["section"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int sectionIndex) ||
                !sectionByIndex.TryGetValue(sectionIndex, out ElfRamSection? section))
            {
                continue;
            }

            ulong address = ParseHex(match.Groups["address"].Value);
            ulong size = ulong.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            string name = match.Groups["name"].Value.Trim();
            if (name.Length == 0 || size == 0 || !AddressIsInsideSection(address, size, section))
            {
                continue;
            }

            var key = new NmSymbolKey(name, address, size);
            if (!seen.Add(key))
            {
                continue;
            }

            nmTypes.TryGetValue(key, out char nmType);

            symbols.Add(new ElfSymbol
            {
                Name = name,
                Address = address,
                Size = size,
                SectionName = section.Name,
                SectionIndex = section.Index,
                Binding = ParseBinding(match.Groups["binding"].Value),
                Visibility = match.Groups["visibility"].Value,
                NmType = nmType == '\0' ? '?' : nmType,
                Kind = ParseKind(nmType),
            });
        }

        return symbols
            .OrderBy(symbol => symbol.Address)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool AddressIsInsideSection(ulong address, ulong size, ElfRamSection section)
        => RangeIsInside(address, size, section.Address, section.Size);

    private static bool RangeIsInside(ulong address, ulong size, ulong rangeAddress, ulong rangeSize)
    {
        if (address < rangeAddress || size > rangeSize)
        {
            return false;
        }

        ulong offset = address - rangeAddress;
        return offset <= rangeSize - size;
    }

    private static ElfSymbolBinding ParseBinding(string binding) => binding switch
    {
        "LOCAL" => ElfSymbolBinding.Local,
        "GLOBAL" => ElfSymbolBinding.Global,
        "WEAK" => ElfSymbolBinding.Weak,
        _ => ElfSymbolBinding.Unknown,
    };

    private static ElfSymbolKind ParseKind(char nmType) => nmType switch
    {
        'D' or 'd' => ElfSymbolKind.InitializedData,
        'B' or 'b' => ElfSymbolKind.UninitializedData,
        'G' or 'g' => ElfSymbolKind.SmallInitializedData,
        'S' or 's' => ElfSymbolKind.SmallUninitializedData,
        'C' or 'c' => ElfSymbolKind.Common,
        'V' or 'v' => ElfSymbolKind.WeakObject,
        _ => ElfSymbolKind.Unknown,
    };

    private static string ResolveTool(string? configuredPath, string fileName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        candidates.Add(Path.Combine(DefaultToolDirectory, fileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, fileName)));
        }

        string? resolved = candidates.FirstOrDefault(File.Exists);
        if (resolved is not null)
        {
            return Path.GetFullPath(resolved);
        }

        throw new ElfSymbolReaderException(
            $"找不到 {fileName}。请安装 Arm GNU Toolchain，或通过 {nameof(ElfSymbolReaderOptions)} 指定工具路径。" +
            $"默认检查位置：{Path.Combine(DefaultToolDirectory, fileName)}");
    }

    private static async Task<ToolResult> RunToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ElfSymbolReaderException($"无法启动 ELF 工具：{executablePath}");
            }
        }
        catch (ElfSymbolReaderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ElfSymbolReaderException($"启动 ELF 工具失败：{executablePath}", exception);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        string standardOutput = await stdoutTask.ConfigureAwait(false);
        string standardError = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(standardError)
                ? "工具未返回错误说明。"
                : standardError.Trim();

            throw new ElfSymbolReaderException(
                $"ELF 工具执行失败（退出码 {process.ExitCode}）：{Path.GetFileName(executablePath)}\n{detail}");
        }

        return new ToolResult(standardOutput, standardError);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between HasExited and Kill.
        }
    }

    private static IEnumerable<string> EnumerateLines(string value)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static ulong ParseHex(string value) =>
        ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    [GeneratedRegex(
        @"^\s*\[\s*(?<index>\d+)\]\s+(?<name>\S+)\s+\S+\s+(?<address>[0-9A-Fa-f]+)\s+[0-9A-Fa-f]+\s+(?<size>[0-9A-Fa-f]+)\s+[0-9A-Fa-f]+\s+(?<flags>[A-Za-z]+)\s+")]
    private static partial Regex SectionLineRegex();

    [GeneratedRegex(
        @"^\s*LOAD\s+0x[0-9A-Fa-f]+\s+0x(?<virtual>[0-9A-Fa-f]+)\s+0x(?<physical>[0-9A-Fa-f]+)\s+0x[0-9A-Fa-f]+\s+0x(?<memorySize>[0-9A-Fa-f]+)\s+(?<flags>[RWE ]+?)\s+0x[0-9A-Fa-f]+\s*$")]
    private static partial Regex LoadSegmentLineRegex();

    [GeneratedRegex(
        @"^\s*\d+:\s+(?<address>[0-9A-Fa-f]+)\s+(?<size>\d+)\s+(?<type>\S+)\s+(?<binding>\S+)\s+(?<visibility>\S+)\s+(?<section>\S+)\s+(?<name>.+?)\s*$")]
    private static partial Regex SymbolLineRegex();

    [GeneratedRegex(
        @"^\s*(?<address>[0-9A-Fa-f]+)\s+(?<size>[0-9A-Fa-f]+)\s+(?<type>\S)\s+(?<name>.+?)\s*$")]
    private static partial Regex NmLineRegex();

    internal readonly record struct NmSymbolKey(string Name, ulong Address, ulong Size);

    internal readonly record struct ElfLoadSegment(ulong Address, ulong Size, string Flags);

    private sealed record ToolResult(string StandardOutput, string StandardError);
}
