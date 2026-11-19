using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace SignalTracker.Services
{
    /// <summary>Stores generated Excel reports in private S3 and emails expiring download links.</summary>
    public sealed class ReportDeliveryService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public ReportDeliveryService(IConfiguration configuration, ILogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task UploadAndEmailAsync(
            byte[] reportBytes,
            string fileName,
            string recipientEmailsText,
            string projectName,
            CancellationToken cancellationToken = default)
        {
            var recipientEmails = recipientEmailsText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (recipientEmails.Length == 0)
                throw new InvalidOperationException("At least one recipient email address is required.");

            foreach (var recipientEmail in recipientEmails)
                _ = MailboxAddress.Parse(recipientEmail);

            var bucket = Required("AWS_S3_BUCKET");
            var regionName = Required("AWS_REGION");
            var endpoint = Required("AWS_S3_ENDPOINT");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || endpointUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("AWS_S3_ENDPOINT must be an absolute HTTPS URL.");
            }
            var ttl = int.TryParse(_configuration["S3_DOWNLOAD_URL_TTL_SECONDS"], out var configuredTtl)
                ? configuredTtl
                : 86400;
            if (ttl is < 1 or > 604800)
                throw new InvalidOperationException("S3_DOWNLOAD_URL_TTL_SECONDS must be between 1 and 604800.");

            var safeFileName = Path.GetFileName(fileName);
            var objectKey = $"reports/{Guid.NewGuid():N}/{safeFileName}";
            var contentType = Path.GetExtension(safeFileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            // AWS SDK resolves credentials from the standard provider chain (environment,
            // shared credentials, or an instance/task role). No keys belong in source code.
            _logger.LogInformation("[ReportDelivery] Uploading report to S3. Bucket={Bucket}, Key={ObjectKey}", bucket, objectKey);
            using var s3 = new AmazonS3Client(new AmazonS3Config
            {
                ServiceURL = endpointUri.ToString().TrimEnd('/'),
                AuthenticationRegion = regionName,
                ForcePathStyle = true,
                // Avoid sending newer optional checksum headers that some S3-compatible
                // providers reject; required checksum validation remains enabled.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
            await using var stream = new MemoryStream(reportBytes, writable: false);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = objectKey,
                InputStream = stream,
                ContentType = contentType
            }, cancellationToken);
            _logger.LogInformation("[ReportDelivery] S3 upload completed. Bucket={Bucket}, Key={ObjectKey}", bucket, objectKey);

            var downloadUrl = s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = objectKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddSeconds(ttl),
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentDisposition = $"attachment; filename=\"{safeFileName.Replace("\"", "") }\""
                }
            });

            _logger.LogInformation("[ReportDelivery] Sending report link through SMTP. Key={ObjectKey}", objectKey);
            await SendEmailAsync(recipientEmails, projectName, downloadUrl, cancellationToken);
            _logger.LogInformation("[ReportDelivery] SMTP server accepted report email for {RecipientCount} recipient(s). Key={ObjectKey}", recipientEmails.Length, objectKey);
        }

        private async Task SendEmailAsync(
            IReadOnlyList<string> recipientEmails,
            string projectName,
            string downloadUrl,
            CancellationToken cancellationToken)
        {
            var host = Required("SMTP_HOST");
            var username = Required("SMTP_USER");
            // Use the authenticated SMTP mailbox as the sender address.
            var from = username;
            var port = int.TryParse(_configuration["SMTP_PORT"], out var configuredPort) ? configuredPort : 587;
            var password = _configuration["SMTP_PASS"];
            var securityType = (_configuration["Security_type"] ?? "STARTTLS").Trim();
            if (!securityType.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase)
                && !securityType.Equals("TLS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Security_type must be TLS or STARTTLS.");
            }

            var secureSocketOptions = securityType.Equals("TLS", StringComparison.OrdinalIgnoreCase)
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, port, secureSocketOptions, cancellationToken);
            if (!string.IsNullOrWhiteSpace(username))
                await smtp.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);

            var failedRecipients = 0;
            for (var index = 0; index < recipientEmails.Count; index++)
            {
                var message = new MimeMessage();
                message.From.Add(MailboxAddress.Parse(from));
                message.To.Add(MailboxAddress.Parse(recipientEmails[index]));
                message.Subject = $"Report ready: {projectName}";
                message.Body = new TextPart("plain")
                {
                    Text = $"Hello,\r\n\r\nYour drive test report for {projectName} has been generated successfully.\r\n\r\nYou can download the report using the link below:\r\n{downloadUrl}\r\n\r\nThis download link will expire within 24 hours.\r\n\r\nRegards,\r\nStracer Team"
                };

                try
                {
                    await smtp.SendAsync(message, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedRecipients++;
                    _logger.LogError(ex, "[ReportDelivery] SMTP failed for recipient {RecipientNumber} of {RecipientCount}.", index + 1, recipientEmails.Count);
                }
            }

            await smtp.DisconnectAsync(true, cancellationToken);
            if (failedRecipients > 0)
                throw new InvalidOperationException($"SMTP failed to deliver to {failedRecipients} of {recipientEmails.Count} recipient(s). See backend logs for details.");
        }

        private string Required(string key)
        {
            var value = _configuration[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Missing configuration value: {key}");
            return value.Trim();
        }
    }
}
