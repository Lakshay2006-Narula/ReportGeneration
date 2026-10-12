using System.Security.Cryptography;
using System.Reflection;
using System.Text;

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
    var daysText = RequireOption(args, "--days");
    var privateKeySource = GetOption(args, "--private-key") ?? "";
    var outputPath = GetOption(args, "--out");

    if (!int.TryParse(daysText, out var days) || days <= 0)
        return Fail("--days must be a positive number.");

    var licenseKey = IssueLicenseCore(machineId, days, licensedTo, privateKeySource);

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

    var defaultPrivateKeyPath = FindDefaultPrivateKeyPath();
    var machineId = PromptRequired("Machine ID");
    var licensedTo = Prompt("Customer/company name", "S-Tracer User");
    var days = PromptPositiveInt("License duration in days", 365);
    var privateKeySource = HasEmbeddedPrivateKey()
        ? ""
        : Prompt("Private key path", defaultPrivateKeyPath ?? Path.Combine(AppContext.BaseDirectory, "private-key.txt"));
    var outputPath = Prompt(
        "Output file",
        Path.Combine(AppContext.BaseDirectory, $"license-{SanitizeFileName(machineId)}-{days}d.txt"));

    var licenseKey = IssueLicenseCore(machineId, days, licensedTo, privateKeySource);
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

static string IssueLicenseCore(string machineId, int days, string licensedTo, string privateKeySource)
{
    var privateKey = string.IsNullOrWhiteSpace(privateKeySource)
        ? ReadEmbeddedPrivateKey()
        : File.Exists(privateKeySource)
        ? File.ReadAllText(privateKeySource).Trim()
        : privateKeySource.Trim();

    var issuedUtc = DateTimeOffset.UtcNow;
    var expiresUtc = issuedUtc.AddDays(days);
    var payloadJson =
        $"{{\"mid\":\"{EscapeJson(machineId.Trim())}\",\"name\":\"{EscapeJson(licensedTo.Trim())}\",\"iat\":\"{issuedUtc:O}\",\"exp\":\"{expiresUtc:O}\"}}";
    var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
    var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);
    return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
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

static string? FindDefaultPrivateKeyPath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "private-key.txt"),
        Path.Combine(AppContext.BaseDirectory, "license-keys", "private-key.txt"),
        Path.Combine(Environment.CurrentDirectory, "private-key.txt"),
        Path.Combine(Environment.CurrentDirectory, "license-keys", "private-key.txt")
    };

    return candidates.FirstOrDefault(File.Exists);
}

static bool HasEmbeddedPrivateKey()
{
    return Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("private-key.txt");
}

static string ReadEmbeddedPrivateKey()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("private-key.txt");
    if (stream == null)
        throw new InvalidOperationException("No private key was embedded. Rebuild with license-keys/private-key.txt present or pass --private-key.");

    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd().Trim();
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

static string EscapeJson(string value)
{
    var builder = new StringBuilder(value.Length + 8);

    foreach (var ch in value)
    {
        switch (ch)
        {
            case '\\':
                builder.Append("\\\\");
                break;
            case '"':
                builder.Append("\\\"");
                break;
            case '\b':
                builder.Append("\\b");
                break;
            case '\f':
                builder.Append("\\f");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\r':
                builder.Append("\\r");
                break;
            case '\t':
                builder.Append("\\t");
                break;
            default:
                if (char.IsControl(ch))
                    builder.Append($"\\u{(int)ch:x4}");
                else
                    builder.Append(ch);
                break;
        }
    }

    return builder.ToString();
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
    Console.WriteLine("  dotnet run --project tools/SignalTracker.LicenseTool -- issue --machine XXXX-XXXX-XXXX-XXXX --days 365 --name \"Customer\"");
}
