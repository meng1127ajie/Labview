using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using V3RttMonitor.Core.CanBus;
using V3RttMonitor.Core.Diagnostics;
using V3RttMonitor.Core.Export;
using V3RttMonitor.Core.Hss;
using V3RttMonitor.Core.Protocol;
using V3RttMonitor.Core.Transport;
using V3RttMonitor.Core.Visualization;
using Hsu.Formats.Blf;
using Hsu.Formats.Trace;
using CanKit.Core;
using V3RttMonitor.CanAdapters;
using KitCanFrame = CanKit.Abstractions.API.Can.Definitions.CanFrame;

var tests = new (string Name, Action Body)[]
{
    ("自动识别66通道", () => AutoDetectsChannelCount(66)),
    ("自动识别12通道", () => AutoDetectsChannelCount(12)),
    ("任意TCP分片", ParsesRandomChunks),
    ("半帧起始重同步", ResynchronizesFromPartialFrame),
    ("负载内正无穷不误锁", IgnoresInfinityInsidePayload),
    ("手动通道数", ParsesConfiguredCount),
    ("未加载模板不猜测变量名", GenericCatalogDoesNotGuessNames),
    ("滚轮方向符合Windows与ScottPlot约定", WheelZoomDirectionIsStandard),
    ("SEQ固定步长不会误报丢帧", LearnsStableSequenceStep),
    ("SEQ累计确认并回算真实缺口", LearnsSequenceStepFromCumulativeEvidence),
    ("SEQ非整数跳变与重启分开统计", SeparatesSequenceAnomaliesAndRestarts),
    ("CSV导出", ExportsCsv),
    ("TCP客户端直连与握手", DirectTcpClientReceivesAllBytes),
    ("ELF解析实际RAM变量", ReadsActualElfSymbols),
    ("HSS混合类型缓冲解包", DecodesHssSamples),
    ("J-Link V952包含HSS导出", ValidatesInstalledHssExports),
    ("HSS模拟未启动统计安全", SimulatedHssPreStartStatisticsAreSafe),
    ("HSS模拟按请求时间轴补样", SimulatedHssGeneratesRequestedTimeline),
    ("DBC解析Intel与Motorola信号", ParsesAndDecodesDbcSignals),
    ("DBC写出后可再次解析", WritesRoundTripDbc),
    ("DBC位布局检测越界与重叠", ValidatesDbcBitLayout),
    ("ASC与candump通用报文解析", ParsesCanTextFormats),
    ("Vector CAN FD文本解析", ParsesVectorCanFd),
    ("示例ASC与DBC端到端读取", ReadsCanSampleFiles),
    ("TCP CAN任意分片与解析统计", TcpCanSourceReceivesFrames),
    ("TCP CAN无换行原始字节诊断", TcpCanSourceReportsRawBytesWithoutLines),
    ("CAN适配器提供ZLG硬件与虚拟端点", DiscoversUniversalCanEndpoints),
    ("通用CAN虚拟总线端到端接收", ReceivesFromVirtualCanAdapter),
    ("官方x64 ZLGCAN运行库可加载", LoadsOfficialZlgX64Runtime),
    ("Vector BLF写入后读取", ReadsVectorBlf),
    ("多DBC后加载优先与冲突报告", MergesMultipleDbcFiles),
    ("多段CAN日志时间轴合并", MergesCanLogTimelines),
};

var failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}
Console.WriteLine($"\n结果：{tests.Length - failed}/{tests.Length} 通过");
return failed == 0 ? 0 : 1;

static void AutoDetectsChannelCount(int count)
{
    var stream = Enumerable.Range(0, 5).SelectMany(i => BuildFrame(count, i, 1000 + i * 5)).ToArray();
    var parser = new JustFloatParser();
    var frames = parser.Feed(stream);
    Equal(count, parser.FloatCount, "识别通道数");
    Equal(5, frames.Count, "帧数");
    Equal((long)4, frames[^1].Sequence, "末帧SEQ");
}

