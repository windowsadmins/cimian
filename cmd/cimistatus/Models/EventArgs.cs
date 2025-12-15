using System;

namespace Cimian.Status.Models
{
    public class ProgressEventArgs : EventArgs
    {
        public int Percentage { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class StatusEventArgs : EventArgs
    {
        public string Message { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }

    public class UpdateCompletedEventArgs : EventArgs
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int ExitCode { get; set; }
    }

    public class StatusMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public int Percent { get; set; }
        public bool Error { get; set; }
    }
}
