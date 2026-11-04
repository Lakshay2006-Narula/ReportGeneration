namespace SignalTracker.Licensing;

public sealed class LicenseFeatureException(string captureType) : Exception(
    $"Your license does not include {(captureType == "wifi" ? "Wi-Fi" : "Cellular")} reports.")
{
    public string CaptureType { get; } = captureType;
}
