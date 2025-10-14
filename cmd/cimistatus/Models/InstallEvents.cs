using System;

namespace Cimian.Status.Models
{
    /// <summary>
    /// Event raised when installation progress is detected from events.jsonl
    /// </summary>
    public class InstallProgressEvent : EventArgs
    {
        public string PackageName { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public int Progress { get; set; }
        public string Message { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Event raised when installation status changes (started, completed, failed)
    /// </summary>
    public class InstallStatusEvent : EventArgs
    {
        public string PackageName { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Event raised when a new installation session starts
    /// </summary>
    public class SessionStartEvent : EventArgs
    {
        public string SessionId { get; set; } = string.Empty;
        public string SessionPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Event raised when an installation session ends
    /// </summary>
    public class SessionEndEvent : EventArgs
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
