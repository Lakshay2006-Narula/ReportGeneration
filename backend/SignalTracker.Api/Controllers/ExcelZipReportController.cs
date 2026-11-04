using Microsoft.AspNetCore.Mvc;
using SignalTracker.Models;
using SignalTracker.Licensing;

namespace SignalTracker.Controllers
{
    // Generates the exact same Excel report as ExcelReportController,
    // but reads its data from an uploaded log zip instead of the database.
    [Route("api/[controller]")]
    [ApiController]
    public class ExcelZipReportController : ExcelReportController
    {
        public ExcelZipReportController(
            ApplicationDbContext db,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            LicenseService licenseService)
            : base(db, httpClientFactory, configuration, licenseService)
        {
        }
    }
}