static void ParsesRandomChunks()
{
    const int count = 66;
    var stream = Enumerable.Range(0, 20).SelectMany(i => BuildFrame(count, i, 2000 + i * 5)).ToArray();
    var parser = new JustFloatParser();
    var frames = new List<RttFrame>();
    var random = new Random(431);
    for (var offset = 0; offset < stream.Length;)
    {
        var length = Math.Min(random.Next(1, 521), stream.Length - offset);
        frames.AddRange(parser.Feed(stream.AsSpan(offset, length)));
        offset += length;
    }
    Equal(20, frames.Count, "分片解析帧数");
    True(parser.IsLocked, "解析器应锁定");
}

static void ResynchronizesFromPartialFrame()
{
    const int count = 12;
    var full = Enumerable.Range(100, 5).SelectMany(i => BuildFrame(count, i, 3000 + i * 5)).ToArray();
    var partial = full[17..];
    var parser = new JustFloatParser();
    var frames = parser.Feed(partial);
    Equal(count, parser.FloatCount, "半帧识别通道数");
    Equal(4, frames.Count, "丢弃半帧后完整帧数");
    Equal((long)101, frames[0].Sequence, "首个完整SEQ");
}

static void IgnoresInfinityInsidePayload()
{
    const int count = 20;
    var frames = Enumerable.Range(0, 5).Select(i => BuildFrame(count, i, 4000 + i * 5)).ToArray();
    foreach (var frame in frames)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(11 * sizeof(float), sizeof(uint)), JustFloatParser.TailWord);
    }
    var parser = new JustFloatParser();
    var parsed = parser.Feed(frames.SelectMany(frame => frame).ToArray());
    Equal(5, parsed.Count, "含+Inf帧数");
    True(float.IsPositiveInfinity(parsed[0].Values[11]), "+Inf应作为数据保留");
}

static void ParsesConfiguredCount()
{
    const int count = 8;
    var parser = new JustFloatParser();
    parser.SetFloatCount(count);
    var stream = Enumerable.Range(0, 3).SelectMany(i => BuildFrame(count, i, 5000 + i)).ToArray();
    var parsed = parser.Feed(stream);
    Equal(3, parsed.Count, "手动通道帧数");
    Equal(count, parser.FloatCount, "手动通道数");
}

static void GenericCatalogDoesNotGuessNames()
{
    var fields = RttFieldCatalog.GetAll(66);
    Equal(66, fields.Count, "通道目录数量");
    Equal("CH_0", fields[0].Key, "首通道名称");
    Equal("通道 23", fields[23].DisplayName, "通道显示名");
    Equal("未配置", fields[55].Group, "未加载模板分组");
    True(fields.All(field => field.IsAutoGenerated), "默认目录应全部标记为自动通道");
}

static void WheelZoomDirectionIsStandard()
{
    var zoomIn = WheelZoom.FactorForDelta(120);
    var zoomOut = WheelZoom.FactorForDelta(-120);
    True(zoomIn > 1, "滚轮向上应放大");
    True(zoomOut < 1, "滚轮向下应缩小");
    True(Math.Abs(zoomIn * zoomOut - 1) < 1e-12, "正反一格应互为倒数");
}

static void LearnsStableSequenceStep()
{
    var tracker = new SequenceContinuityTracker();
    foreach (var sequence in new long[] { 10_000, 10_100, 10_200, 10_300, 10_400 })
    {
        tracker.Observe(sequence);
    }

    var result = tracker.GetSnapshot();
    True(result.IsStepConfirmed, "连续3次相同步长后应确认");
    Equal(100L, result.NominalStep!.Value, "正常SEQ步长");
    Equal(0L, result.LostFrames, "固定+100不能按+1误报丢帧");
    Equal(0L, result.GapEvents, "连续数据不应产生缺口事件");
}

static void LearnsSequenceStepFromCumulativeEvidence()
{
    var tracker = new SequenceContinuityTracker();
    // Normal +100 increments are interleaved with two real gaps. The third
    // cumulative +100 observation confirms the convention and reclassifies
    // the earlier +200/+300 transitions.
    foreach (var sequence in new long[] { 0, 100, 300, 400, 700, 800 })
    {
        tracker.Observe(sequence);
    }

    var result = tracker.GetSnapshot();
    Equal(100L, result.NominalStep!.Value, "累计证据确认步长");
    Equal(3L, result.LostFrames, "应回算1+2个缺失样本");
    Equal(2L, result.GapEvents, "两个整数倍缺口事件");
    Equal(6L, result.ReceivedFrames, "实际接收帧数");
}

