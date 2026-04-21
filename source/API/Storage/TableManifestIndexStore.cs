using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using PowerPack.Models;
using PowerPack.Options;

namespace PowerPack.Storage;

public sealed class TableManifestIndexStore : IManifestIndexStore
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly TableClient _solutionIndex;
    private readonly TableClient _dependencyIndex;

    public TableManifestIndexStore(PowerPackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var tableServiceClient = options.Storage.CreateTableServiceClient();
        _solutionIndex = tableServiceClient.GetTableClient(options.Storage.SolutionIndexTableName);
        _dependencyIndex = tableServiceClient.GetTableClient(options.Storage.DependencyIndexTableName);
    }

    public async Task UpsertManifestAsync(
        SolutionManifest manifest,
        ManifestPackageMetadata? packageMetadata,
        CancellationToken cancellationToken
    )
    {
        var normalized = ManifestNormalizer.Normalize(manifest);
        await _solutionIndex.CreateIfNotExistsAsync(cancellationToken);
        await ValidateStoredNameConsistencyAsync(normalized.Name, cancellationToken);

        var existing = await GetManifestAsync(normalized.Name, normalized.ParsedVersion, cancellationToken);
        if (existing is not null)
            await DeleteDependencyRowsAsync(existing, cancellationToken);

        await _dependencyIndex.CreateIfNotExistsAsync(cancellationToken);

        var manifestJson = JsonSerializer.Serialize(normalized, JsonSerializerOptions);
        var entity = new TableEntity(GetSolutionPartitionKey(normalized.Name), normalized.Version)
        {
            ["Name"] = normalized.Name,
            ["NameKey"] = GetSolutionPartitionKey(normalized.Name),
            ["Version"] = normalized.Version,
            ["Publisher"] = normalized.Publisher,
            ["ManifestJson"] = manifestJson,
            ["DependenciesJson"] = JsonSerializer.Serialize(normalized.Dependencies, JsonSerializerOptions),
            ["ConnectionsJson"] = normalized.Connections.ToJsonString(JsonSerializerOptions),
            ["VariablesJson"] = normalized.Variables.ToJsonString(JsonSerializerOptions),
            ["MetadataJson"] = normalized.Metadata?.ToJsonString(JsonSerializerOptions) ?? "{}",
            ["UpdatedAtUtc"] = DateTimeOffset.UtcNow,
        };

        if (packageMetadata is not null)
        {
            entity["PackageBlobName"] = packageMetadata.BlobName;
            entity["PackageFileName"] = packageMetadata.FileName;
            entity["PackageContentType"] = packageMetadata.ContentType;
            entity["PackageContentLength"] = packageMetadata.ContentLength;
            entity["PackageQuality"] = packageMetadata.Quality;
        }

        await _solutionIndex.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);

        foreach (var dependency in normalized.Dependencies)
        {
            var dependencyEntity = new TableEntity(
                GetSolutionPartitionKey(dependency.Key),
                $"{GetSolutionPartitionKey(normalized.Name)}|{normalized.Version}"
            )
            {
                ["DependencyName"] = dependency.Key,
                ["DependencyKey"] = GetSolutionPartitionKey(dependency.Key),
                ["DependentName"] = normalized.Name,
                ["DependentKey"] = GetSolutionPartitionKey(normalized.Name),
                ["DependentVersion"] = normalized.Version,
                ["RequiredVersion"] = dependency.Value,
                ["UpdatedAtUtc"] = DateTimeOffset.UtcNow,
            };

            await _dependencyIndex.UpsertEntityAsync(dependencyEntity, TableUpdateMode.Replace, cancellationToken);
        }
    }

    public async Task<SolutionManifest?> GetManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken)
    {
        await _solutionIndex.CreateIfNotExistsAsync(cancellationToken);

        try
        {
            var response = await _solutionIndex.GetEntityAsync<TableEntity>(
                GetSolutionPartitionKey(name),
                version.ToString(),
                cancellationToken: cancellationToken
            );

            return ParseManifest(response.Value, name);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SolutionManifest>> ListManifestsAsync(string name, CancellationToken cancellationToken)
    {
        await _solutionIndex.CreateIfNotExistsAsync(cancellationToken);

        var manifests = new List<SolutionManifest>();
        await foreach (var entity in _solutionIndex.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeODataString(GetSolutionPartitionKey(name))}'",
            cancellationToken: cancellationToken
        ))
            manifests.Add(ParseManifest(entity, name));

        var exactNames = manifests.Select(manifest => manifest.Name).Distinct(StringComparer.Ordinal).ToList();
        if (exactNames.Count > 1)
            throw new PowerPackValidationException(
                $"Manifest index contains solution names that differ only by case: {string.Join(", ", exactNames)}."
            );

        return [.. manifests.OrderByDescending(manifest => manifest.ParsedVersion)];
    }

    public async Task<ManifestPackageMetadata?> GetPackageMetadataAsync(
        string name,
        SolutionVersion version,
        CancellationToken cancellationToken
    )
    {
        await _solutionIndex.CreateIfNotExistsAsync(cancellationToken);

        try
        {
            var response = await _solutionIndex.GetEntityAsync<TableEntity>(
                GetSolutionPartitionKey(name),
                version.ToString(),
                cancellationToken: cancellationToken
            );
            return ParsePackageMetadata(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteManifestAsync(string name, SolutionVersion version, CancellationToken cancellationToken)
    {
        var existing = await GetManifestAsync(name, version, cancellationToken);
        if (existing is null)
            return;

        await _solutionIndex.CreateIfNotExistsAsync(cancellationToken);
        await _solutionIndex.DeleteEntityAsync(
            GetSolutionPartitionKey(existing.Name),
            existing.Version,
            ETag.All,
            cancellationToken
        );

        await DeleteDependencyRowsAsync(existing, cancellationToken);
    }

    public async Task<IReadOnlyList<DependentRecord>> GetDependentsAsync(string dependencyName, CancellationToken cancellationToken)
    {
        await _dependencyIndex.CreateIfNotExistsAsync(cancellationToken);

        var dependents = new List<DependentRecord>();
        await foreach (var entity in _dependencyIndex.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeODataString(GetSolutionPartitionKey(dependencyName))}'",
            cancellationToken: cancellationToken
        ))
        {
            dependents.Add(new DependentRecord
            {
                Dependency = entity.GetString("DependencyName") ?? dependencyName,
                Dependent = entity.GetString("DependentName") ?? throw new PowerPackValidationException(
                    $"Dependency index row '{entity.RowKey}' is missing DependentName."
                ),
                DependentVersion = entity.GetString("DependentVersion") ?? throw new PowerPackValidationException(
                    $"Dependency index row '{entity.RowKey}' is missing DependentVersion."
                ),
                RequiredVersion = entity.GetString("RequiredVersion") ?? throw new PowerPackValidationException(
                    $"Dependency index row '{entity.RowKey}' is missing RequiredVersion."
                ),
            });
        }

        return [.. dependents
            .OrderBy(record => record.Dependent, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(record => SolutionVersion.Parse(record.DependentVersion))];
    }

    private async Task DeleteDependencyRowsAsync(SolutionManifest manifest, CancellationToken cancellationToken)
    {
        await _dependencyIndex.CreateIfNotExistsAsync(cancellationToken);

        foreach (var dependency in manifest.Dependencies)
            await _dependencyIndex.DeleteEntityAsync(
                GetSolutionPartitionKey(dependency.Key),
                $"{GetSolutionPartitionKey(manifest.Name)}|{manifest.Version}",
                ETag.All,
                cancellationToken
            );
    }

    private async Task ValidateStoredNameConsistencyAsync(string expectedName, CancellationToken cancellationToken)
    {
        var exactNames = new HashSet<string>(StringComparer.Ordinal)
        {
            expectedName,
        };

        await foreach (var entity in _solutionIndex.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeODataString(GetSolutionPartitionKey(expectedName))}'",
            select: ["Name"],
            cancellationToken: cancellationToken
        ))
        {
            var existingName = entity.GetString("Name");
            if (!string.IsNullOrWhiteSpace(existingName))
                exactNames.Add(existingName);
        }

        if (exactNames.Count > 1)
            throw new PowerPackValidationException(
                $"Manifest index contains solution names that differ only by case: {string.Join(", ", exactNames.OrderBy(name => name, StringComparer.Ordinal))}."
            );
    }

    private static SolutionManifest ParseManifest(TableEntity entity, string requestedName)
    {
        var manifestJson = entity.GetString("ManifestJson") ?? throw new PowerPackValidationException(
            $"Solution index row '{entity.RowKey}' is missing ManifestJson."
        );
        var manifest = JsonSerializer.Deserialize<SolutionManifest>(manifestJson, JsonSerializerOptions)
            ?? throw new PowerPackValidationException(
                $"Solution index row '{entity.RowKey}' contains invalid ManifestJson."
            );

        var normalized = ManifestNormalizer.Normalize(manifest);
        if (!normalized.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            throw new PowerPackValidationException(
                $"Requested solution '{requestedName}' does not match indexed solution '{normalized.Name}'."
            );

        return normalized;
    }

    private static ManifestPackageMetadata? ParsePackageMetadata(TableEntity entity)
    {
        var blobName = entity.GetString("PackageBlobName");
        var fileName = entity.GetString("PackageFileName");
        var contentType = entity.GetString("PackageContentType");
        var quality = entity.GetString("PackageQuality");
        if (string.IsNullOrWhiteSpace(blobName) ||
            string.IsNullOrWhiteSpace(fileName) ||
            string.IsNullOrWhiteSpace(contentType) ||
            string.IsNullOrWhiteSpace(quality))
            return null;

        return new ManifestPackageMetadata
        {
            BlobName = blobName,
            FileName = fileName,
            ContentType = contentType,
            ContentLength = entity.GetInt64("PackageContentLength") ?? throw new PowerPackValidationException(
                $"Solution index row '{entity.RowKey}' is missing PackageContentLength."
            ),
            Quality = quality,
        };
    }

    private static string GetSolutionPartitionKey(string name) => name.Trim().ToUpperInvariant();

    private static string EscapeODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
