namespace V3RttMonitor.Core.Hss;

/// <summary>
/// HSS can only return bytes.  The ELF symbol table contains an object's size,
/// but does not reliably contain its C/C++ scalar type, so the user may select
/// one of these interpretations before starting a capture.
/// </summary>
public enum ElfNumericType
{
    Unsupported = 0,
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    UInt64,
    Int64,
    Float64,
}

public enum ElfSymbolBinding
{
    Unknown = 0,
    Local,
    Global,
    Weak,
}

/// <summary>Classification inferred from GNU nm's symbol letter.</summary>
public enum ElfSymbolKind
{
    Unknown = 0,
    InitializedData,
    UninitializedData,
    SmallInitializedData,
    SmallUninitializedData,
    Common,
    WeakObject,
}

/// <summary>A defined object located in an allocated, writable ELF section.</summary>
public sealed record ElfSymbol
{
    public required string Name { get; init; }
    public required ulong Address { get; init; }
    public required ulong Size { get; init; }
    public required string SectionName { get; init; }
    public required int SectionIndex { get; init; }
    public required ElfSymbolBinding Binding { get; init; }
    public required string Visibility { get; init; }
    public required char NmType { get; init; }
    public required ElfSymbolKind Kind { get; init; }

    public string AddressText => Address <= uint.MaxValue
        ? $"0x{Address:X8}"
        : $"0x{Address:X16}";

    /// <summary>J-Link HSS V6.x uses a 32-bit address field.</summary>
    public bool IsHssAddressSupported => Address <= uint.MaxValue;

    /// <summary>True when this object's byte count can represent one scalar.</summary>
    public bool IsScalarCandidate => NumericTypes.Count > 0;

    /// <summary>
    /// Conservative choices constrained only by symbol size. Signedness and
    /// floating-point intent must be confirmed by the user/source code.
    /// </summary>
    public IReadOnlyList<ElfNumericType> NumericTypes => ElfNumericTypeInfo.ForSize(Size);

    /// <summary>
    /// Practical default for telemetry: 4-byte and 8-byte objects default to
    /// float/double; 1-byte and 2-byte objects default to unsigned integers.
    /// The UI must allow changing this value before capture.
    /// </summary>
    public ElfNumericType DefaultNumericType => ElfNumericTypeInfo.DefaultForSize(Size);
}

public static class ElfNumericTypeInfo
{
    private static readonly ElfNumericType[] OneByte =
        [ElfNumericType.UInt8, ElfNumericType.Int8];

    private static readonly ElfNumericType[] TwoBytes =
        [ElfNumericType.UInt16, ElfNumericType.Int16];

    private static readonly ElfNumericType[] FourBytes =
        [ElfNumericType.Float32, ElfNumericType.UInt32, ElfNumericType.Int32];

    private static readonly ElfNumericType[] EightBytes =
        [ElfNumericType.Float64, ElfNumericType.UInt64, ElfNumericType.Int64];

    public static IReadOnlyList<ElfNumericType> ForSize(ulong size) => size switch
    {
        1 => OneByte,
        2 => TwoBytes,
        4 => FourBytes,
        8 => EightBytes,
        _ => Array.Empty<ElfNumericType>(),
    };

    public static ElfNumericType DefaultForSize(ulong size) => size switch
    {
        1 => ElfNumericType.UInt8,
        2 => ElfNumericType.UInt16,
        4 => ElfNumericType.Float32,
        8 => ElfNumericType.Float64,
        _ => ElfNumericType.Unsupported,
    };

    public static int GetByteCount(this ElfNumericType type) => type switch
    {
        ElfNumericType.UInt8 or ElfNumericType.Int8 => 1,
        ElfNumericType.UInt16 or ElfNumericType.Int16 => 2,
        ElfNumericType.UInt32 or ElfNumericType.Int32 or ElfNumericType.Float32 => 4,
        ElfNumericType.UInt64 or ElfNumericType.Int64 or ElfNumericType.Float64 => 8,
        _ => 0,
    };
}

public sealed record ElfRamSection(
    int Index,
    string Name,
    ulong Address,
    ulong Size,
    string Flags);

/// <summary>Search/filter state suitable for binding to a variable picker.</summary>
public sealed record ElfSymbolSearchOptions
{
    public string SearchText { get; init; } = string.Empty;
    public bool IncludeLocalSymbols { get; init; } = true;
    public bool ScalarOnly { get; init; } = true;
    public int MaxResults { get; init; } = 500;
}

/// <summary>Immutable result of one ELF scan.</summary>
public sealed record ElfSymbolCatalog
{
    public required string ElfPath { get; init; }
    public required IReadOnlyList<ElfSymbol> Symbols { get; init; }
    public required IReadOnlyList<ElfRamSection> RamSections { get; init; }
    public required string NmToolPath { get; init; }
    public required string ReadElfToolPath { get; init; }

    public IReadOnlyList<ElfSymbol> Search(ElfSymbolSearchOptions? options = null)
    {
        options ??= new ElfSymbolSearchOptions();
        if (options.MaxResults <= 0)
        {
            return Array.Empty<ElfSymbol>();
        }

        string query = options.SearchText.Trim();

        return Symbols
            .Where(symbol => options.IncludeLocalSymbols || symbol.Binding != ElfSymbolBinding.Local)
            .Where(symbol => !options.ScalarOnly || symbol.IsScalarCandidate)
            .Where(symbol => query.Length == 0 ||
                             symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             symbol.SectionName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => SearchRank(symbol, query))
            .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Address)
            .Take(options.MaxResults)
            .ToArray();
    }

    private static int SearchRank(ElfSymbol symbol, string query)
    {
        if (query.Length == 0)
        {
            return symbol.Binding == ElfSymbolBinding.Global ? 0 : 1;
        }

        if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }
}
