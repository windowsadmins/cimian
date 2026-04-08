// LogWindow.xaml.cs - Log viewer window for Cimian session logs
// Finds the most recent session log, displays entries, and tails if running

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

public partial class LogWindow : Window
{
    private const string LogsBaseDir = @"C:\ProgramData\ManagedInstalls\logs";
    private const int MaxLines = 5000;

    private static LogWindow? _instance;
    private static readonly object _instanceLock = new();

    private FileSystemWatcher? _fileWatcher;
    private FileSystemWatcher? _dirWatcher;
    private string? _currentLogPath;
    private string _fullLogText = string.Empty;
    private string _filterText = string.Empty;
    private bool _autoScroll = true;
    private long _lastFileSize;

    /// <summary>
    /// Gets or activates the singleton log window.
    /// </summary>
    public static LogWindow GetOrActivate()
    {
        lock (_instanceLock)
        {
            if (_instance == null)
            {
                _instance = new LogWindow();
            }
            _instance.Activate();
            // Refresh to latest log whenever brought to front
            _instance.FindAndLoadLatestLog();
            return _instance;
        }
    }

    public LogWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 600));
        CenterOnScreen();

        Closed += OnClosed;
        FindAndLoadLatestLog();
        StartWatching();
    }

    private void CenterOnScreen()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var x = (displayArea.WorkArea.Width - 2250) / 2;
        var y = (displayArea.WorkArea.Height - 1500) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>
    /// Finds the most recent install.log across all session directories
    /// Session structure: logs/YYYY-MM-DD/HHMM/install.log
    /// </summary>
    private string? FindLatestLogFile()
    {
        if (!Directory.Exists(LogsBaseDir)) return null;

        // Get day directories sorted descending
        var dayDirs = Directory.GetDirectories(LogsBaseDir)
            .OrderByDescending(d => Path.GetFileName(d))
            .Take(3); // only check last 3 days

        foreach (var dayDir in dayDirs)
        {
            var sessionDirs = Directory.GetDirectories(dayDir)
                .OrderByDescending(d => Path.GetFileName(d));

            foreach (var sessionDir in sessionDirs)
            {
                var installLog = Path.Combine(sessionDir, "install.log");
                if (File.Exists(installLog))
                    return installLog;

                var runLog = Path.Combine(sessionDir, "run.log");
                if (File.Exists(runLog))
                    return runLog;
            }
        }

        return null;
    }

    private void FindAndLoadLatestLog()
    {
        _currentLogPath = FindLatestLogFile();
        LoadLogFile();
    }

    private void LoadLogFile()
    {
        try
        {
            if (_currentLogPath == null || !File.Exists(_currentLogPath))
            {
                _fullLogText = Directory.Exists(LogsBaseDir)
                    ? "No log sessions found. Run 'Check Now' to start a session."
                    : $"Log directory not found: {LogsBaseDir}";
                StatusText.Text = _currentLogPath == null ? "No logs found" : "Log file not found";
                UpdateDisplay();
                return;
            }

            // Read with sharing so we don't block the writer
            using var stream = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

            // Show session info in status bar
            var sessionDir = Path.GetDirectoryName(_currentLogPath);
            var sessionName = sessionDir != null
                ? $"{Path.GetFileName(Path.GetDirectoryName(sessionDir))}/{Path.GetFileName(sessionDir)}"
                : "";
            StatusText.Text = $"Session: {sessionName}  •  {Path.GetFileName(_currentLogPath)}";
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
            if (_currentLogPath == null || !File.Exists(_currentLogPath)) return;

            using var stream = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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
        // Watch the current log file for changes (tailing)
        WatchCurrentLogFile();

        // Watch the logs base directory for new sessions
        try
        {
            if (!Directory.Exists(LogsBaseDir)) return;

            _dirWatcher = new FileSystemWatcher(LogsBaseDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName,
                Filter = "*.log",
                EnableRaisingEvents = true
            };

            _dirWatcher.Created += (s, e) =>
            {
                var name = Path.GetFileName(e.FullPath);
                if (!name.Equals("install.log", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("run.log", StringComparison.OrdinalIgnoreCase))
                    return;

                // New session started — switch to it
                DispatcherQueue.TryEnqueue(() =>
                {
                    _currentLogPath = e.FullPath;
                    _lastFileSize = 0;
                    LoadLogFile();
                    WatchCurrentLogFile();
                });
            };
        }
        catch
        {
            // Could not watch for new sessions
        }
    }

    private void WatchCurrentLogFile()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        if (_currentLogPath == null) return;

        try
        {
            var dir = Path.GetDirectoryName(_currentLogPath);
            var file = Path.GetFileName(_currentLogPath);

            if (dir == null || !Directory.Exists(dir)) return;

            _fileWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += (s, e) =>
            {
                DispatcherQueue.TryEnqueue(AppendNewContent);
            };
        }
        catch
        {
            // Could not watch log file
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
        FindAndLoadLatestLog();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _fullLogText = string.Empty;
        LogTextBlock.Text = string.Empty;
        LineCountText.Text = "0 lines";
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _dirWatcher?.Dispose();
        _dirWatcher = null;

        lock (_instanceLock)
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
