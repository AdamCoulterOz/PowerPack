using PowerPack.Models;

namespace PowerPack.Storage;

public sealed class InMemoryManifestIndexStore : IManifestIndexStore
{
    private readonly Dictionary<string, Dictionary<string, SolutionManifest>> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, ManifestPackageMetadata>> _packages = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertManifestAsync(
        SolutionManifest manifest,
        ManifestPackageMetadata? packageMetadata,
        CancellationToken cancellationToken
    )
    {
        var normalized = ManifestNormalizer.Normalize(manifest);
        var partition = GetOrCreatePartition(normalized.Name);
        ValidateStoredNameConsistency(partition.Values.Select(existing => existing.Name), normalized.Name);
        partition[normalized.Version] = normalized;
        if (packageMetadata is not null)
        {
            var packagePartition = GetOrCreatePackagePartition(normalized.Name);
            packagePartition[normalized.Version] = packageMetadata;
        }
        return Task.CompletedTask;
    }

    public Task<SolutionManifest?> GetManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken)
    {
        if (_manifests.TryGetValue(name, out var partition) && partition.TryGetValue(version.ToString(), out var manifest))
            return Task.FromResult<SolutionManifest?>(manifest);

        return Task.FromResult<SolutionManifest?>(null);
    }

    public Task<IReadOnlyList<SolutionManifest>> ListManifestsAsync(string name, CancellationToken cancellationToken)
    {
        if (!_manifests.TryGetValue(name, out var partition))
            return Task.FromResult<IReadOnlyList<SolutionManifest>>([]);

        ValidateStoredNameConsistency(partition.Values.Select(manifest => manifest.Name), partition.Values.First().Name);

        IReadOnlyList<SolutionManifest> manifests = [.. partition.Values.OrderByDescending(manifest => manifest.ParsedVersion)];

        return Task.FromResult(manifests);
    }

    public Task<ManifestPackageMetadata?> GetPackageMetadataAsync(
        string name,
        SolutionVersion version,
        CancellationToken cancellationToken
    )
    {
        if (_packages.TryGetValue(name, out var partition) &&
            partition.TryGetValue(version.ToString(), out var metadata))
            return Task.FromResult<ManifestPackageMetadata?>(metadata);

        return Task.FromResult<ManifestPackageMetadata?>(null);
    }

    public Task DeleteManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken)
    {
        if (_manifests.TryGetValue(name, out var partition))
        {
            partition.Remove(version.ToString());
            if (partition.Count == 0)
                _manifests.Remove(name);
        }

        if (_packages.TryGetValue(name, out var packagePartition))
        {
            packagePartition.Remove(version.ToString());
            if (packagePartition.Count == 0)
                _packages.Remove(name);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DependentRecord>> GetDependentsAsync(string dependencyName, CancellationToken cancellationToken)
    {
        var dependents = _manifests.Values
            .SelectMany(partition => partition.Values)
            .SelectMany(manifest => manifest.Dependencies.Select(
                dependency => new
                {
                    Manifest = manifest,
                    DependencyName = dependency.Key,
                    DependencyVersion = dependency.Value,
                }
            ))
            .Where(entry => entry.DependencyName.Equals(dependencyName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.Manifest.ParsedVersion)
            .Select(entry => new DependentRecord
            {
                Dependency = entry.DependencyName,
                Dependent = entry.Manifest.Name,
                DependentVersion = entry.Manifest.Version,
                RequiredVersion = entry.DependencyVersion,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DependentRecord>>(dependents);
    }

    private Dictionary<string, SolutionManifest> GetOrCreatePartition(string name)
    {
        if (!_manifests.TryGetValue(name, out var partition))
        {
            partition = new Dictionary<string, SolutionManifest>(StringComparer.Ordinal);
            _manifests[name] = partition;
        }

        return partition;
    }

    private Dictionary<string, ManifestPackageMetadata> GetOrCreatePackagePartition(string name)
    {
        if (!_packages.TryGetValue(name, out var partition))
        {
            partition = new Dictionary<string, ManifestPackageMetadata>(StringComparer.Ordinal);
            _packages[name] = partition;
        }

        return partition;
    }

    private static void ValidateStoredNameConsistency(IEnumerable<string> exactNames, string expectedName)
    {
        var distinctNames = exactNames.Distinct(StringComparer.Ordinal).ToList();
        if (distinctNames.Count > 1)
            throw new PowerPackValidationException(
                $"Manifest index contains solution names that differ only by case: {string.Join(", ", distinctNames)}."
            );
    }
}
