using Microsoft.AspNetCore.Mvc;
using SignalTracker.Licensing;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LicenseController : ControllerBase
    {
        private readonly LicenseService _licenseService;

        public LicenseController(LicenseService licenseService)
        {
            _licenseService = licenseService;
        }

        public class ActivateRequest
        {
            public string? LicenseKey { get; set; }
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            var status = _licenseService.GetStatus();
            return Ok(new
            {
                activated = status.Activated,
                machineId = status.MachineId,
                licensedTo = status.LicensedTo,
                expiresUtc = status.ExpiresUtc,
                error = status.Activated ? null : status.Error
            });
        }

        [HttpPost("activate")]
        public IActionResult Activate([FromBody] ActivateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.LicenseKey))
                return BadRequest(new { message = "License key is required." });

            var status = _licenseService.Activate(request.LicenseKey);
            if (!status.Activated)
            {
                return BadRequest(new
                {
                    message = status.Error ?? "License key is invalid.",
                    machineId = status.MachineId
                });
            }

            return Ok(new
            {
                activated = status.Activated,
                machineId = status.MachineId,
                licensedTo = status.LicensedTo,
                expiresUtc = status.ExpiresUtc
            });
        }
    }
}
