using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using PowerPack.Models;
using PowerPack.Options;

namespace PowerPack.Services;

public interface IPackageBlobStore
{
    Task<ManifestPackageMetadata> UploadAsync(
        string packageName,
        SolutionVersion version,
        string quality,
        Stream content,
        CancellationToken cancellationToken
    );

    Task<PackageDownloadResult?> DownloadAsync(
        ManifestPackageMetadata packageMetadata,
        CancellationToken cancellationToken
    );

    Task DeleteAsync(ManifestPackageMetadata packageMetadata, CancellationToken cancellationToken);
}

public sealed class PackageDownloadResult : IAsyncDisposable
{
    public required Stream Content { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long ContentLength { get; init; }

    public async ValueTask DisposeAsync() => await Content.DisposeAsync();
}

public sealed class PackageBlobStore : IPackageBlobStore
{
    private readonly BlobContainerClient _containerClient;

    public PackageBlobStore(PowerPackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _containerClient = options.Storage
            .CreateBlobServiceClient()
            .GetBlobContainerClient(options.Storage.PackageContainerName);
    }

    public async Task<ManifestPackageMetadata> UploadAsync(
        string packageName,
        SolutionVersion version,
        string quality,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(quality);
        ArgumentNullException.ThrowIfNull(content);

        await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var fileName = $"{packageName}_{version}.zip";
        var blobName = BuildBlobName(packageName, version, quality, fileName);
        var blobClient = _containerClient.GetBlobClient(blobName);

        if (content.CanSeek)
            content.Position = 0;

        await blobClient.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentDisposition = $@"attachment; filename=""{fileName}""",
                    ContentType = "application/zip",
                },
                Tags = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Package"] = packageName,
                    ["Version"] = version.ToString(),
                    ["Quality"] = quality,
                },
            },
            cancellationToken
        );

        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        return new ManifestPackageMetadata
        {
            BlobName = blobName,
            FileName = fileName,
            ContentType = properties.Value.ContentType ?? "application/zip",
            ContentLength = properties.Value.ContentLength,
            Quality = quality,
        };
    }

    public async Task<PackageDownloadResult?> DownloadAsync(
        ManifestPackageMetadata packageMetadata,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(packageMetadata);

        var blobClient = _containerClient.GetBlobClient(packageMetadata.BlobName);
        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            return new PackageDownloadResult
            {
                Content = response.Value.Content,
                FileName = packageMetadata.FileName,
                ContentType = response.Value.Details.ContentType ?? packageMetadata.ContentType,
                ContentLength = response.Value.Details.ContentLength,
            };
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(ManifestPackageMetadata packageMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageMetadata);

        var blobClient = _containerClient.GetBlobClient(packageMetadata.BlobName);
        await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
    }

    private static string BuildBlobName(string packageName, SolutionVersion version, string quality, string fileName) =>
        $"{Uri.EscapeDataString(packageName)}/{version}/{quality}/{Uri.EscapeDataString(fileName)}";
}
