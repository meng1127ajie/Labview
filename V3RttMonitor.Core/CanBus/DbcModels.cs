using System.Globalization;

namespace V3RttMonitor.Core.CanBus;

public enum DbcByteOrder
{
    Motorola = 0,
    Intel = 1,
}

public enum DbcSignalValueType
{
    Integer,
    Float32,
    Float64,
}

public sealed class DbcDatabase
{
    public string Name { get; set; } = "CAN_Database";
    public List<DbcMessage> Messages { get; } = [];

    public DbcMessage? FindMessage(uint id, bool isExtended)
    {
        return Messages.FirstOrDefault(message => message.Id == id && message.IsExtended == isExtended)
            ?? Messages.FirstOrDefault(message => message.Id == id);
    }
}

public sealed class DbcMessage
{
    public uint Id { get; set; }
    public bool IsExtended { get; set; }
    public string Name { get; set; } = "Message";
    public int Length { get; set; } = 8;
    public string Sender { get; set; } = "Vector__XXX";
    public string Comment { get; set; } = string.Empty;
    public int? CycleTimeMs { get; set; }
    public List<DbcSignal> Signals { get; } = [];

    public CanFrameKey Key => new(Id, IsExtended);
    public uint FileId => IsExtended ? Id | 0x80000000u : Id;
}

public sealed class DbcSignal
{
    public string Name { get; set; } = "Signal";
    public int StartBit { get; set; }
    public int Length { get; set; } = 8;
    public DbcByteOrder ByteOrder { get; set; } = DbcByteOrder.Intel;
    public bool IsSigned { get; set; }
    public double Factor { get; set; } = 1;
    public double Offset { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Receiver { get; set; } = "Vector__XXX";
    public string Comment { get; set; } = string.Empty;
    public bool IsMultiplexer { get; set; }
    public int? MultiplexerValue { get; set; }
    public DbcSignalValueType ValueType { get; set; } = DbcSignalValueType.Integer;
    public Dictionary<long, string> Choices { get; } = [];

    public string DefinitionText => $"{StartBit}|{Length}@{(int)ByteOrder}{(IsSigned ? '-' : '+')} ({Factor.ToString("G", CultureInfo.InvariantCulture)},{Offset.ToString("G", CultureInfo.InvariantCulture)})";
}

public sealed record DbcDiagnostic(int LineNumber, string Message, bool IsError = false);

public sealed record DbcParseResult
{
    public required DbcDatabase Database { get; init; }
    public IReadOnlyList<DbcDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record DecodedCanSignal
{
    public required DbcMessage Message { get; init; }
    public required DbcSignal Signal { get; init; }
    public ulong RawUnsigned { get; init; }
    public long RawSigned { get; init; }
    public double PhysicalValue { get; init; }
    public string? ChoiceText { get; init; }
}

public sealed record DbcSignalValidation
{
    public required DbcSignal Signal { get; init; }
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<int> PhysicalBits { get; init; } = [];
}

public sealed record DbcBitCell(int GlobalBit, int ByteIndex, int BitInByte, IReadOnlyList<DbcSignal> Signals)
{
    public bool HasConflict => Signals.Count > 1;
}
