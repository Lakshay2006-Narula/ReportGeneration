namespace SignalTracker.Models
{
    /// <summary>
    /// multipart/form-data request model for ExcelReportController.DiscoverBands.
    /// Not included in the pasted controller source — reconstructed from every request.* member
    /// the controller actually reads.
    /// </summary>
    public class ZipBandDiscoveryRequest
    {
        public IFormFile? LogZip { get; set; }
        public List<IFormFile>? LogZips { get; set; }
        public long? SessionIdOverride { get; set; }
        public string? EarfcnMode { get; set; }
        public bool? EarfcnWise { get; set; }
    }
}
