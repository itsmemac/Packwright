using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Packwright.App.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class LogView : UserControl
{
    private const int MaximumLines = 5000;
    private readonly StringBuilder _text = new();
    private int _lines;

    public LogView()
    {
        InitializeComponent();
        // Show what was logged before this page existed (startup, earlier sessions), then follow new entries.
        foreach (string line in Logger.ReadTail(1000))
        {
            _text.Append(line).Append('\n');
            _lines++;
        }
        LogText.Text = _text.ToString();
        LogText.CaretIndex = LogText.Text.Length;
        Logger.Logged += entry => Dispatcher.UIThread.Post(() => Append(entry));
        ClearButton.Click += (_, _) => { _text.Clear(); _lines = 0; LogText.Text = string.Empty; };
        OpenLogButton.Click += (_, _) => Dialogs.OpenPath(Logger.LogDirectory);
    }

    private void Append(LogEntry entry)
    {
        _text.Append(Logger.Format(entry)).Append('\n');
        if (++_lines > MaximumLines)
        {
            int cut = _text.ToString().IndexOf('\n') + 1;
            _text.Remove(0, cut);
            _lines--;
        }
        LogText.Text = _text.ToString();
        if (AutoScrollBox.IsChecked == true) LogText.CaretIndex = LogText.Text.Length;
    }
}
