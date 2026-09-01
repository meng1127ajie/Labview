using System.Windows;
using System.Windows.Input;

namespace V3RttMonitor.App;

public partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow() => InitializeComponent();

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
