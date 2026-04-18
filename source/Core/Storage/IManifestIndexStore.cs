using PowerPack.Models;

namespace PowerPack.Storage;

public interface IManifestIndexStore
{
    Task UpsertManifestAsync(
        SolutionManifest manifest,
        ManifestPackageMetadata? packageMetadata,
        CancellationToken cancellationToken
    );

    Task<SolutionManifest?> GetManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken);

    Task<IReadOnlyList<SolutionManifest>> ListManifestsAsync(string name, CancellationToken cancellationToken);

    Task<ManifestPackageMetadata?> GetPackageMetadataAsync(
        string name,
        SolutionVersion version,
        CancellationToken cancellationToken
    );

    Task DeleteManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken);

    Task<IReadOnlyList<DependentRecord>> GetDependentsAsync(string dependencyName, CancellationToken cancellationToken);
}
