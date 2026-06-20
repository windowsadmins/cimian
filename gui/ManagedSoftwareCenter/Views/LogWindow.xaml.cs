// LogWindow.xaml.cs - Standalone log viewer window
// Thin host around LogViewerControl; opened from the View Log flyout's
// pop-out button or the Ctrl+L accelerator.

using Microsoft.UI.Xaml;

namespace Cimian.GUI.ManagedSoftwareCenter.Views;

public partial class LogWindow : Window
{
    private static LogWindow? _instance;
    private static readonly object _instanceLock = new();

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
            _instance.LogViewer.RefreshLog();
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
        LogViewer.Start();
    }

    /// <summary>
    /// Centers the window on the work area of its display and clamps the
    /// position so it can never open outside the visible area. Uses the
    /// actual window size — centering math must never hardcode dimensions,
    /// which previously pushed the window off the top of smaller screens.
    /// </summary>
    private void CenterOnScreen()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        var work = displayArea.WorkArea;
        var size = AppWindow.Size;

        var x = work.X + Math.Max(0, (work.Width - size.Width) / 2);
        var y = work.Y + Math.Max(0, (work.Height - size.Height) / 2);
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        LogViewer.Stop();

        lock (_instanceLock)
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
