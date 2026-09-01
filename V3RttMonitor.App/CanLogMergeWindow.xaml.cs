using System.Globalization;
using System.Windows;
using V3RttMonitor.Core.CanBus;

namespace V3RttMonitor.App;

public partial class CanLogMergeWindow : Window
{
    public CanLogMergeWindow(int existingFrames, int selectedFiles)
    {
        InitializeComponent();
        SummaryText.Text = $"当前 {existingFrames:N0} 帧，本次选择 {selectedFiles} 个日志文件。";
        if (existingFrames == 0 && selectedFiles == 1)
        {
            ReplaceRadio.IsChecked = true;
        }
    }

    public CanLogMergeMode MergeMode { get; private set; } = CanLogMergeMode.AppendContinuous;
    public double GapSeconds { get; private set; } = 1;

    private void Mode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (GapTextBox is not null) GapTextBox.IsEnabled = AppendGapRadio?.IsChecked == true;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        MergeMode = ReplaceRadio.IsChecked == true
            ? CanLogMergeMode.Replace
            : PreserveRadio.IsChecked == true
                ? CanLogMergeMode.PreserveOriginalTime
                : AppendGapRadio.IsChecked == true
                    ? CanLogMergeMode.AppendWithGap
                    : CanLogMergeMode.AppendContinuous;
        if (MergeMode == CanLogMergeMode.AppendWithGap
            && (!double.TryParse(GapTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gap) || gap < 0))
        {
            MessageBox.Show(this, "段间隔必须是大于等于0的秒数。", "时间间隔", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MergeMode == CanLogMergeMode.AppendWithGap) GapSeconds = double.Parse(GapTextBox.Text, CultureInfo.InvariantCulture);
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
