using System;
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
    }

    public interface ILogService
    {
        string GetLastRunTime();
        void SaveLastRunTime();
        void OpenLogsDirectory();
        string GetLatestLogDirectory();
    }

    public interface IStatusServer
    {
        event EventHandler<StatusMessage>? MessageReceived;
        Task StartAsync();
        Task StopAsync();
        bool IsRunning { get; }
    }
}
