// LogWindow.xaml.cs - Log viewer window for managedsoftwareupdate log
// Tails the log file with auto-scroll, search/filter, and monospace font

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

public partial class LogWindow : Window
{
    private const string LogFilePath = @"C:\ProgramData\ManagedInstalls\Logs\ManagedSoftwareUpdate.log";
    private const int MaxLines = 5000;

    private FileSystemWatcher? _watcher;
    private string _fullLogText = string.Empty;
    private string _filterText = string.Empty;
    private bool _autoScroll = true;
    private long _lastFileSize;

    public LogWindow()
    {
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 600));
        CenterOnScreen();

        Closed += OnClosed;
        LoadLogFile();
        StartWatching();
    }

    private void CenterOnScreen()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var x = (displayArea.WorkArea.Width - 900) / 2;
        var y = (displayArea.WorkArea.Height - 600) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void LoadLogFile()
    {
        try
        {
            if (!File.Exists(LogFilePath))
            {
                _fullLogText = $"Log file not found: {LogFilePath}";
                StatusText.Text = "Log file not found";
                UpdateDisplay();
                return;
            }

            // Read with sharing so we don't block the writer
            using var stream = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            _lastFileSize = stream.Length;

            // Trim to max lines
            var lines = content.Split('\n');
            if (lines.Length > MaxLines)
            {
                _fullLogText = string.Join('\n', lines[^MaxLines..]);
            }
            else
            {
                _fullLogText = content;
            }

            var lineCount = _fullLogText.Split('\n').Length;
            LineCountText.Text = $"{lineCount} lines";
            StatusText.Text = $"Loaded: {LogFilePath}";
            UpdateDisplay();
        }
        catch (Exception ex)
        {
            _fullLogText = $"Error reading log: {ex.Message}";
            StatusText.Text = "Error reading log file";
            UpdateDisplay();
        }
    }

    private void AppendNewContent()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return;

            using var stream = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // If file was truncated/rotated, reload entirely
            if (stream.Length < _lastFileSize)
            {
                LoadLogFile();
                return;
            }

            if (stream.Length == _lastFileSize) return;

            // Read only new content
            stream.Seek(_lastFileSize, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var newContent = reader.ReadToEnd();
            _lastFileSize = stream.Length;

            if (string.IsNullOrEmpty(newContent)) return;

            _fullLogText += newContent;

            // Trim if too long
            var lines = _fullLogText.Split('\n');
            if (lines.Length > MaxLines)
            {
                _fullLogText = string.Join('\n', lines[^MaxLines..]);
            }

            var lineCount = _fullLogText.Split('\n').Length;
            LineCountText.Text = $"{lineCount} lines";
            UpdateDisplay();
        }
        catch
        {
            // File might be temporarily locked during write
        }
    }

    private void UpdateDisplay()
    {
        if (string.IsNullOrEmpty(_filterText))
        {
            LogTextBlock.Text = _fullLogText;
        }
        else
        {
            var lines = _fullLogText.Split('\n');
            var filtered = lines.Where(l => l.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
            LogTextBlock.Text = string.Join('\n', filtered);
        }

        if (_autoScroll)
        {
            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        }
    }

    private void StartWatching()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            var file = Path.GetFileName(LogFilePath);

            if (dir == null || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(AppendNewContent);
            };

            StatusText.Text = "Watching for changes...";
        }
        catch
        {
            StatusText.Text = "Could not watch log file";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = SearchBox.Text;
        UpdateDisplay();
    }

    private void AutoScrollToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = AutoScrollToggle.IsChecked == true;
        if (_autoScroll)
        {
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLogFile();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _fullLogText = string.Empty;
        LogTextBlock.Text = string.Empty;
        LineCountText.Text = "0 lines";
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
