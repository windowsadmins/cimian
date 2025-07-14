using System;

namespace CimianStatus
{
    public class StatusMessage
    {
        public string Type { get; set; } = string.Empty;
        public string? Data { get; set; }
        public int Percent { get; set; }
        public bool Error { get; set; }
    }
}
