using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using SignalTracker.Licensing;
using SignalTracker.Models;

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

    await next();
});

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

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
