using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    try
    {
        return InteractiveIssueLicense();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.WriteLine();
        Console.Write("Press Enter to close...");
        Console.ReadLine();
        return 1;
    }
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "genkeys" => GenerateKeys(args),
        "issue" => IssueLicense(args),
        _ => Fail($"Unknown command: {args[0]}")
    };
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

static int GenerateKeys(string[] args)
{
    var outputDirectory = GetOption(args, "--out") ?? "license-keys";
    Directory.CreateDirectory(outputDirectory);
    if (File.Exists(Path.Combine(outputDirectory, "private-key.txt")) ||
        File.Exists(Path.Combine(outputDirectory, "public-key.txt")))
        throw new IOException("Signing key files already exist. Choose a new output directory; existing keys will not be overwritten.");

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
    var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    File.WriteAllText(Path.Combine(outputDirectory, "private-key.txt"), privateKey);
    File.WriteAllText(Path.Combine(outputDirectory, "public-key.txt"), publicKey);

    Console.WriteLine($"Private key: {Path.GetFullPath(Path.Combine(outputDirectory, "private-key.txt"))}");
    Console.WriteLine($"Public key:  {Path.GetFullPath(Path.Combine(outputDirectory, "public-key.txt"))}");
    Console.WriteLine();
    Console.WriteLine("Keep private-key.txt secret. Only public-key.txt is embedded in the app.");
    return 0;
}

static int IssueLicense(string[] args)
{
    var machineId = RequireOption(args, "--machine");
    var licensedTo = GetOption(args, "--name") ?? "S-Tracer User";
    var companyCode = RequireOption(args, "--company-code");
    var daysText = RequireOption(args, "--days");
    var privateKeySource = RequireOption(args, "--private-key");
    var outputPath = GetOption(args, "--out");
    var features = ParseFeatures(RequireOption(args, "--features"));

    if (string.IsNullOrWhiteSpace(companyCode))
        return Fail("--company-code is required.");

    if (!int.TryParse(daysText, out var days) || days <= 0)
        return Fail("--days must be a positive number.");

    var licenseKey = IssueLicenseCore(machineId, days, licensedTo, companyCode, privateKeySource, features);

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        File.WriteAllText(outputPath, licenseKey);
        Console.WriteLine(Path.GetFullPath(outputPath));
    }
    else
    {
        Console.WriteLine(licenseKey);
    }

    return 0;
}

static int InteractiveIssueLicense()
{
    Console.Title = "S-Tracer License Generator";
    Console.WriteLine("S-Tracer License Generator");
    Console.WriteLine("==========================");
    Console.WriteLine();

    var machineId = PromptRequired("Machine ID");
    var licensedTo = Prompt("Customer/company name", "S-Tracer User");
    var companyCode = PromptRequired("Company code");
    var days = PromptPositiveInt("License duration in days", 365);
    var features = ParseFeatures(PromptRequired("Report features (cellular, wifi, or cellular,wifi)"));
    var privateKeySource = PromptRequired("Private signing key file (internal use only)");
    var outputPath = Prompt(
        "Output file",
        Path.Combine(AppContext.BaseDirectory, $"license-{SanitizeFileName(machineId)}-{days}d.txt"));

    var licenseKey = IssueLicenseCore(machineId, days, licensedTo, companyCode, privateKeySource, features);
    File.WriteAllText(outputPath, licenseKey);

    Console.WriteLine();
    Console.WriteLine("License generated:");
    Console.WriteLine(Path.GetFullPath(outputPath));
    Console.WriteLine();
    Console.WriteLine(licenseKey);
    Console.WriteLine();
    Console.Write("Press Enter to close...");
    Console.ReadLine();

    return 0;
}

static string IssueLicenseCore(string machineId, int days, string licensedTo, string companyCode, string privateKeySource, string[] features)
{
    if (string.IsNullOrWhiteSpace(privateKeySource) || !File.Exists(privateKeySource))
        throw new ArgumentException("A private signing key file is required. Use --private-key on a trusted internal machine.");
    var privateKey = File.ReadAllText(privateKeySource).Trim();

    var issuedUtc = DateTimeOffset.UtcNow;
    var expiresUtc = issuedUtc.AddDays(days);
    var payloadJson = JsonSerializer.Serialize(new
    {
        mid = machineId.Trim(), name = licensedTo.Trim(), cc = companyCode.Trim(),
        iat = issuedUtc.ToString("O"), exp = expiresUtc.ToString("O"), features
    });
    var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
    var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);
    return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
}

static string[] ParseFeatures(string input)
{
    var features = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(f => f.ToLowerInvariant()).Distinct().ToArray();
    if (features.Length == 0 || features.Any(f => f is not "cellular" and not "wifi"))
        throw new ArgumentException("--features must be cellular, wifi, or cellular,wifi.");
    return features;
}

static string? GetOption(string[] args, string name)
{
    var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string RequireOption(string[] args, string name)
{
    return GetOption(args, name) ?? throw new ArgumentException($"{name} is required.");
}

static string PromptRequired(string label)
{
    while (true)
    {
        Console.Write($"{label}: ");
        var value = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        Console.WriteLine($"{label} is required.");
    }
}

static string Prompt(string label, string defaultValue)
{
    Console.Write($"{label} [{defaultValue}]: ");
    var value = Console.ReadLine()?.Trim();
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
}

static int PromptPositiveInt(string label, int defaultValue)
{
    while (true)
    {
        var value = Prompt(label, defaultValue.ToString());
        if (int.TryParse(value, out var parsed) && parsed > 0)
            return parsed;

        Console.WriteLine($"{label} must be a positive number.");
    }
}

static string SanitizeFileName(string value)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var cleaned = new string(value.Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray());
    return string.IsNullOrWhiteSpace(cleaned) ? "machine" : cleaned.Trim();
}

static string Base64UrlEncode(byte[] bytes)
{
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  STracerLicenseGenerator.exe");
    Console.WriteLine("  dotnet run --project tools/SignalTracker.LicenseTool -- genkeys --out license-keys");
    Console.WriteLine("  dotnet run --project tools/SignalTracker.LicenseTool -- issue --machine XXXX-XXXX-XXXX-XXXX --company-code ABC --days 365 --features cellular,wifi --private-key PATH-TO-PRIVATE-KEY --name \"Customer\"");
}
