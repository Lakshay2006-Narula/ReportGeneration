using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using SignalTracker.Licensing;
using SignalTracker.Models;

LoadRootEnvironmentFile();
var builder = WebApplication.CreateBuilder(args);
var dataDirectory = ResolveWritableDataDirectory();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
{
    options.AddPolicy("DesktopLoopback", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                    return true;

                return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                       && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                           || uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                           || uri.Host.Equals("tauri.localhost", StringComparison.OrdinalIgnoreCase)
                           || uri.Scheme.Equals("tauri", StringComparison.OrdinalIgnoreCase)
                           || uri.IsLoopback);
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    var dbPath = Path.Combine(dataDirectory, "offline.db");
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddSingleton<LicenseService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("Report", limiterOptions =>
    {
        limiterOptions.PermitLimit = 4;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 4;
        limiterOptions.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
    });
});

var app = builder.Build();
var enforceLicense = builder.Configuration.GetValue("License:Enforce", false);

app.UseCors("DesktopLoopback");
app.UseRateLimiter();

// Every /api/* call (other than license status/activation and the health probe) requires an
// activated, machine-matched license. This is the app's real access control — there is no user
// login in this single-user offline desktop tool.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isExempt = path.StartsWithSegments("/api/license") || path.StartsWithSegments("/api/health");

    if (enforceLicense && path.StartsWithSegments("/api") && !isExempt)
    {
        var licenseService = context.RequestServices.GetRequiredService<LicenseService>();
        if (!licenseService.IsActivated)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "LICENSE_REQUIRED",
                message = "This application is not licensed for this device."
            });
            return;
        }
    }

    try
    {
        await next();
    }
    catch (LicenseFeatureException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "LICENSE_FEATURE_REQUIRED",
            feature = ex.CaptureType,
            message = ex.Message
        });
    }
});

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static void LoadRootEnvironmentFile()
{
    var searchRoots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    var envPath = searchRoots
        .SelectMany(start => WalkParents(start))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(directory => Path.Combine(directory, ".env"))
        .FirstOrDefault(File.Exists);

    if (envPath == null)
        return;

    foreach (var rawLine in File.ReadLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            continue;

        var separator = line.IndexOf('=');
        if (separator <= 0)
            continue;

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        if (!string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}

static IEnumerable<string> WalkParents(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory != null)
    {
        yield return directory.FullName;
        directory = directory.Parent;
    }
}

static string ResolveWritableDataDirectory()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "STracerReportGenerator"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STracerReportGenerator"),
        Path.Combine(Path.GetTempPath(), "STracerReportGenerator"),
        Path.Combine(Environment.CurrentDirectory, "backend-data")
    };

    foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
    {
        try
        {
            Directory.CreateDirectory(candidate);
            var probePath = Path.Combine(candidate, ".write-test");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return candidate;
        }
        catch
        {
            // Try the next per-user location.
        }
    }

    throw new InvalidOperationException("No writable data directory is available for the local backend.");
}
