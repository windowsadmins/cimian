// UiDispatcher.cs - Marshals service events onto the WinUI dispatcher thread.
// Background sources (FileSystemWatcher, named-pipe/TCP read loops, flag-file
// poll loops) raise events that viewmodels consume by setting ObservableProperty
// members. PropertyChanged into an active XAML binding from a non-UI thread is
// a fatal COMException (0x8001010E RPC_E_WRONG_THREAD), and inside an async void
// handler it kills the process — so every cross-thread event source must raise
// through Post().

using Microsoft.UI.Dispatching;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Static dispatcher used by services to raise events on the UI thread.
/// Initialized once from the App constructor. When no queue has been set
/// (unit tests), Post() invokes the action inline.
/// </summary>
public static class UiDispatcher
{
    private static DispatcherQueue? s_queue;

    /// <summary>Captures the UI thread's DispatcherQueue. Call once at startup.</summary>
    public static void Initialize(DispatcherQueue queue) => s_queue = queue;

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Executes inline when
    /// already on the UI thread or when no queue is set (tests). Returns false
    /// only if the dispatcher rejected the enqueue (app shutting down).
    /// </summary>
    public static bool Post(Action action)
    {
        var queue = s_queue;
        if (queue == null || queue.HasThreadAccess)
        {
            action();
            return true;
        }
        return queue.TryEnqueue(() => action());
    }
}
