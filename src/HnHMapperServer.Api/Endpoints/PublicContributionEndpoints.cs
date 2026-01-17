using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HnHMapperServer.Api.Endpoints;

/// <summary>
/// Public contribution endpoints - anonymous .hmap file uploads
/// Files are stored for manual processing by administrators
/// </summary>
public static class PublicContributionEndpoints
{
    private const int MaxFileSizeBytes = 200 * 1024 * 1024; // 200 MB
    private const string HmapSignature = "Haven Mapfile 1";
    private static readonly Regex SafeFilenameRegex = new(@"[^a-zA-Z0-9\-_\.]", RegexOptions.Compiled);

    public static void MapPublicContributionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/public")
            .AllowAnonymous()
            .WithTags("Public Contributions");

        group.MapPost("/contribute", HandleContribution)
            .RequireRateLimiting("HmapContribution")
            .DisableAntiforgery()
            .WithName("ContributeHmap")
            .WithSummary("Upload an .hmap file for contribution (anonymous, rate limited)");
    }

    private static async Task<IResult> HandleContribution(
        HttpContext context,
        IConfiguration configuration,
        ILogger<Program> logger)
    {
        var clientIp = GetClientIp(context);

        try
        {
            // Check content type
            if (!context.Request.HasFormContentType)
            {
                logger.LogWarning("Contribution rejected: Invalid content type from {IP}", AnonymizeIp(clientIp));
                return Results.BadRequest(new { error = "Invalid content type. Expected multipart/form-data" });
            }

            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");

            if (file == null || file.Length == 0)
            {
                logger.LogWarning("Contribution rejected: No file provided from {IP}", AnonymizeIp(clientIp));
                return Results.BadRequest(new { error = "No file provided" });
            }

            // Validate file extension
            var originalFileName = file.FileName;
            if (!originalFileName.EndsWith(".hmap", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Contribution rejected: Invalid extension '{FileName}' from {IP}", originalFileName, AnonymizeIp(clientIp));
                return Results.BadRequest(new { error = "Invalid file type. Only .hmap files are accepted" });
            }

            // Validate file size
            if (file.Length > MaxFileSizeBytes)
            {
                logger.LogWarning("Contribution rejected: File too large ({Size} bytes) from {IP}", file.Length, AnonymizeIp(clientIp));
                return Results.Problem(
                    detail: $"File size exceeds maximum limit of {MaxFileSizeBytes / (1024 * 1024)} MB",
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // Validate .hmap signature
            using var stream = file.OpenReadStream();
            var signatureBuffer = new byte[HmapSignature.Length];
            var bytesRead = await stream.ReadAsync(signatureBuffer);

            if (bytesRead < HmapSignature.Length)
            {
                logger.LogWarning("Contribution rejected: File too small to contain signature from {IP}", AnonymizeIp(clientIp));
                return Results.BadRequest(new { error = "Invalid .hmap file: file too small" });
            }

            var fileSignature = System.Text.Encoding.ASCII.GetString(signatureBuffer);
            if (fileSignature != HmapSignature)
            {
                logger.LogWarning("Contribution rejected: Invalid signature '{Signature}' from {IP}", fileSignature, AnonymizeIp(clientIp));
                return Results.BadRequest(new { error = "Invalid .hmap file: signature mismatch" });
            }

            // Reset stream position for full file read
            stream.Position = 0;

            // Get optional slug parameter
            var slug = form["slug"].FirstOrDefault() ?? "unknown";

            // Create contribution ID and file paths
            var timestamp = DateTime.UtcNow;
            var guid = Guid.NewGuid().ToString("N")[..8];
            var sanitizedFileName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalFileName));
            var contributionId = $"{timestamp:yyyy-MM-dd_HH-mm-ss}_{guid}";
            var fileName = $"{contributionId}_{sanitizedFileName}.hmap";

            var gridStorage = configuration["GridStorage"] ?? "map";
            var contributionsDir = Path.Combine(gridStorage, "contributions");
            Directory.CreateDirectory(contributionsDir);

            var filePath = Path.Combine(contributionsDir, fileName);
            var metadataPath = Path.Combine(contributionsDir, $"{fileName}.meta.json");

            // Save the file
            await using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await stream.CopyToAsync(fileStream);
            }

            // Create metadata
            var metadata = new
            {
                originalFileName = originalFileName,
                uploadTimestamp = timestamp.ToString("O"),
                clientIp = AnonymizeIp(clientIp),
                fileSizeBytes = file.Length,
                publicMapSlug = slug,
                contributionId = contributionId
            };

            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            logger.LogInformation(
                "Contribution accepted: {ContributionId} ({Size} bytes) for slug '{Slug}' from {IP}",
                contributionId, file.Length, slug, AnonymizeIp(clientIp));

            return Results.Ok(new
            {
                success = true,
                contributionId = contributionId,
                message = "Thank you for your contribution!"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing contribution from {IP}", AnonymizeIp(clientIp));
            return Results.Problem(
                detail: "An error occurred while processing your contribution",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Get client IP address, handling reverse proxy headers
    /// </summary>
    private static string GetClientIp(HttpContext context)
    {
        // Check X-Forwarded-For header first (for reverse proxy scenarios)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // Take the first IP in the chain (original client)
            var firstIp = forwardedFor.Split(',')[0].Trim();
            return firstIp;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Anonymize IP address by zeroing the last octet (IPv4) or last 80 bits (IPv6)
    /// </summary>
    private static string AnonymizeIp(string ip)
    {
        if (ip == "unknown") return ip;

        // IPv4: Replace last octet with 0
        if (ip.Contains('.') && !ip.Contains(':'))
        {
            var parts = ip.Split('.');
            if (parts.Length == 4)
            {
                return $"{parts[0]}.{parts[1]}.{parts[2]}.0";
            }
        }

        // IPv6: Replace last 80 bits (truncate at 3rd group)
        if (ip.Contains(':'))
        {
            var parts = ip.Split(':');
            if (parts.Length >= 3)
            {
                return $"{parts[0]}:{parts[1]}:{parts[2]}::0";
            }
        }

        return ip;
    }

    /// <summary>
    /// Sanitize filename to only allow alphanumeric, hyphen, underscore, and dot
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        // Get just the filename without path
        fileName = Path.GetFileName(fileName);

        // Replace unsafe characters
        var sanitized = SafeFilenameRegex.Replace(fileName, "_");

        // Limit length
        if (sanitized.Length > 50)
        {
            sanitized = sanitized[..50];
        }

        // Ensure not empty
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "contribution";
        }

        return sanitized;
    }
}