static void SeparatesSequenceAnomaliesAndRestarts()
{
    var tracker = new SequenceContinuityTracker();
    foreach (var sequence in new long[] { 0, 100, 200, 300, 450, 650, 650, 25 })
    {
        tracker.Observe(sequence);
    }

    var result = tracker.GetSnapshot();
    Equal(1L, result.LostFrames, "+200只代表缺失1个采样");
    Equal(1L, result.GapEvents, "整数倍跳变事件数");
    Equal(2L, result.Anomalies, "+150与重复SEQ均是异常而不是丢帧");
    Equal(1L, result.Restarts, "SEQ回退单独记为重启");
    True(Math.Abs(result.LossRatePercent - 100.0 / 9.0) < 1e-9, "丢帧率分母应为收到+丢失");
}

static void ExportsCsv()
{
    const int count = 12;
    var parser = new JustFloatParser();
    var frames = parser.Feed(Enumerable.Range(0, 3).SelectMany(i => BuildFrame(count, i, 6000 + i)).ToArray());
    var path = Path.Combine(Path.GetTempPath(), $"JustFloatCsv_{Guid.NewGuid():N}.csv");
    try
    {
        var descriptors = RttFieldCatalog.GetAll(count).Take(4).ToArray();
        RttCsvExporter.Write(path, frames.Select((frame, index) => (index, frame)), descriptors);
        var lines = File.ReadAllLines(path);
        Equal(4, lines.Length, "CSV行数");
        True(lines[0].StartsWith("FrameIndex,CH_0,CH_1", StringComparison.Ordinal), "CSV表头");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void DirectTcpClientReceivesAllBytes() => DirectTcpClientReceivesAllBytesAsync().GetAwaiter().GetResult();

static void ReadsActualElfSymbols() => ReadsActualElfSymbolsAsync().GetAwaiter().GetResult();

static async Task ReadsActualElfSymbolsAsync()
{
    var root = FindWorkspaceRoot();
    var elf = Path.Combine(root, "M_MOT_Standard_Calibrate_142.elf");
    var catalog = await new ElfSymbolReader().ReadAsync(elf);
    True(catalog.Symbols.Count >= 100, "ELF应包含大量RAM对象符号");
    True(catalog.Symbols.Any(item => item.Name == "Mtr1Ctrl"), "应解析Mtr1Ctrl变量");
    True(catalog.Search(new ElfSymbolSearchOptions { SearchText = "Mtr1", ScalarOnly = false }).Count > 0, "ELF搜索应返回结果");
}

static void DecodesHssSamples()
{
    var floatSymbol = TestSymbol("speed", 0x20000000, 4);
    var intSymbol = TestSymbol("state", 0x20000004, 2);
    var variables = new[]
    {
        new HssVariableSelection { Symbol = floatSymbol, NumericType = ElfNumericType.Float32 },
        new HssVariableSelection { Symbol = intSymbol, NumericType = ElfNumericType.Int16 },
    };
    var decoder = new HssSampleDecoder(variables);
    var data = new byte[decoder.SampleSize * 2];
    WriteSample(0, 1_000, 1.5f, -12);
    WriteSample(decoder.SampleSize, 2_000, 2.5f, 7);
    var samples = decoder.Decode(data);
    Equal(2, samples.Count, "HSS样本数");
    True(Math.Abs(samples[0].Values[0] - 1.5) < 1e-6, "float变量解码");
    True(Math.Abs(samples[0].Values[1] + 12) < 1e-6, "int16变量解码");
    Equal(2_000UL, samples[1].TimestampUs, "HSS微秒时间戳");

    void WriteSample(int offset, uint timestamp, float value, short state)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(value));
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 8, 2), state);
    }
}

static void ValidatesInstalledHssExports()
{
    const string dll = @"C:\Program Files\SEGGER\JLink_V952\JLink_x64.dll";
    if (!File.Exists(dll)) return;
    var errors = JLinkHssCompatibility.ValidateExports(dll);
    Equal(0, errors.Count, "V952 HSS导出完整性");
}

static void SimulatedHssPreStartStatisticsAreSafe()
{
    var session = new SimulatedHssSession();
    var statistics = session.GetStatistics();
    True(!statistics.IsRunning, "模拟会话不应默认运行");
    Equal(0L, statistics.ReceivedSamples, "未启动样本数");
    True(statistics.Capabilities is null, "未启动前能力应等待配置");
}

