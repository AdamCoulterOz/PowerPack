using System.Text.Json.Serialization;

namespace PowerPack.Models;

public sealed class ManifestPackageMetadata
{
    [JsonPropertyName("blobName")]
    public required string BlobName { get; init; }

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("contentLength")]
    public required long ContentLength { get; init; }

    [JsonPropertyName("quality")]
    public required string Quality { get; init; }
}

public sealed class ResolvedPackage
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("contentLength")]
    public required long ContentLength { get; init; }

    [JsonPropertyName("quality")]
    public required string Quality { get; init; }

    [JsonPropertyName("downloadUrl")]
    public required string DownloadUrl { get; init; }
}

public sealed class PublishedPackageResponse
{
    [JsonPropertyName("manifest")]
    public required SolutionManifest Manifest { get; init; }

    [JsonPropertyName("package")]
    public required ManifestPackageMetadata Package { get; init; }
}
