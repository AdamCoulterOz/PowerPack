using System.ComponentModel.DataAnnotations;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace PowerPack.Options;

public sealed class PowerPackOptions
{
    public const string SectionName = "PowerPack";

    [Required]
    public StorageOptions Storage { get; init; } = new();

    [Required]
    public DownloadOptions Downloads { get; init; } = new();

    [Required]
    public AuthOptions Auth { get; init; } = new();
}

public sealed class StorageOptions
{
    public string? ConnectionString { get; init; }

    public string? AccountUrl { get; init; }

    public string? BlobAccountUrl { get; init; }

    [Required]
    public string SolutionIndexTableName { get; init; } = "solutionindex";

    [Required]
    public string DependencyIndexTableName { get; init; } = "dependencyindex";

    [Required]
    public string PackageContainerName { get; init; } = "packages";

    public TableServiceClient CreateTableServiceClient()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return new TableServiceClient(ConnectionString);

        if (!string.IsNullOrWhiteSpace(AccountUrl))
        {
            TokenCredential credential = new DefaultAzureCredential();
            return new TableServiceClient(new Uri(AccountUrl), credential);
        }

        throw new InvalidOperationException(
            "PowerPack storage configuration is invalid. Set either PowerPack:Storage:ConnectionString " +
            "or PowerPack:Storage:AccountUrl."
        );
    }

    public BlobServiceClient CreateBlobServiceClient()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return new BlobServiceClient(ConnectionString);

        if (!string.IsNullOrWhiteSpace(BlobAccountUrl))
        {
            TokenCredential credential = new DefaultAzureCredential();
            return new BlobServiceClient(new Uri(BlobAccountUrl), credential);
        }

        throw new InvalidOperationException(
            "PowerPack blob storage configuration is invalid. Set either PowerPack:Storage:ConnectionString " +
            "or PowerPack:Storage:BlobAccountUrl."
        );
    }
}

public sealed class DownloadOptions
{
    [Required]
    [MinLength(32)]
    public string TokenSigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; init; } = 30;
}

public sealed class AuthOptions
{
    [Required]
    public string ApplicationClientId { get; init; } = string.Empty;

    [Required]
    public string ApplicationIdUri { get; init; } = string.Empty;

    [Required]
    public string TenantId { get; init; } = string.Empty;

    [Required]
    public string RequiredRole { get; init; } = "PowerPack.Access";

    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";
}
