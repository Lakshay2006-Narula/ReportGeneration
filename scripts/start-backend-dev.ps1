$healthUrl = "http://127.0.0.1:5000/api/health"
$existingBackend = $null

try {
    $existingBackend = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 2 -ErrorAction Stop
} catch {
    $existingBackend = $null
}

if ($existingBackend -and $existingBackend.StatusCode -ge 200 -and $existingBackend.StatusCode -lt 300) {
    Write-Host "Using the existing development backend at http://127.0.0.1:5000"
    exit 0
}

$env:ASPNETCORE_URLS = "http://127.0.0.1:5000"
dotnet run --project "backend\SignalTracker.Api\SignalTracker.Api.csproj" --no-launch-profile
exit $LASTEXITCODE
