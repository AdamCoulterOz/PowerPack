namespace PowerPack.Models;

public static class ManifestNormalizer
{
    public static SolutionManifest Normalize(SolutionManifest manifest)
    {
        var name = RequireNonEmpty(manifest.Name, "Manifest name must be a non-empty string.");
        var publisher = RequireNonEmpty(manifest.Publisher, "Manifest publisher must be a non-empty string.");
        var version = SolutionVersion.Parse(manifest.Version).ToString();

        var dependencyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in manifest.Dependencies.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var dependencyName = RequireNonEmpty(entry.Key, "Dependency names must be non-empty strings.");
            var existingDependencyName = dependencyNames.GetValueOrDefault(dependencyName);
            if (existingDependencyName is not null && existingDependencyName != dependencyName)
            {
                throw new PowerPackValidationException(
                    $"Manifest '{name}' version '{version}' declares dependency names that differ only by case: " +
                    $"'{existingDependencyName}' and '{dependencyName}'."
                );
            }

            dependencyNames[dependencyName] = dependencyName;
            dependencies[dependencyName] = SolutionVersion.Parse(entry.Value).ToString();
        }

        return new SolutionManifest
        {
            Name = name,
            Version = version,
            Publisher = publisher,
            Dependencies = dependencies,
            Connections = manifest.Connections.DeepClone().AsObject(),
            Variables = manifest.Variables.DeepClone().AsObject(),
            Metadata = manifest.Metadata?.DeepClone().AsObject(),
        };
    }

    private static string RequireNonEmpty(string? value, string error)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PowerPackValidationException(error);
        return value.Trim();
    }
}