static void SimulatedHssGeneratesRequestedTimeline() => SimulatedHssGeneratesRequestedTimelineAsync().GetAwaiter().GetResult();

static async Task SimulatedHssGeneratesRequestedTimelineAsync()
{
    await using var session = new SimulatedHssSession();
    var samples = new List<HssSample>();
    session.SampleReceived += sample => { lock (samples) samples.Add(sample); };
    await session.StartAsync(new HssConfiguration
    {
        DllPath = string.Empty,
        PeriodUs = 10_000,
        Variables = [new HssVariableSelection { Symbol = TestSymbol("value", 0x20000000, 4), NumericType = ElfNumericType.Float32 }],
    });
    await Task.Delay(260);
    await session.StopAsync();
    HssSample[] snapshot;
    lock (samples) snapshot = samples.ToArray();
    True(snapshot.Length >= 20, "100Hz模拟在260ms内应产生至少20点");
    True(snapshot.Zip(snapshot.Skip(1)).All(pair => pair.Second.TimestampUs - pair.First.TimestampUs == 10_000), "模拟时间戳应严格按周期递增");
}

static void ParsesAndDecodesDbcSignals()
{
    const string dbc = """
VERSION "test"
NS_ :
BS_:
BU_: Vector__XXX
BO_ 256 TestMessage: 8 Vector__XXX
 SG_ Intel16 : 0|16@1+ (0.1,-40) [0|1000] "V" Vector__XXX
 SG_ Motorola16 : 23|16@0+ (1,0) [0|65535] "rpm" Vector__XXX
 SG_ Signed12 : 32|12@1- (1,0) [-2048|2047] "A" Vector__XXX
VAL_ 256 Intel16 4660 "Ready" ;
""";
    var result = DbcParser.Parse(dbc, "test");
    Equal(1, result.Database.Messages.Count, "DBC报文数");
    var message = result.Database.Messages[0];
    Equal(3, message.Signals.Count, "DBC信号数");
    var payload = new byte[] { 0x34, 0x12, 0xAB, 0xCD, 0, 0, 0, 0 };
    True(DbcCodec.TryDecode(message, message.Signals[0], payload, out var intel), "Intel解码");
    True(Math.Abs(intel.PhysicalValue - 426) < 1e-9, "Intel物理值");
    Equal("Ready", intel.ChoiceText!, "枚举文本");
    True(DbcCodec.TryDecode(message, message.Signals[1], payload, out var motorola), "Motorola解码");
    Equal(0xABCDUL, motorola.RawUnsigned, "Motorola跨字节原始值");

    True(DbcCodec.TryEncode(message, message.Signals[2], -12, payload, out var error), error ?? "有符号编码失败");
    True(DbcCodec.TryDecode(message, message.Signals[2], payload, out var signed), "有符号解码");
    Equal(-12L, signed.RawSigned, "12位符号扩展");
}

static void WritesRoundTripDbc()
{
    var database = new DbcDatabase { Name = "Generated" };
    var message = new DbcMessage { Id = 0x18FF50E5, IsExtended = true, Name = "MotorStatus", Length = 8, CycleTimeMs = 10 };
    var sourceSignal = new DbcSignal { Name = "Speed", StartBit = 0, Length = 16, ByteOrder = DbcByteOrder.Intel, Factor = 0.1, Unit = "rpm", Maximum = 6553.5, Comment = "motor speed" };
    sourceSignal.Choices[0] = "Stopped";
    message.Signals.Add(sourceSignal);
    database.Messages.Add(message);
    var text = DbcWriter.Write(database);
    var reparsed = DbcParser.Parse(text, "Generated");
    Equal(1, reparsed.Database.Messages.Count, "回读报文数");
    var restored = reparsed.Database.Messages[0];
    Equal(message.Id, restored.Id, "扩展ID回读");
    True(restored.IsExtended, "扩展帧标记回读");
    Equal(10, restored.CycleTimeMs!.Value, "周期回读");
    True(Math.Abs(restored.Signals[0].Factor - 0.1) < 1e-12, "系数回读");
    Equal("motor speed", restored.Signals[0].Comment, "注释回读");
    Equal("Stopped", restored.Signals[0].Choices[0], "枚举回读");
}

