using System.Text.Json.Serialization;

namespace SignalTracker.Licensing
{
    /// <summary>The signed payload embedded in every license key. Kept intentionally small.</summary>
    public class LicensePayload
    {
        [JsonPropertyName("mid")]
        public string MachineId { get; set; } = "";

        [JsonPropertyName("name")]
        public string? LicensedTo { get; set; }

        [JsonPropertyName("iat")]
        public string? IssuedUtc { get; set; }

        [JsonPropertyName("exp")]
        public string? ExpiresUtc { get; set; }
    }
}
