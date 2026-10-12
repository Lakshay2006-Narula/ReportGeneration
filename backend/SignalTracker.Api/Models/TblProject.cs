namespace SignalTracker.Models
{
    /// <summary>Minimal stand-in entity — only the columns ExcelReportController's DB path reads. See ApplicationDbContext.</summary>
    public class TblProject
    {
        public int id { get; set; }
        public string? project_name { get; set; }
        public string? ref_session_id { get; set; }
    }
}