static void ValidatesDbcBitLayout()
{
    var message = new DbcMessage { Id = 1, Name = "Layout", Length = 2 };
    var first = new DbcSignal { Name = "A", StartBit = 0, Length = 8, ByteOrder = DbcByteOrder.Intel };
    var overlap = new DbcSignal { Name = "B", StartBit = 4, Length = 8, ByteOrder = DbcByteOrder.Intel };
    var outside = new DbcSignal { Name = "C", StartBit = 15, Length = 16, ByteOrder = DbcByteOrder.Motorola };
    message.Signals.AddRange([first, overlap, outside]);
    var layout = DbcCodec.BuildBitLayout(message);
    True(layout.Any(cell => cell.HasConflict), "应检测信号重叠");
    True(!DbcCodec.ValidateSignal(message, outside).IsValid, "应检测越界");
    Equal(16, layout.Count, "两字节位图单元数");
}

static void ParsesCanTextFormats()
{
    var context = new CanTextParseContext();
    True(CanTextFrameParser.TryParse("0.001000 1 123 Rx d 8 01 02 03 04 05 06 07 08", context, out var asc, out _), "ASC经典帧");
    Equal(0x123u, asc.Id, "ASC ID");
    Equal(8, asc.Data.Length, "ASC数据长度");
    True(CanTextFrameParser.TryParse("(12.345678) can0 18FF50E5#11223344", context, out var candump, out _), "candump帧");
    True(candump.IsExtended, "candump扩展帧");
    Equal(4, candump.Data.Length, "candump数据长度");
    context.FallbackTimestampSeconds = 3.25;
    True(CanTextFrameParser.TryParse("456#AABB", context, out var compact, out _), "紧凑实时帧");
    True(Math.Abs(compact.TimestampSeconds - 3.25) < 1e-12, "实时回退时间戳");
}

static void ParsesVectorCanFd()
{
    var context = new CanTextParseContext();
    const string line = "0.123000 CANFD 1 Rx 123 1 0 9 12 01 02 03 04 05 06 07 08 09 0A 0B 0C";
    True(CanTextFrameParser.TryParse(line, context, out var frame, out var error), error ?? "CAN FD解析失败");
    True(frame.IsFd, "CAN FD标志");
    True(frame.BitrateSwitch, "BRS标志");
    Equal(12, frame.Data.Length, "CAN FD载荷长度");
    Equal(9, frame.Dlc, "CAN FD DLC编码");
}

static void ReadsCanSampleFiles() => ReadsCanSampleFilesAsync().GetAwaiter().GetResult();

static async Task ReadsCanSampleFilesAsync()
{
    var root = FindWorkspaceRoot();
    var dbcResult = await DbcParser.ParseFileAsync(Path.Combine(root, "Samples", "CAN", "demo.dbc"));
    var logResult = await new TextCanLogReader().ReadAsync(Path.Combine(root, "Samples", "CAN", "demo.asc"));
    Equal(3, dbcResult.Database.Messages.Count, "示例DBC报文数");
    Equal(23, logResult.Frames.Count, "示例ASC帧数");
    Equal(3, logResult.Frames.Select(frame => frame.Key).Distinct().Count(), "示例ASC ID数");
    var speedMessage = dbcResult.Database.FindMessage(0x100, false)!;
    var lastSpeedFrame = logResult.Frames.Last(frame => frame.Id == 0x100);
    True(DbcCodec.TryDecode(speedMessage, speedMessage.Signals[0], lastSpeedFrame.Data, out var speed), "示例转速解码");
    True(Math.Abs(speed.PhysicalValue - 150) < 1e-9, "示例末帧转速");
}

static void TcpCanSourceReceivesFrames() => TcpCanSourceReceivesFramesAsync().GetAwaiter().GetResult();

static async Task TcpCanSourceReceivesFramesAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await using var source = new TcpCanFrameSource("127.0.0.1", port);
    var received = new List<CanFrame>();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    string? parseFailureStatus = null;
    source.StatusChanged += status =>
    {
        if (status.Contains("解析失败", StringComparison.Ordinal)) parseFailureStatus = status;
    };
    source.FrameReceived += frame =>
    {
        lock (received)
        {
            received.Add(frame);
            if (received.Count == 2) completion.TrySetResult();
        }
    };
    try
    {
        var start = source.StartAsync();
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        var stream = client.GetStream();
        var fragments = new[]
        {
            "123#01",
            "020304\r",
            "\nINVALID-CAN-LINE\n",
            "(1.250",
            "000) can0 18FF50E5#AABBCCDD\r\n\n",
        };
        var expectedBytes = 0;
        foreach (var fragment in fragments)
        {
            var bytes = Encoding.ASCII.GetBytes(fragment);
            expectedBytes += bytes.Length;
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(
            () => source.GetStatistics() is { ReceivedLines: 3, ParsedFrames: 2, ParseErrors: 1 },
            TimeSpan.FromSeconds(3));
        lock (received)
        {
            Equal(2, received.Count, "TCP CAN帧数");
            Equal(0x123u, received[0].Id, "第一帧ID");
            Equal(0x18FF50E5u, received[1].Id, "第二帧ID");
        }
        var statistics = source.GetStatistics();
        Equal((long)expectedBytes, statistics.ReceivedBytes, "TCP CAN原始字节数");
        Equal(3L, statistics.ReceivedLines, "TCP CAN非空换行记录数");
        Equal(2L, statistics.ParsedFrames, "TCP CAN解析成功数");
        Equal(1L, statistics.ParseErrors, "TCP CAN解析失败数");
        True(parseFailureStatus?.Contains("INVALID-CAN-LINE", StringComparison.Ordinal) == true, "首个解析失败应包含原始行诊断");
        True(statistics.LastRawPreview.Length > 0, "TCP CAN应保留最后原始数据预览");
    }
    finally
    {
        listener.Stop();
        await source.StopAsync();
    }
}

static void TcpCanSourceReportsRawBytesWithoutLines() => TcpCanSourceReportsRawBytesWithoutLinesAsync().GetAwaiter().GetResult();

static async Task TcpCanSourceReportsRawBytesWithoutLinesAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    await using var source = new TcpCanFrameSource("127.0.0.1", port);
    try
    {
        var start = source.StartAsync();
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        var raw = new byte[] { 0x4A, 0x4C, 0x00, 0x80 };
        await client.GetStream().WriteAsync(raw);
        await client.GetStream().FlushAsync();
        await WaitUntilAsync(
            () => source.GetStatistics() is { ReceivedBytes: var bytes, LastRawPreview.Length: > 0 } && bytes == raw.Length,
            TimeSpan.FromSeconds(3));

        var statistics = source.GetStatistics();
        Equal((long)raw.Length, statistics.ReceivedBytes, "无换行时仍应统计原始字节");
        Equal(0L, statistics.ReceivedLines, "无换行不应统计文本行");
        Equal(0L, statistics.ParsedFrames, "无换行不应解析帧");
        Equal(0L, statistics.ParseErrors, "未终止的字节块不应误报格式失败");
        Equal("4A 4C 00 80", statistics.LastRawPreview, "二进制原始数据应以十六进制预览");
    }
    finally
    {
        listener.Stop();
        await source.StopAsync();
    }
}

static void DiscoversUniversalCanEndpoints() => DiscoversUniversalCanEndpointsAsync().GetAwaiter().GetResult();

static async Task DiscoversUniversalCanEndpointsAsync()
{
    var factory = new CanKitFrameSourceFactory();
    var endpoints = await factory.DiscoverAsync();
    True(endpoints.Any(item => item.Endpoint.StartsWith("zlg://", StringComparison.OrdinalIgnoreCase)), "应提供ZLG硬件端点");
    True(endpoints.Any(item => item.Endpoint.StartsWith("virtual://", StringComparison.OrdinalIgnoreCase)), "应提供虚拟测试端点");
}

static void ReceivesFromVirtualCanAdapter() => ReceivesFromVirtualCanAdapterAsync().GetAwaiter().GetResult();

