using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public sealed class RemoteSessionsController : ControllerBase
    {
        private const int MaximumPageSize = 200;

        private readonly IConfiguration _configuration;
        private readonly ILogger<RemoteSessionsController> _logger;

        public RemoteSessionsController(
            IConfiguration configuration,
            ILogger<RemoteSessionsController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // GET /api/RemoteSessions?companyId=10
        // GET /api/RemoteSessions?userId=25
        // GET /api/RemoteSessions?companyId=10&userId=25
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] long? companyId,
            [FromQuery] long? userId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100,
            CancellationToken cancellationToken = default)
        {
            if (!companyId.HasValue && !userId.HasValue)
            {
                return BadRequest(new
                {
                    message = "At least one of companyId or userId is required."
                });
            }

            if (companyId <= 0 || userId <= 0)
                return BadRequest(new { message = "companyId and userId must be positive numbers." });

            if (page < 1)
                return BadRequest(new { message = "page must be at least 1." });

            if (pageSize < 1 || pageSize > MaximumPageSize)
            {
                return BadRequest(new
                {
                    message = $"pageSize must be between 1 and {MaximumPageSize}."
                });
            }

            var connectionString = _configuration.GetConnectionString("MySqlConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("ConnectionStrings:MySqlConnection is not configured.");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "The remote session database is not configured."
                });
            }

            var urlTemplate = _configuration["RemoteLogZipUrlTemplate"];
            if (string.IsNullOrWhiteSpace(urlTemplate) ||
                !urlTemplate.Contains("{logId}", StringComparison.Ordinal))
            {
                _logger.LogError("RemoteLogZipUrlTemplate is missing or does not contain {{logId}}.");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "The remote log ZIP URL is not configured correctly."
                });
            }

            var filters = new List<string>();
            if (companyId.HasValue) filters.Add("`company_id` = @companyId");
            if (userId.HasValue) filters.Add("`user_id` = @userId");

            var offset = (long)(page - 1) * pageSize;
            var whereClause = string.Join(" AND ", filters);
            var sql = $"""
                SELECT `id`, `company_id`, `user_id`
                FROM `tbl_session`
                WHERE {whereClause}
                ORDER BY `id` DESC
                LIMIT @pageSize OFFSET @offset;
                """;

            try
            {
                var sessions = new List<RemoteSessionResponse>();

                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = new MySqlCommand(sql, connection);
                if (companyId.HasValue) command.Parameters.AddWithValue("@companyId", companyId.Value);
                if (userId.HasValue) command.Parameters.AddWithValue("@userId", userId.Value);
                command.Parameters.AddWithValue("@pageSize", pageSize);
                command.Parameters.AddWithValue("@offset", offset);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var companyIdOrdinal = reader.GetOrdinal("company_id");
                var userIdOrdinal = reader.GetOrdinal("user_id");
                while (await reader.ReadAsync(cancellationToken))
                {
                    var logId = reader.GetInt64("id");
                    sessions.Add(new RemoteSessionResponse
                    {
                        LogId = logId,
                        CompanyId = reader.IsDBNull(companyIdOrdinal) ? null : reader.GetInt64(companyIdOrdinal),
                        UserId = reader.IsDBNull(userIdOrdinal) ? null : reader.GetInt64(userIdOrdinal),
                        FileName = $"log_{logId}.zip",
                        ZipUrl = urlTemplate.Replace(
                            "{logId}",
                            logId.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal)
                    });
                }

                return Ok(new
                {
                    companyId,
                    userId,
                    page,
                    pageSize,
                    count = sessions.Count,
                    sessions
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
            }
            catch (MySqlException ex)
            {
                _logger.LogError(ex, "Failed to fetch remote sessions from tbl_session.");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to fetch sessions from the remote database."
                });
            }
        }

        private sealed class RemoteSessionResponse
        {
            public long LogId { get; init; }
            public long? CompanyId { get; init; }
            public long? UserId { get; init; }
            public required string FileName { get; init; }
            public required string ZipUrl { get; init; }
        }
    }
}
