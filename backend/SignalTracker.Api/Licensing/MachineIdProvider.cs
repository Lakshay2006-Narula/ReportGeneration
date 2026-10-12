using System.Security.Cryptography;
using System.Text;

namespace SignalTracker.Licensing
{
    /// <summary>
    /// Derives a stable, display-friendly identifier for this Windows installation, used to bind
    /// license keys to a single machine. Never reversible back to the raw MachineGuid.
    /// </summary>
    public static class MachineIdProvider
    {
        private const string Salt = "STracerReportGenerator.OfflineLicense.v1";

        public static string GetMachineId()
        {
            var raw = TryGetWindowsMachineGuid() ?? FallbackIdentifier();
            return Hash(raw);
        }

        private static string? TryGetWindowsMachineGuid()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var value = key?.GetValue("MachineGuid") as string;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string FallbackIdentifier()
        {
            return $"{Environment.MachineName}|{Environment.ProcessorCount}|{Environment.OSVersion.VersionString}|{Environment.UserName}";
        }

        private static string Hash(string raw)
        {
            var bytes = Encoding.UTF8.GetBytes($"{Salt}:{raw}");
            var digest = SHA256.HashData(bytes);
            var hex = Convert.ToHexString(digest); // 64 uppercase hex chars
            var shortId = hex[..16];
            return string.Join("-", Enumerable.Range(0, 4).Select(i => shortId.Substring(i * 4, 4)));
        }
    }
}
