namespace SignalTracker.Models
{
    /// <summary>
    /// multipart/form-data request model for ExcelReportController.GenerateFromZip.
    /// Not included in the pasted controller source — reconstructed from every request.* member
    /// the controller actually reads.
    /// </summary>
    public class ZipReportUploadRequest
    {
        public string? JobId { get; set; }
        public IFormFile? LogZip { get; set; }
        public List<IFormFile>? LogZips { get; set; }
        public string? Title { get; set; }
        public string? GeneratedBy { get; set; }
        public string? ProjectName { get; set; }
        public long? SessionIdOverride { get; set; }
        public string? BandFilter { get; set; }
        public List<string>? Bands { get; set; }
        public bool? ShowSampleCount { get; set; }
        public bool? FilterByImageName { get; set; }
        public List<string>? PlotInputs { get; set; }
        public List<string>? PlotHeaders { get; set; }
        public List<string>? Kpis { get; set; }
        public string? EarfcnMode { get; set; }
        public bool? EarfcnWise { get; set; }
    }
}
