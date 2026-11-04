using System.Globalization;
using System.IO.Compression;

namespace SignalTracker.Controllers;

public partial class ExcelReportController
{
    private static readonly string[] WifiImageHeaders =
        { "BAND", "WIFI_RSSI", "LINK_SPEED", "CHANNEL", "DL_THPT", "UL_THPT" };

    private static string[] GetPlotHeaders(List<WalkTestLogRow> rows) =>
        rows.Any(r => r.IsWifi)
            ? rows.Any(r => !r.IsWifi) ? ImageHeaders.Union(WifiImageHeaders).ToArray() : WifiImageHeaders
            : ImageHeaders;

    private static string[] ResolveReportPlotHeaders(List<WalkTestLogRow> rows, List<string>? selected)
    {
        var available = GetPlotHeaders(rows);
        // An old client selecting every cellular KPI still gets a full automatically detected Wi-Fi report.
        if (selected == null || selected.Count == 0 || new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase).SetEquals(ImageHeaders))
            return available;
        return available.Where(h => selected.Contains(h, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private void RequireReportFeature(string captureType)
    {
        if (_configuration.GetValue("License:Enforce", false))
            _licenseService.RequireReport(captureType);
    }

    private void ValidateCaptureType(List<WalkTestLogRow> rows, string? captureType, string? jobId = null)
    {
        if (rows.Count > 0 && !string.IsNullOrWhiteSpace(captureType) && !captureType.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var wifi = captureType.Equals("wifi", StringComparison.OrdinalIgnoreCase);
            if (!wifi && !captureType.Equals("cellular", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Capture type must be Wi-Fi or Cellular.");
            if (rows.Any(r => r.IsWifi != wifi))
                throw new InvalidDataException(wifi
                    ? "This log file is for cellular data, not Wi-Fi. Select the Cellular option and upload a cellular log, or upload a Wi-Fi log here."
                    : "This log file is for Wi-Fi, not cellular data. Select the Wi-Fi option to use this file, or upload a cellular log here.");
        }

        // Inspect actual rows before request filters: omitted/auto/mislabeled input cannot bypass licensing.
        if (rows.Any(r => r.IsWifi)) RequireReportFeature("wifi");
        if (rows.Any(r => !r.IsWifi)) RequireReportFeature("cellular");
        if (jobId != null && SignalTracker.Services.ReportProgressTracker.Get(jobId) is { } job)
            job.RequiredFeatures = job.RequiredFeatures
                .Union(rows.Select(r => r.IsWifi ? "wifi" : "cellular")).ToArray();
    }

    private static ZipArchiveEntry? FindWifiPlot(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("WiFi_plot.csv", StringComparison.OrdinalIgnoreCase));
        if (entry != null) return entry;
        // Some exporters use the cellular plot filename for either capture type.
        entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("image_plot.csv", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        var headers = ParseCsvLine(reader.ReadLine() ?? "");
        return headers.Any(h => h.Trim().Equals("WiFi RSSI", StringComparison.OrdinalIgnoreCase)) ? entry : null;
    }

    private static bool IsWifiCapture(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("session_params.txt", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is string line)
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals("capture_type", StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim().Equals("WIFI", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string WifiBandName(string? band) =>
        string.IsNullOrWhiteSpace(band) || band == "@" ? "Wi-Fi (Unknown Band)" : band.Trim();

    private static string? ReadWifiCsvRecord(TextReader reader)
    {
        var lines = new List<string>();
        var quoted = false;
        while (reader.ReadLine() is string line)
        {
            lines.Add(line);
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] != '"') continue;
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            if (!quoted) return string.Join("\n", lines);
        }
        if (quoted) throw new InvalidDataException("Wi-Fi plot contains an unterminated quoted CSV field.");
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static List<WalkTestLogRow> ExtractWifiRows(ZipArchive archive, ZipArchiveEntry entry, long sessionId)
    {
        var rows = new List<WalkTestLogRow>();
        var thresholds = ExtractThresholdConfigFromZip(archive);
        using var reader = new StreamReader(entry.Open());
        var headerLine = ReadWifiCsvRecord(reader);
        if (headerLine == null) return rows;
        var headers = ParseCsvLine(headerLine).Select((h, i) => (h: h.Trim().Trim('\uFEFF'), i))
            .GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        while (ReadWifiCsvRecord(reader) is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseCsvLine(line);
            string? Raw(string name) => headers.TryGetValue(name, out var i) && i < values.Count ? values[i].Trim() : null;
            string? Value(string name)
            {
                var raw = Raw(name);
                return string.IsNullOrWhiteSpace(raw) || raw == "@" ? null : raw.Split('@')[0].Trim();
            }
            var rawTime = Raw("timestamp");
            if (!DateTime.TryParse(rawTime, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var time)) continue;
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, metric) in new[] { ("Band", "BAND"), ("WiFi RSSI", "WIFI_RSSI"),
                         ("Link Speed", "LINK_SPEED"), ("Channel", "CHANNEL"), ("DL THPT", "DL_THPT"), ("UL THPT", "UL_THPT") })
            {
                var raw = Raw(column);
                if (raw?.Split('@') is { Length: > 1 } parts && !string.IsNullOrWhiteSpace(parts[1]))
                    colors[metric] = NormalizeColorHex(parts[1]);
            }
            var band = Value("Band");
            rows.Add(new WalkTestLogRow
            {
                Id = rows.Count + 1, SessionId = checked((int)sessionId), Timestamp = time,
                IsWifi = true, Network = "WiFi", IndoorOutdoor = "Indoor", Primary = "Yes",
                X = ParseFloatSafe(Value("x")), Y = ParseFloatSafe(Value("y")),
                Lat = ParseFloatSafe(Value("lat")), Lon = ParseFloatSafe(Value("lon")),
                Band = band, BandSheetName = WifiBandName(band),
                WifiRssi = ParseFloatSafe(Value("WiFi RSSI")), WifiLinkSpeed = ParseFloatSafe(Value("Link Speed")),
                WifiChannel = Value("Channel"), WifiSsid = Value("ssid"), WifiBssid = Value("bssid"),
                Provider = Value("ssid"), DlTpt = Value("DL THPT"), UlTpt = Value("UL THPT"),
                MetricColors = colors.Count == 0 ? null : colors, WifiThresholds = thresholds
            });
        }
        return rows;
    }

    private static List<LegendStatRow> CalculateWifiLegendStatistics(
        List<WalkTestLogRow> rows, string header, ReportThresholdConfig thresholds, bool filterByImageName)
    {
        var target = filterByImageName ? rows.Where(r => RowHasImageName(r)).ToList() : rows;
        if (target.Count == 0) target = rows;
        var ranges = header switch
        {
            "BAND" => thresholds.WifiBand, "WIFI_RSSI" => thresholds.WifiRssi,
            "LINK_SPEED" => thresholds.WifiLinkSpeed, "CHANNEL" => thresholds.WifiChannel,
            _ => thresholds.GetRangesForHeader(header)
        };
        var result = ranges.Select(r => new LegendStatRow { Range = r }).ToList();
        var extras = new Dictionary<(string Value, string Color), LegendStatRow>();
        foreach (var row in target)
        {
            var raw = header switch
            {
                "BAND" => row.Band, "WIFI_RSSI" => row.WifiRssi?.ToString(CultureInfo.InvariantCulture),
                "LINK_SPEED" => row.WifiLinkSpeed?.ToString(CultureInfo.InvariantCulture), "CHANNEL" => row.WifiChannel,
                "DL_THPT" => row.DlTpt, "UL_THPT" => row.UlTpt, _ => null
            };
            var numeric = header == "BAND" ? null : ParseDoubleSafe(raw);
            LegendStatRow? match = null;
            if (header == "BAND")
                match = result.FirstOrDefault(r =>
                    (r.Range.Label?.Equals(raw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Range.ValueMatch?.Equals(raw?.Replace(" GHz", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Range.ValueMatch == "2" && raw?.Equals("2.4 GHz", StringComparison.OrdinalIgnoreCase) == true));
            else if (numeric.HasValue)
                match = result.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Range.ValueMatch)
                    ? ParseDoubleSafe(r.Range.ValueMatch) == numeric : r.Range.Contains(numeric.Value)) ??
                    result.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Range.ValueMatch) && r.Range.ContainsInclusive(numeric.Value));
            if (match != null) { match.Count++; continue; }
            var missing = string.IsNullOrWhiteSpace(raw);
            var color = missing ? "#000000" : GetHexColorFromRows(new List<WalkTestLogRow> { row }, header) ?? "#888888";
            var display = missing ? "No Data" : ranges.Count == 0 ? raw! : $"Out of Range: {raw}";
            var key = (display, color);
            if (!extras.TryGetValue(key, out var extra))
            {
                extra = new LegendStatRow { Range = new ThresholdRange(display, numeric ?? 0, numeric ?? 0, color)
                    { ValueMatch = display, Label = display } };
                extras[key] = extra;
                result.Add(extra);
            }
            extra.Count++;
        }
        foreach (var item in result) item.Percentage = target.Count == 0 ? 0 : item.Count * 100.0 / target.Count;
        return result;
    }
}
