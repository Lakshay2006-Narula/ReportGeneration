namespace SignalTracker.Models
{
    /// <summary>Minimal stand-in entity — only the columns ExcelReportController's DB path reads. See ApplicationDbContext.</summary>
    public class TblSession
    {
        public int? id { get; set; }
        public string? notes { get; set; }
    }
}