static async Task ReceivesFromVirtualCanAdapterAsync()
{
    var session = $"justfloat-{Guid.NewGuid():N}";
    var factory = new CanKitFrameSourceFactory();
    await using var receiver = factory.Create(new CanSourceConnectionOptions
    {
        Endpoint = $"virtual://{session}/0",
        Protocol = CanLinkProtocol.Classic,
        NominalBitrate = 500_000,
        ListenOnly = false,
    });
    var completion = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.FrameReceived += frame => completion.TrySetResult(frame);
    await receiver.StartAsync();
    using var sender = CanBus.Open($"virtual://{session}/1", cfg => cfg.Baud(500_000));
    var sent = sender.Transmit(KitCanFrame.Classic(0x321, new byte[] { 0x11, 0x22, 0x33 }));
    Equal(1, sent, "虚拟CAN发送帧数");
    var received = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Equal(0x321u, received.Id, "虚拟CAN接收ID");
    Equal(3, received.Data.Length, "虚拟CAN数据长度");
    Equal((byte)0x22, received.Data[1], "虚拟CAN负载");
    True(receiver is ICanFrameSourceDiagnostics diagnostics && diagnostics.GetStatistics().ParsedFrames == 1, "硬件适配层接收统计");
}

static void LoadsOfficialZlgX64Runtime()
{
    var status = CanDriverRuntimeProbe.ValidateZlg();
    True(status.IsReady, status.Message);
    Equal("x64", status.Architecture, "ZLGCAN运行库架构");
    True(status.Path.EndsWith("zlgcan.dll", StringComparison.OrdinalIgnoreCase), "ZLGCAN运行库路径");
}

static void ReadsVectorBlf() => ReadsVectorBlfAsync().GetAwaiter().GetResult();

static async Task ReadsVectorBlfAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"JustFloatBlf_{Guid.NewGuid():N}.blf");
    try
    {
        using (var writer = new BlfFileWriter(path, DateTime.UtcNow))
        {
            writer.Append(0x123, [0x11, 0x22, 0x33], CanFrameDirection.Rx, 1, false);
            writer.Append(0x18FF50E5, [0xAA, 0xBB, 0xCC, 0xDD], CanFrameDirection.Tx, 2, true);
            writer.Complete();
        }
        var result = await new BlfCanLogReader().ReadAsync(path);
        Equal(2, result.Frames.Count, "BLF帧数");
        Equal(0x123u, result.Frames[0].Id, "BLF标准ID");
        True(result.Frames[1].IsExtended, "BLF扩展帧");
        True(result.Frames[1].Direction == CanDirection.Tx, "BLF方向");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void MergesMultipleDbcFiles()
{
    var first = new DbcDatabase { Name = "Base" };
    first.Messages.Add(new DbcMessage { Id = 0x100, Name = "Old", Length = 8 });
    var second = new DbcDatabase { Name = "Override" };
    second.Messages.Add(new DbcMessage { Id = 0x100, Name = "New", Length = 8 });
    second.Messages.Add(new DbcMessage { Id = 0x200, Name = "Extra", Length = 8 });
    var result = DbcDatabaseMerger.Merge([
        new DbcSourceDatabase("Base", "base.dbc", first),
        new DbcSourceDatabase("Override", "override.dbc", second),
    ]);
    Equal(2, result.Database.Messages.Count, "多DBC合并报文数");
    Equal("New", result.Database.FindMessage(0x100, false)!.Name, "后加载DBC优先");
    Equal(1, result.Conflicts.Count, "重复ID冲突数");
}

static void MergesCanLogTimelines()
{
    var existing = new[] { CanFrameAt(0, 0), CanFrameAt(1, 0) };
    var next = new CanLogSegment("second.asc", [CanFrameAt(10, 0), CanFrameAt(11, 0)]);
    var continuous = CanLogMerger.Merge(existing, [next], CanLogMergeMode.AppendContinuous);
    True(Math.Abs(continuous[2].TimestampSeconds - 2) < 1e-12, "紧接模式新段起点");
    Equal(1, continuous[2].SegmentIndex, "新段编号");
    var withGap = CanLogMerger.Merge(existing, [next], CanLogMergeMode.AppendWithGap, 5);
    True(Math.Abs(withGap[2].TimestampSeconds - 6) < 1e-12, "指定间隔模式起点");
    var preserved = CanLogMerger.Merge(existing, [next], CanLogMergeMode.PreserveOriginalTime);
    True(Math.Abs(preserved[^1].TimestampSeconds - 11) < 1e-12, "保留原始时间模式");

    static CanFrame CanFrameAt(double time, int segment) => new()
    {
        TimestampSeconds = time,
        Id = 0x100,
        Dlc = 1,
        Data = [0],
        SegmentIndex = segment,
    };
}

static ElfSymbol TestSymbol(string name, ulong address, ulong size) => new()
{
    Name = name,
    Address = address,
    Size = size,
    SectionName = ".bss",
    SectionIndex = 1,
    Binding = ElfSymbolBinding.Global,
    Visibility = "DEFAULT",
    NmType = 'B',
    Kind = ElfSymbolKind.UninitializedData,
};

static string FindWorkspaceRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "V3RttMonitor.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("找不到V3RttMonitor.sln。");
}

