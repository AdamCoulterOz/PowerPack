using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using PowerPack.Models;
using PowerPack.Options;

namespace PowerPack.Services;

public sealed class PackageDownloadTokenService(IOptions<PowerPackOptions> options)
{
    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(options.Value.Downloads.TokenSigningKey);
    private readonly int _tokenLifetimeMinutes = options.Value.Downloads.TokenLifetimeMinutes;

    public string CreateToken(string packageName, string version)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DownloadTokenPayload
        {
            Package = packageName,
            Version = version,
            ExpiresAtUnixTimeSeconds = DateTimeOffset.UtcNow.AddMinutes(_tokenLifetimeMinutes).ToUnixTimeSeconds(),
        });

        Span<byte> signature = stackalloc byte[32];
        using var hmac = new HMACSHA256(_signingKey);
        hmac.TryComputeHash(payload, signature, out _);

        return $"{WebEncoders.Base64UrlEncode(payload)}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    public void ValidateToken(string token, string packageName, string version)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new PowerPackValidationException("Package download token is required.");

        var parts = token.Split('.', 2, StringSplitOptions.None);
        if (parts.Length != 2)
            throw new PowerPackValidationException("Package download token is invalid.");

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = WebEncoders.Base64UrlDecode(parts[0]);
            signatureBytes = WebEncoders.Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            throw new PowerPackValidationException("Package download token is invalid.");
        }

        using var hmac = new HMACSHA256(_signingKey);
        var expectedSignature = hmac.ComputeHash(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
            throw new PowerPackValidationException("Package download token signature is invalid.");

        var payload = JsonSerializer.Deserialize<DownloadTokenPayload>(payloadBytes)
            ?? throw new PowerPackValidationException("Package download token payload is invalid.");

        if (!string.Equals(payload.Package, packageName, StringComparison.Ordinal) ||
            !string.Equals(payload.Version, version, StringComparison.Ordinal))
            throw new PowerPackValidationException("Package download token does not match the requested package.");

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.ExpiresAtUnixTimeSeconds)
            throw new PowerPackValidationException("Package download token has expired.");
    }

    private sealed class DownloadTokenPayload
    {
        public required string Package { get; init; }

        public required string Version { get; init; }

        public required long ExpiresAtUnixTimeSeconds { get; init; }
    }
}
