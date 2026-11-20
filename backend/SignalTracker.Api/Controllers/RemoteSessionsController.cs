using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SignalTracker.Licensing;

namespace SignalTracker.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public sealed class RemoteSessionsController : ControllerBase
    {
        private const string WifiDataSql = """
                    (LOWER(REPLACE(TRIM(COALESCE(s.`type`, '')), '-', '')) = 'wifi'
                     OR EXISTS (
                         SELECT 1 FROM `tbl_network_log` n
                         WHERE n.`session_id` = s.`id`
                           AND UPPER(REPLACE(REPLACE(COALESCE(n.`network`, ''), '-', ''), ' ', '')) = 'WIFI'
                     ))
                    """;

        private const int MaximumPageSize = 200;
        private const long MaximumZipBytes = 500L * 1024 * 1024;

        private readonly IConfiguration _configuration;
        private readonly LicenseService _licenseService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<RemoteSessionsController> _logger;

        public RemoteSessionsController(
            IConfiguration configuration,
            LicenseService licenseService,
            IHttpClientFactory httpClientFactory,
            ILogger<RemoteSessionsController> logger)
        {
            _configuration = configuration;
            _licenseService = licenseService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // GET /api/RemoteSessions
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Get(
            [FromQuery] long? companyId,
            [FromQuery] long? userId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100,
            [FromQuery] string? captureType = null,
            CancellationToken cancellationToken = default)
        {
            if ((companyId.HasValue && companyId <= 0) || (userId.HasValue && userId <= 0))
                return BadRequest(new { message = "companyId and userId must be positive when provided." });

            if (page < 1)
                return BadRequest(new { message = "page must be at least 1." });

            if (pageSize < 1 || pageSize > MaximumPageSize)
            {
                return BadRequest(new
                {
                    message = $"pageSize must be between 1 and {MaximumPageSize}."
                });
            }

            var selectedCapture = (captureType ?? "all").Trim().ToLowerInvariant();
            if (selectedCapture != "all" && selectedCapture != "wifi" && selectedCapture != "cellular")
                return BadRequest(new { message = "captureType must be wifi, cellular, or all." });

            if (_configuration.GetValue("License:Enforce", false))
            {
                if (selectedCapture == "all")
                {
                    var wifi = _licenseService.AllowsReport("wifi");
                    var cellular = _licenseService.AllowsReport("cellular");
                    if (!wifi && !cellular) _licenseService.RequireReport("cellular");
                    if (!wifi || !cellular) selectedCapture = wifi ? "wifi" : "cellular";
                }
                else _licenseService.RequireReport(selectedCapture);
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
                return StatusCode(500, new { message = "The remote log ZIP URL is not configured correctly." });
            }

            try
            {
                var licensedCompanyId = await ResolveLicensedCompanyIdAsync(connectionString, cancellationToken);
                if (!licensedCompanyId.HasValue)
                {
                    return BadRequest(new
                    {
                        message = "No company could be resolved from the active license."
                    });
                }
                if (companyId.HasValue && companyId.Value != licensedCompanyId.Value)
                    return BadRequest(new { message = "companyId does not match the active license." });

                var sessions = new List<RemoteSessionResponse>();

                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                var availableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var columnsCommand = new MySqlCommand(
                    "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'tbl_session';",
                    connection))
                await using (var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken))
                {
                    while (await columnsReader.ReadAsync(cancellationToken))
                        availableColumns.Add(columnsReader.GetString(0));
                }

                var networkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var networkColumnsCommand = new MySqlCommand(
                    "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'tbl_network_log';",
                    connection))
                await using (var networkColumnsReader = await networkColumnsCommand.ExecuteReaderAsync(cancellationToken))
                {
                    while (await networkColumnsReader.ReadAsync(cancellationToken))
                        networkColumns.Add(networkColumnsReader.GetString(0));
                }

                static string OptionalColumn(HashSet<string> columns, string alias, params string[] candidates)
                {
                    var column = candidates.FirstOrDefault(columns.Contains);
                    return column is null ? $"NULL AS `{alias}`" : $"s.`{column}` AS `{alias}`";
                }

                var dateSql = OptionalColumn(availableColumns, "session_date", "start_date", "session_date", "session_start_date", "startdate", "date");
                var timeSql = OptionalColumn(availableColumns, "session_time", "start_time", "session_time", "session_start_time", "starttime", "time");
                var endTimeSql = OptionalColumn(availableColumns, "end_time", "session_end_time", "session_end", "end_datetime", "ended_at", "endtime");
                var notesSql = OptionalColumn(availableColumns, "notes", "notes");
                var networkNotesColumn = new[] { "notes", "session_notes", "remark", "remarks" }.FirstOrDefault(networkColumns.Contains);
                var networkNotesSql = networkNotesColumn is null ? "NULL AS `network_notes`" : $"MAX(NULLIF(TRIM(n.`{networkNotesColumn}`), '')) AS `network_notes`";
                var dateExpression = dateSql.StartsWith("NULL", StringComparison.Ordinal)
                    ? "DATE(nl.`first_timestamp`) AS `session_date`"
                    : $"COALESCE({dateSql[..dateSql.LastIndexOf(" AS ", StringComparison.Ordinal)]}, DATE(nl.`first_timestamp`)) AS `session_date`";
                var timeExpression = timeSql.StartsWith("NULL", StringComparison.Ordinal)
                    ? "TIME(nl.`first_timestamp`) AS `session_time`"
                    : $"COALESCE({timeSql[..timeSql.LastIndexOf(" AS ", StringComparison.Ordinal)]}, TIME(nl.`first_timestamp`)) AS `session_time`";
                var endTimeExpression = endTimeSql.StartsWith("NULL", StringComparison.Ordinal)
                    ? "TIME(nl.`last_timestamp`) AS `end_time`"
                    : $"COALESCE({endTimeSql[..endTimeSql.LastIndexOf(" AS ", StringComparison.Ordinal)]}, TIME(nl.`last_timestamp`)) AS `end_time`";
                var notesExpression = notesSql.StartsWith("NULL", StringComparison.Ordinal)
                    ? "nl.`network_notes` AS `notes`"
                    : $"COALESCE(NULLIF(TRIM({notesSql[..notesSql.LastIndexOf(" AS ", StringComparison.Ordinal)]}), ''), nl.`network_notes`) AS `notes`";

                var userFilter = userId.HasValue ? " AND s.`user_id` = @userId" : "";
                // Legacy Wi-Fi ZIP sessions can be marked as wifi before their plot rows are imported.
                var captureFilter = selectedCapture == "wifi" ? $" AND {WifiDataSql}"
                    : selectedCapture == "cellular" ? $" AND NOT {WifiDataSql}" : "";
                // The live schema stores the image-plot flag on tbl_session.extra_json.
                // Filter before pagination so only qualifying sessions are listed.
                var sql = $"""
                    SELECT s.`id`, s.`company_id`, s.`user_id`, {WifiDataSql} AS has_wifi_data,
                           {dateExpression}, {timeExpression}, {endTimeExpression}, {notesExpression}
                    FROM `tbl_session` s
                    LEFT JOIN (
                        SELECT n.`session_id`, MIN(n.`timestamp`) AS `first_timestamp`,
                               MAX(n.`timestamp`) AS `last_timestamp`, {networkNotesSql}
                        FROM `tbl_network_log` n
                        GROUP BY n.`session_id`
                    ) nl ON nl.`session_id` = s.`id`
                    WHERE s.`company_id` = @companyId{userFilter}
                      AND CASE
                          WHEN JSON_VALID(s.`extra_json`) THEN
                              JSON_UNQUOTE(JSON_EXTRACT(s.`extra_json`, '$.imageplot'))
                          ELSE NULL
                      END = '1'{captureFilter}
                    ORDER BY s.`id` DESC
                    LIMIT @pageSize OFFSET @offset;
                    """;

                await using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@companyId", licensedCompanyId.Value);
                if (userId.HasValue) command.Parameters.AddWithValue("@userId", userId.Value);
                command.Parameters.AddWithValue("@pageSize", pageSize);
                command.Parameters.AddWithValue("@offset", (long)(page - 1) * pageSize);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var companyIdOrdinal = reader.GetOrdinal("company_id");
                var userIdOrdinal = reader.GetOrdinal("user_id");
                static string? ReadOptional(MySqlDataReader dataReader, string name)
                {
                    var ordinal = dataReader.GetOrdinal(name);
                    return dataReader.IsDBNull(ordinal) ? null : Convert.ToString(dataReader.GetValue(ordinal), CultureInfo.InvariantCulture);
                }
                while (await reader.ReadAsync(cancellationToken))
                {
                    var logId = reader.GetInt64("id");
                    sessions.Add(new RemoteSessionResponse
                    {
                        LogId = logId,
                        CaptureType = reader.GetBoolean("has_wifi_data") ? "wifi" : "cellular",
                        CompanyId = reader.IsDBNull(companyIdOrdinal) ? null : reader.GetInt64(companyIdOrdinal),
                        UserId = reader.IsDBNull(userIdOrdinal) ? null : reader.GetInt64(userIdOrdinal),
                        SessionDate = ReadOptional(reader, "session_date"),
                        SessionTime = ReadOptional(reader, "session_time"),
                        EndTime = ReadOptional(reader, "end_time"),
                        Notes = ReadOptional(reader, "notes"),
                        FileName = $"log_{logId}.zip",
                        ZipUrl = urlTemplate.Replace(
                            "{logId}",
                            logId.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal)
                    });
                }

                return Ok(new
                {
                    captureType = selectedCapture,
                    companyId = licensedCompanyId,
                    companyCode = _licenseService.GetStatus().CompanyCode,
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
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Could not resolve licensed company for remote sessions.");
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("{logId:long}/zip")]
        public async Task<IActionResult> DownloadZip(long logId, CancellationToken cancellationToken)
        {
            if (logId <= 0)
                return BadRequest(new { message = "logId must be positive." });

            var connectionString = _configuration.GetConnectionString("MySqlConnection");
            var urlTemplate = _configuration["RemoteLogZipUrlTemplate"];
            if (string.IsNullOrWhiteSpace(connectionString) ||
                string.IsNullOrWhiteSpace(urlTemplate) ||
                !urlTemplate.Contains("{logId}", StringComparison.Ordinal))
            {
                return StatusCode(500, new { message = "Remote ZIP download is not configured." });
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"signaltracker-download-{logId}-{Guid.NewGuid():N}.zip");
            var handedOff = false;
            try
            {
                var companyId = await ResolveLicensedCompanyIdAsync(connectionString, cancellationToken);
                if (!companyId.HasValue)
                    return BadRequest(new { message = "No company could be resolved from the active license." });

                await using (var connection = new MySqlConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await using var command = new MySqlCommand(
                        $"SELECT {WifiDataSql} FROM `tbl_session` s WHERE s.`id` = @logId AND s.`company_id` = @companyId LIMIT 1;",
                        connection);
                    command.Parameters.AddWithValue("@logId", logId);
                    command.Parameters.AddWithValue("@companyId", companyId.Value);
                    var isWifi = await command.ExecuteScalarAsync(cancellationToken);
                    if (isWifi == null)
                        return NotFound(new { message = "This log was not found for the licensed company." });
                    if (_configuration.GetValue("License:Enforce", false))
                        _licenseService.RequireReport(Convert.ToBoolean(isWifi) ? "wifi" : "cellular");
                }

                var url = urlTemplate.Replace("{logId}", logId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    return StatusCode(500, new { message = "Remote ZIP URL is invalid." });
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                using var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return StatusCode(502, new { message = $"Remote ZIP download failed (HTTP {(int)response.StatusCode})." });

                if (response.Content.Headers.ContentLength > MaximumZipBytes)
                    return StatusCode(413, new { message = "Remote ZIP exceeds the 500 MB limit." });

                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    long totalBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await input.ReadAsync(buffer, timeout.Token)) != 0)
                    {
                        totalBytes += bytesRead;
                        if (totalBytes > MaximumZipBytes)
                            return StatusCode(413, new { message = "Remote ZIP exceeds the 500 MB limit." });
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), timeout.Token);
                    }

                    if (totalBytes == 0)
                        return StatusCode(502, new { message = "Remote ZIP is empty." });
                }

                using (var archive = ZipFile.OpenRead(tempPath))
                {
                    if (archive.Entries.Count == 0)
                        return StatusCode(502, new { message = "Remote ZIP contains no files." });
                }

                var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.DeleteOnClose);
                handedOff = true;
                return File(stream, "application/zip", $"log_{logId}.zip");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is MySqlException or HttpRequestException or IOException or OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to download remote ZIP for log {LogId}.", logId);
                return StatusCode(502, new { message = $"Could not download log {logId}." });
            }
            finally
            {
                if (!handedOff)
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }

        private sealed class RemoteSessionResponse
        {
            public long LogId { get; init; }
            public required string CaptureType { get; init; }
            public long? CompanyId { get; init; }
            public long? UserId { get; init; }
            public string? SessionDate { get; init; }
            public string? SessionTime { get; init; }
            public string? EndTime { get; init; }
            public string? Notes { get; init; }
            public required string FileName { get; init; }
            public required string ZipUrl { get; init; }
        }

        private async Task<long?> ResolveLicensedCompanyIdAsync(string connectionString, CancellationToken cancellationToken)
        {
            var status = _licenseService.GetStatus();
            if (status.CompanyId.HasValue && status.CompanyId.Value > 0)
                return status.CompanyId.Value;

            var companyCode = status.CompanyCode?.Trim();
            if (string.IsNullOrWhiteSpace(companyCode))
                return null;

            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var columnsCommand = new MySqlCommand(
                """
                SELECT COLUMN_NAME
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'tbl_company';
                """,
                connection);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    columns.Add(reader.GetString("COLUMN_NAME"));
            }

            var idColumn = new[] { "id", "company_id" }.FirstOrDefault(columns.Contains);
            var codeColumn = new[]
            {
                "company_code",
                "code",
                "companyCode",
                "companycode",
                "company_unique_code",
                "unique_code"
            }.FirstOrDefault(columns.Contains);

            if (idColumn == null || codeColumn == null)
            {
                _logger.LogError("tbl_company must contain an id/company_id column and a company code column.");
                throw new InvalidOperationException("Company table is not configured for license company-code lookup.");
            }

            await using var lookupCommand = new MySqlCommand(
                $"SELECT `{idColumn}` FROM `tbl_company` WHERE `{codeColumn}` = @companyCode LIMIT 1;",
                connection);
            lookupCommand.Parameters.AddWithValue("@companyCode", companyCode);

            var result = await lookupCommand.ExecuteScalarAsync(cancellationToken);
            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("Licensed company code was not found in tbl_company.");

            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }
}