static async Task DirectTcpClientReceivesAllBytesAsync()
{
    const int count = 12;
    const int frameCount = 20;
    var streamBytes = Enumerable.Range(0, frameCount).SelectMany(i => BuildFrame(count, i, 7000 + i * 5)).ToArray();
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var recordPath = Path.Combine(Path.GetTempPath(), $"JustFloatTcp_{Guid.NewGuid():N}.bin");
    var received = new List<RttFrame>();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var session = new RttSession();
    session.FrameReceived += frame =>
    {
        lock (received)
        {
            received.Add(frame);
            if (received.Count >= frameCount) completion.TrySetResult();
        }
    };

    try
    {
        session.StartRecording(recordPath);
        await session.StartAsync(new RttSessionSettings
        {
            Host = "127.0.0.1",
            Port = port,
            HandshakeData = "plot0",
            ExpectedFloatCount = 0,
            ConnectTimeoutMs = 2000,
        });

        using var serverClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var network = serverClient.GetStream();
        var handshake = new byte[5];
        await network.ReadExactlyAsync(handshake).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Equal("plot0", Encoding.UTF8.GetString(handshake), "握手内容");

        for (var offset = 0; offset < streamBytes.Length;)
        {
            var length = Math.Min(137, streamBytes.Length - offset);
            await network.WriteAsync(streamBytes.AsMemory(offset, length));
            offset += length;
        }
        await network.FlushAsync();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var liveStatistics = session.GetStatistics();
        Equal((long)frameCount, liveStatistics.ReceivedFrames, "清空前会话帧数");
        Equal(1L, liveStatistics.SequenceStep, "在线统计学习SEQ步长");
        True(liveStatistics.IsSequenceStepConfirmed, "在线SEQ步长应已确认");
        Equal(0L, liveStatistics.LostFrames, "完整在线数据不应误报丢帧");
        Equal(0L, liveStatistics.GapEvents, "完整在线数据无缺口事件");
        True(liveStatistics.RecentFramesPerSecond > 0, "最近2秒帧率应有值");
        True(liveStatistics.AverageFramesPerSecond > 0, "全程平均帧率应有值");
        True(liveStatistics.RecentBytesPerSecond > 0, "最近2秒吞吐应有值");
        session.ClearStatistics();
        var cleared = session.GetStatistics();
        Equal(0L, cleared.ReceivedFrames, "清空后会话帧数");
        Equal(0L, cleared.ReceivedBytes, "清空后会话字节数");
        Equal(-1L, cleared.LastSequence, "清空后末帧SEQ");
        session.StopRecording();
        await session.StopAsync();

        lock (received)
        {
            Equal(frameCount, received.Count, "TCP接收帧数");
            Equal(count, received[0].FloatCount, "TCP自动通道数");
        }
        Equal(streamBytes.Length, checked((int)new FileInfo(recordPath).Length), "原始记录字节数");
    }
    finally
    {
        listener.Stop();
        await session.StopAsync();
        if (File.Exists(recordPath)) File.Delete(recordPath);
    }
}

static byte[] BuildFrame(int count, float sequence, float timeMs)
{
    var payload = count * sizeof(float);
    var bytes = new byte[payload + sizeof(uint)];
    for (var i = 0; i < count; i++) Write(i, i + sequence / 10f);
    Write(0, sequence);
    Write(1, timeMs);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payload), JustFloatParser.TailWord);
    return bytes;

    void Write(int index, float value) => BinaryPrimitives.WriteInt32LittleEndian(
        bytes.AsSpan(index * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(value));
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException($"等待条件超时（{timeout.TotalSeconds:F1}s）");
        await Task.Delay(10);
    }
}

static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}
