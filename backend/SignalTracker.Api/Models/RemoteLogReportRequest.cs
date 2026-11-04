namespace SignalTracker.Models
{
    public sealed class RemoteLogReportRequest
    {
        public string? CaptureType { get; set; }
        public string? JobId { get; set; }
        public long? CompanyId { get; set; }
        public long? UserId { get; set; }
        public List<long>? LogIds { get; set; }
        public string? Title { get; set; }
        public string? ProjectName { get; set; }
        public string? ReportMode { get; set; }
        public string? BandFilter { get; set; }
        public List<string>? Bands { get; set; }
        public List<string>? PlotInputs { get; set; }
        public bool? FilterByImageName { get; set; }
        public bool? FilteredByImageName { get; set; }
        public bool? ShowSampleCount { get; set; }
        public string? EarfcnMode { get; set; }
        public bool? EarfcnWise { get; set; }
        public List<string>? SheetNames { get; set; }
        public List<int>? LogFileGroups { get; set; }
    }
}
