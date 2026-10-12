using Microsoft.EntityFrameworkCore;

namespace SignalTracker.Models
{
    /// <summary>
    /// Minimal stand-in for the multi-tenant service's real DbContext. This offline build never
    /// wires up a real database — the report engine's ZIP-based endpoints (the only ones this app
    /// uses) never touch it. It exists purely so the pasted controller compiles and resolves via DI;
    /// the DB-backed endpoints (Generate/GenerateFromQuery) are out of scope for the offline app and
    /// will simply error if invoked against the empty SQLite placeholder database.
    /// </summary>
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<TblProject> tbl_project => Set<TblProject>();
        public DbSet<TblNetworkLog> tbl_network_log => Set<TblNetworkLog>();
        public DbSet<TblSession> tbl_session => Set<TblSession>();
    }
}
