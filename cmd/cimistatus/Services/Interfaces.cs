using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Cimian.Status.Models;

namespace Cimian.Status.Services
{
    public interface IUpdateService
    {
        event EventHandler<ProgressEventArgs>? ProgressChanged;
        event EventHandler<StatusEventArgs>? StatusChanged;
        event EventHandler<UpdateCompletedEventArgs>? Completed;

        Task ExecuteUpdateAsync();
        bool IsExecutableFound();
        Process? LaunchWithOutputCapture(Action<string> onOutputReceived, Action<string> onErrorReceived);
    }

    public interface ILogService
    {
        string GetLastRunTime();
        void SaveLastRunTime();
        void OpenLogsDirectory();
        string GetLatestLogDirectory();
        
        // Live log tailing functionality
        event EventHandler<string>? LogLineReceived;
        Task StartLogTailingAsync();
        Task StopLogTailingAsync();
        bool IsLogTailing { get; }
        string GetCurrentLogFilePath();
        
        // Manual process start for testing
        Task<bool> StartProcessWithLiveMonitoringAsync();
    }

    public interface IStatusServer
    {
        event EventHandler<StatusMessage>? MessageReceived;
        Task StartAsync();
        Task StopAsync();
        bool IsRunning { get; }
    }
}
