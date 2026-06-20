// LogViewerControl.xaml.cs - Reusable session log viewer
// Finds the most recent session log, displays entries, and tails while visible.
// Hosted by the View Log flyout (Updates page) and the standalone LogWindow.

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Cimian.GUI.ManagedSoftwareCenter.Controls;

public partial class LogViewerControl : UserControl
{
    private const string LogsBaseDir = @"C:\ProgramData\ManagedInstalls\logs";
    private const int MaxLines = 5000;

    private FileSystemWatcher? _fileWatcher;
    private FileSystemWatcher? _dirWatcher;
    private string? _currentLogPath;
    private string _fullLogText = string.Empty;
    private string _filterText = string.Empty;
    private bool _autoScroll = true;
    private bool _showDebug;
    private long _lastFileSize;

    /// <summary>Raised when the user clicks the pop-out button (flyout mode only).</summary>
    public event EventHandler? PopOutRequested;

    /// <summary>Shows the "open in separate window" toolbar button (flyout mode).</summary>
    public bool ShowPopOutButton
    {
        get => PopOutButton.Visibility == Visibility.Visible;
        set => PopOutButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public LogViewerControl()
    {
        InitializeComponent();
    }

    /// <summary>Loads the latest session log and starts tailing it.</summary>
    public void Start()
    {
        FindAndLoadLatestLog();
        StartWatching();
    }

    /// <summary>Stops tailing and releases the file watchers.</summary>
    public void Stop()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _dirWatcher?.Dispose();
        _dirWatcher = null;
    }

    /// <summary>Reloads the most recent session log (e.g. when re-shown).</summary>
    public void RefreshLog() => FindAndLoadLatestLog();

    /// <summary>
    /// Finds the most recent install.log across all session directories
    /// Session structure: logs/YYYY-MM-DD/HHMM/install.log
    /// </summary>
    private static string? FindLatestLogFile()
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
        // DEBUG lines are hidden by default to keep the view readable; the search
        // filter applies on top. When nothing is being filtered out, show the raw
        // text untouched (cheapest path).
        if (_showDebug && string.IsNullOrEmpty(_filterText))
        {
            LogTextBlock.Text = _fullLogText;
        }
        else
        {
            var lines = _fullLogText.Split('\n')
                .Where(l => _showDebug || !IsDebugLine(l));
            if (!string.IsNullOrEmpty(_filterText))
            {
                lines = lines.Where(l => l.Contains(_filterText, StringComparison.OrdinalIgnoreCase));
            }
            LogTextBlock.Text = string.Join('\n', lines);
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

            _dirWatcher?.Dispose();
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

    private void ShowDebugToggle_Click(object sender, RoutedEventArgs e)
    {
        _showDebug = ShowDebugToggle.IsChecked == true;
        UpdateDisplay();
    }

    /// <summary>
    /// True for a DEBUG-level session log line. Lines are formatted
    /// "[timestamp] LEVEL message"; the level sits right after "] ". Continuation
    /// lines (no timestamp) are not treated as debug so multi-line messages from
    /// higher levels stay visible.
    /// </summary>
    private static bool IsDebugLine(string line)
    {
        var idx = line.IndexOf("] ", StringComparison.Ordinal);
        return idx >= 0
            && line.AsSpan(idx + 2).TrimStart().StartsWith("DEBUG", StringComparison.Ordinal);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        FindAndLoadLatestLog();
    }

    /// <summary>
    /// Copies the currently shown log (respecting the active filter) to the
    /// clipboard so users can paste it into a ticket/report. Restores the status
    /// text after a moment of "Copied" feedback.
    /// </summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = LogTextBlock.Text;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);

            var lineCount = text.Split('\n').Length;
            var previous = StatusText.Text;
            StatusText.Text = $"Copied {lineCount} lines to clipboard";

            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                StatusText.Text = previous;
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _fullLogText = string.Empty;
        LogTextBlock.Text = string.Empty;
        LineCountText.Text = "0 lines";
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        PopOutRequested?.Invoke(this, EventArgs.Empty);
    }
}
