namespace SignalTracker.Models
{
    /// <summary>Minimal stand-in entity — only the columns ExcelReportController's DB path reads. See ApplicationDbContext.</summary>
    public class TblNetworkLog
    {
        public long id { get; set; }
        public int? session_id { get; set; }
        public DateTime? timestamp { get; set; }
        public float? lat { get; set; }
        public float? lon { get; set; }
        public string? indoor_outdoor { get; set; }
        public string? network { get; set; }
        public string? m_alpha_short { get; set; }
        public string? m_alpha_long { get; set; }
        public string? band { get; set; }
        public string? pci { get; set; }
        public float? rsrp { get; set; }
        public float? rsrq { get; set; }
        public float? sinr { get; set; }
        public string? dl_tpt { get; set; }
        public string? ul_tpt { get; set; }
        public float? mos { get; set; }
        public string? apps { get; set; }
        public string? app_name { get; set; }
        public string? nodeb_id { get; set; }
        public string? cell_id { get; set; }
        public string? earfcn { get; set; }
        public string? bler { get; set; }
        public string? volte_call { get; set; }
        public float? cqi { get; set; }
        public string? ta { get; set; }
        public int? level { get; set; }
        public string? primary { get; set; }
        public string? primary_cell_info_1 { get; set; }
        public string? image_path { get; set; }
    }
}
