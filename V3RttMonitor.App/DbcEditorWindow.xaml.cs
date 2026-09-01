using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using V3RttMonitor.App.ViewModels;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App;

public partial class DbcEditorWindow : Window
{
    private readonly DbcEditorViewModel _viewModel;
    private readonly Dictionary<DbcSignalEditorItem, PropertyChangedEventHandler> _subscriptions = [];

    public DbcEditorWindow(DbcDatabase? database, CanMessageItemViewModel? selectedMessage)
    {
        InitializeComponent();
        _viewModel = new DbcEditorViewModel(database, selectedMessage);
        DataContext = _viewModel;
        Loaded += (_, _) => { SubscribeSignals(); RebuildBitMap(); };
    }

    public DbcDatabase? SavedDatabase { get; private set; }
    public string? SavedPath { get; private set; }

    private void AddMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var nextId = _viewModel.Messages.Select(item => item.Id).DefaultIfEmpty(0xFFu).Max() + 1;
        var message = new DbcMessageEditorItem { Id = nextId, Name = $"Message_{nextId:X}", Length = 8 };
        _viewModel.Messages.Add(message);
        _viewModel.SelectedMessage = message;
        MessageList.ScrollIntoView(message);
        SubscribeSignals();
        RebuildBitMap();
    }

    private void RemoveMessageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.SelectedMessage;
        if (message is null || _viewModel.Messages.Count <= 1) return;
        var index = _viewModel.Messages.IndexOf(message);
        _viewModel.Messages.Remove(message);
        _viewModel.SelectedMessage = _viewModel.Messages[Math.Clamp(index, 0, _viewModel.Messages.Count - 1)];
        SubscribeSignals();
        RebuildBitMap();
    }

    private void AddSignalButton_OnClick(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.SelectedMessage;
        if (message is null) return;
        var used = message.Signals.SelectMany(signal => DbcCodec.GetPhysicalBits(signal.ToModel())).ToHashSet();
        var start = Enumerable.Range(0, message.Length).Select(index => index * 8).FirstOrDefault(bit => !Enumerable.Range(bit, 8).Any(used.Contains));
        var signal = new DbcSignalEditorItem { Name = $"Signal_{message.Signals.Count + 1}", StartBit = start, Length = 8, Maximum = 255, ByteOrder = DbcByteOrder.Intel };
        message.Signals.Add(signal);
        message.SelectedSignal = signal;
        SignalGrid.ScrollIntoView(signal);
        SubscribeSignals();
        RebuildBitMap();
    }

    private void RemoveSignalButton_OnClick(object sender, RoutedEventArgs e)
    {
        var message = _viewModel.SelectedMessage;
        var signal = message?.SelectedSignal;
        if (message is null || signal is null) return;
        var index = message.Signals.IndexOf(signal);
        message.Signals.Remove(signal);
        message.SelectedSignal = message.Signals.Count == 0 ? null : message.Signals[Math.Clamp(index, 0, message.Signals.Count - 1)];
        SubscribeSignals();
        RebuildBitMap();
    }

    private void MessageList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SubscribeSignals();
        RebuildBitMap();
    }

    private void SignalGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildBitMap();
    private void EditorValue_OnChanged(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(RebuildBitMap);
    private void EditorValue_OnTextChanged(object sender, TextChangedEventArgs e) => Dispatcher.BeginInvoke(RebuildBitMap);

    private void SubscribeSignals()
    {
        foreach (var pair in _subscriptions) pair.Key.PropertyChanged -= pair.Value;
        _subscriptions.Clear();
        foreach (var message in _viewModel.Messages)
        {
            foreach (var signal in message.Signals)
            {
                PropertyChangedEventHandler handler = (_, _) => Dispatcher.BeginInvoke(RebuildBitMap);
                signal.PropertyChanged += handler;
                _subscriptions[signal] = handler;
            }
        }
    }

    private void RebuildBitMap()
    {
        BitMapGrid.Children.Clear();
        BitMapGrid.RowDefinitions.Clear();
        BitMapGrid.ColumnDefinitions.Clear();
        var editorMessage = _viewModel.SelectedMessage;
        if (editorMessage is null) return;
        var message = editorMessage.ToModel();
        var selectedEditor = editorMessage.SelectedSignal;
        var selected = selectedEditor is null ? null : message.Signals.ElementAtOrDefault(editorMessage.Signals.IndexOf(selectedEditor));
        var layout = DbcCodec.BuildBitLayout(message).ToDictionary(cell => cell.GlobalBit);
        for (var column = 0; column < 9; column++) BitMapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = column == 0 ? new GridLength(66) : new GridLength(72) });
        for (var row = 0; row <= message.Length; row++) BitMapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });

        AddCell("Byte", 0, 0, false, null, "字节序号");
        for (var displayColumn = 0; displayColumn < 8; displayColumn++) AddCell($"Bit {7 - displayColumn}", 0, displayColumn + 1, false, null, "字节内位号");
        var selectedBits = selected is null ? [] : DbcCodec.GetPhysicalBits(selected);
        var order = selectedBits.Select((bit, index) => (bit, index)).ToDictionary(item => item.bit, item => item.index);
        for (var byteIndex = 0; byteIndex < message.Length; byteIndex++)
        {
            AddCell($"Byte {byteIndex}", byteIndex + 1, 0, false, null, $"数据字节{byteIndex}");
            for (var displayColumn = 0; displayColumn < 8; displayColumn++)
            {
                var bitInByte = 7 - displayColumn;
                var globalBit = byteIndex * 8 + bitInByte;
                var cell = layout[globalBit];
                var isSelected = selected is not null && cell.Signals.Contains(selected);
                var text = order.TryGetValue(globalBit, out var sequence) ? $"{globalBit}\n#{sequence}" : globalBit.ToString(CultureInfo.InvariantCulture);
                var tooltip = $"Byte {byteIndex}, Bit {bitInByte}, 全局位 {globalBit}";
                if (order.TryGetValue(globalBit, out sequence)) tooltip += selected!.ByteOrder == DbcByteOrder.Intel ? $"\n当前信号第{sequence}位（从LSB开始）" : $"\n当前信号读取序号{sequence}（从MSB开始）";
                if (cell.Signals.Count > 0) tooltip += "\n占用：" + string.Join(", ", cell.Signals.Select(item => item.Name));
                AddCell(text, byteIndex + 1, displayColumn + 1, isSelected, cell, tooltip, globalBit);
            }
        }

        if (selected is null)
        {
            ValidationText.Text = "请选择或新增一个信号。";
            ValidationText.Foreground = (Brush)FindResource("SecondaryTextBrush");
            return;
        }
        var validation = DbcCodec.ValidateSignal(message, selected);
        var conflicts = layout.Values.Where(cell => cell.HasConflict && cell.Signals.Contains(selected)).Select(cell => cell.GlobalBit).ToArray();
        var errors = validation.Errors.ToList();
        if (conflicts.Length > 0) errors.Add("与其他信号重叠：bit " + string.Join(", ", conflicts));
        ValidationText.Text = errors.Count == 0
            ? $"有效：{selected.Name} · {selected.ByteOrder} · {selected.StartBit}|{selected.Length} · 占用{validation.PhysicalBits.Count}位。格内#0、#1…表示读取顺序。"
            : string.Join("  ", errors);
        ValidationText.Foreground = errors.Count == 0 ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(248, 113, 113));
        EndianHelpText.Text = selected.ByteOrder == DbcByteOrder.Intel
            ? "Intel / Little Endian（DBC @1）：StartBit指向LSB；原始数值bit 0、1、2…按全局位号递增。"
            : "Motorola / Big Endian（DBC @0）：StartBit指向MSB；同一字节向Bit 0移动，跨字节后跳到下一Byte的Bit 7（锯齿顺序）。";
    }

    private void AddCell(string text, int row, int column, bool selected, DbcBitCell? cell, string tooltip, int? globalBit = null)
    {
        var conflict = cell?.HasConflict == true;
        var occupied = cell?.Signals.Count > 0;
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 52, 75)),
            BorderThickness = new Thickness(0.5),
            Background = conflict ? new SolidColorBrush(Color.FromRgb(127, 29, 29)) : selected ? new SolidColorBrush(Color.FromRgb(7, 89, 133)) : occupied ? new SolidColorBrush(Color.FromRgb(30, 58, 95)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            ToolTip = tooltip,
            Cursor = globalBit.HasValue ? Cursors.Hand : Cursors.Arrow,
            Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = selected ? 12 : 11, Foreground = selected ? Brushes.White : (Brush)FindResource("SecondaryTextBrush") },
        };
        if (globalBit is int bit)
        {
            border.MouseLeftButtonDown += (_, _) =>
            {
                if (_viewModel.SelectedMessage?.SelectedSignal is { } signal)
                {
                    signal.StartBit = bit;
                    RebuildBitMap();
                }
            };
        }
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        BitMapGrid.Children.Add(border);
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var database = _viewModel.ToDatabase();
        var errors = Validate(database);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(12)), "DBC检查未通过", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new SaveFileDialog { Title = "保存DBC文件", Filter = "CAN数据库 (*.dbc)|*.dbc", FileName = DbcParser.SanitizeName(database.Name) + ".dbc", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await DbcWriter.WriteFileAsync(dialog.FileName, database);
            SavedDatabase = database;
            SavedPath = dialog.FileName;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "DBC保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<string> Validate(DbcDatabase database)
    {
        var errors = new List<string>();
        foreach (var message in database.Messages)
        {
            if (message.Id > 0x1FFFFFFF) errors.Add($"{message.Name}: ID超出29位。 ");
            foreach (var duplicate in message.Signals.GroupBy(signal => signal.Name).Where(group => group.Count() > 1)) errors.Add($"{message.Name}: 信号名{duplicate.Key}重复。 ");
            foreach (var signal in message.Signals)
            {
                foreach (var error in DbcCodec.ValidateSignal(message, signal).Errors) errors.Add($"{message.Name}/{signal.Name}: {error}");
            }
            foreach (var cell in DbcCodec.BuildBitLayout(message).Where(item => item.HasConflict))
            {
                var signals = cell.Signals.Distinct().ToArray();
                var legalMuxOverlap = signals.All(signal => signal.MultiplexerValue.HasValue)
                    && signals.Select(signal => signal.MultiplexerValue).Distinct().Count() == signals.Length;
                if (!legalMuxOverlap) errors.Add($"{message.Name}: bit {cell.GlobalBit} 被 {string.Join(", ", signals.Select(signal => signal.Name))} 重叠占用。");
            }
        }
        foreach (var duplicate in database.Messages.GroupBy(message => message.Key).Where(group => group.Count() > 1)) errors.Add($"报文ID {duplicate.Key}重复。 ");
        return errors;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
