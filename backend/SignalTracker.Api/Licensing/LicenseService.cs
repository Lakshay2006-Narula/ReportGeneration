using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalTracker.Licensing
{
    public class LicenseStatus
    {
        public bool Activated { get; set; }
        public string MachineId { get; set; } = "";
        public string? LicensedTo { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Validates and persists offline, hardware-locked license keys.
    ///
    /// A license key is "base64url(payloadJson).base64url(ecdsaSignature)". The payload binds the
    /// key to one machine's <see cref="MachineIdProvider"/> id and optionally an expiry date. Only
    /// the ECDSA PUBLIC key lives here — keys are issued offline by the separate
    /// SignalTracker.LicenseTool, which holds the private key and never ships to customers.
    /// </summary>
    public class LicenseService
    {
        // Base64 SubjectPublicKeyInfo (DER) of the vendor's ECDSA P-256 public key.
        // Generated once via `SignalTracker.LicenseTool genkeys`. The matching PRIVATE key is never
        // embedded here and never ships with this app.
        private const string EmbeddedPublicKeyBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEE4sier6l+zFtXR4WxOoQWVxwUvelvU3B3sWOIS6AjyfVtzAuAvZH5fs70I8TgArRMzf3X5QE22raRWIZUjKuRw==";

        private static readonly string LicenseDirectory = ResolveWritableLicenseDirectory();

        private static readonly string LicenseFilePath = Path.Combine(LicenseDirectory, "license.dat");

        private readonly object _lock = new();
        private LicenseStatus? _cachedStatus;

        public string MachineId { get; } = MachineIdProvider.GetMachineId();

        public LicenseStatus GetStatus()
        {
            lock (_lock)
            {
                return _cachedStatus ??= LoadPersistedLicense();
            }
        }

        public bool IsActivated => GetStatus().Activated;

        public LicenseStatus Activate(string licenseKey)
        {
            lock (_lock)
            {
                var status = ValidateLicenseKey(licenseKey);
                if (status.Activated)
                {
                    try
                    {
                        PersistLicenseKey(licenseKey);
                    }
                    catch (Exception ex)
                    {
                        status = NotActivated($"License is valid, but it could not be saved: {ex.Message}");
                    }
                }

                _cachedStatus = status;
                return status;
            }
        }

        private LicenseStatus LoadPersistedLicense()
        {
            try
            {
                if (!File.Exists(LicenseFilePath))
                    return NotActivated();

                var protectedBytes = File.ReadAllBytes(LicenseFilePath);

                byte[] plainBytes;
                try
                {
                    plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    // Corrupted, foreign, or from a different Windows user profile — not activated.
                    return NotActivated();
                }

                var licenseKey = Encoding.UTF8.GetString(plainBytes);
                return ValidateLicenseKey(licenseKey);
            }
            catch
            {
                return NotActivated();
            }
        }

        private void PersistLicenseKey(string licenseKey)
        {
            Directory.CreateDirectory(LicenseDirectory);
            var plainBytes = Encoding.UTF8.GetBytes(licenseKey);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(LicenseFilePath, protectedBytes);
        }

        private static string ResolveWritableLicenseDirectory()
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
                    var targetPath = Path.Combine(candidate, "license.dat");

                    if (File.Exists(targetPath))
                    {
                        using var stream = File.Open(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    }
                    else
                    {
                        var probePath = Path.Combine(candidate, ".license-write-test");
                        File.WriteAllText(probePath, "ok");
                        File.Delete(probePath);
                    }

                    return candidate;
                }
                catch
                {
                    // Try the next per-user location.
                }
            }

            throw new InvalidOperationException("No writable license directory is available.");
        }

        private LicenseStatus NotActivated(string? error = null) => new()
        {
            Activated = false,
            MachineId = MachineId,
            Error = error
        };

        private LicenseStatus ValidateLicenseKey(string? licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return NotActivated("License key is empty.");

            var parts = licenseKey.Trim().Replace(" ", "").Replace("\r", "").Replace("\n", "").Split('.');
            if (parts.Length != 2)
                return NotActivated("License key format is invalid.");

            byte[] payloadBytes;
            byte[] signatureBytes;
            try
            {
                payloadBytes = Base64UrlDecode(parts[0]);
                signatureBytes = Base64UrlDecode(parts[1]);
            }
            catch
            {
                return NotActivated("License key is not valid base64.");
            }

            bool signatureValid;
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(EmbeddedPublicKeyBase64), out _);
                signatureValid = ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256);
            }
            catch
            {
                return NotActivated("License signature could not be verified.");
            }

            if (!signatureValid)
                return NotActivated("License signature is invalid.");

            LicensePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes);
            }
            catch
            {
                return NotActivated("License payload is corrupted.");
            }

            if (payload == null || string.IsNullOrWhiteSpace(payload.MachineId))
                return NotActivated("License payload is corrupted.");

            if (!string.Equals(payload.MachineId, MachineId, StringComparison.OrdinalIgnoreCase))
                return NotActivated("This license key was issued for a different machine.");

            DateTime? expiresUtc = null;
            if (!string.IsNullOrWhiteSpace(payload.ExpiresUtc))
            {
                if (!DateTime.TryParse(
                        payload.ExpiresUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedExpiry))
                {
                    return NotActivated("License expiry date is invalid.");
                }

                expiresUtc = parsedExpiry;
                if (expiresUtc.Value < DateTime.UtcNow)
                    return NotActivated("This license key has expired.");
            }

            return new LicenseStatus
            {
                Activated = true,
                MachineId = MachineId,
                LicensedTo = payload.LicensedTo,
                ExpiresUtc = expiresUtc
            };
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var padded = input.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}
