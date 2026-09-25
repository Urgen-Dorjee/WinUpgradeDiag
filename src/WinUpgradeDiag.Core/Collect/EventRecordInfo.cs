using System;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>A neutral copy of one Windows event log record. No interpretation.</summary>
    public sealed class EventRecordInfo
    {
        public string LogName { get; set; }
        public string ProviderName { get; set; }
        public int EventId { get; set; }
        public DateTime? TimeCreatedUtc { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
    }
}
