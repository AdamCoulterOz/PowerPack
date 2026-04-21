using System.Text.Json;
using System.Text.Json.Nodes;
using PowerPack.Models;

namespace PowerPack.Services;

public sealed class DependencyDeploymentGraphBuilder
{
    private const string PackageServiceIdentityName = "default";

    public DependencyDeploymentGraph Build(
        ResolutionResult resolution,
        IEnumerable<string>? sourceAllowedAttachmentExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.Missing.Count > 0)
        {
            throw new PowerPackValidationException(
                "PowerPack dependency resolution returned missing requirements: " +
                string.Join(
                    "; ",
                    resolution.Missing.Select(item => $"{item.Name} >= {item.MinimumVersion} ({item.Reason})")));
        }

        if (resolution.Invalid.Count > 0)
        {
            throw new PowerPackValidationException(
                "PowerPack dependency resolution returned invalid results: " +
                string.Join("; ", resolution.Invalid));
        }

        var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolved in resolution.Resolved)
        {
            if (canonicalNames.TryGetValue(resolved.Name, out var existingName) &&
                !string.Equals(existingName, resolved.Name, StringComparison.Ordinal))
            {
                throw new PowerPackValidationException(
                    $"PowerPack resolution returned solution names that differ only by case: {existingName}, {resolved.Name}.");
            }

            canonicalNames[resolved.Name] = resolved.Name;
        }

        var graph = new DependencyDeploymentGraph
        {
            Roots = resolution.Roots
                .Select(solution => new SolutionReference
                {
                    Name = canonicalNames.GetValueOrDefault(solution.Name, solution.Name),
                    Version = solution.Version,
                })
                .ToList(),
        };
        var requiredAllowedAttachmentExtensions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resolved in resolution.Resolved)
        {
            var packageName = RequireNonEmpty(resolved.Name, "resolution.resolved[].name");
            var packageVersion = SolutionVersion.Parse(RequireNonEmpty(resolved.Version, $"resolution.resolved[{packageName}].version")).ToString();
            var manifest = resolved.Manifest ?? throw new PowerPackValidationException(
                $"PowerPack resolution entry '{packageName}' is missing manifest data.");

            var manifestName = RequireNonEmpty(manifest.Name, $"resolution.resolved[{packageName}].manifest.name");
            var manifestVersion = SolutionVersion.Parse(RequireNonEmpty(manifest.Version, $"resolution.resolved[{packageName}].manifest.version")).ToString();
            if (!string.Equals(manifestName, packageName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifestVersion, packageVersion, StringComparison.Ordinal))
            {
                throw new PowerPackValidationException(
                    $"PowerPack resolution returned mismatched manifest payload for '{packageName}' version '{packageVersion}'.");
            }

            if (resolved.Package is null)
            {
                throw new PowerPackValidationException(
                    $"Resolved package '{packageName}' version '{packageVersion}' is missing package metadata.");
            }

            var projectedManifest = ProjectManifest(manifest, canonicalNames);
            foreach (var extension in projectedManifest.EnvironmentRequirements.Dataverse.AllowedAttachmentExtensions)
                requiredAllowedAttachmentExtensions.Add(extension);

            graph.TopologicalOrder.Add(packageName);
            graph.Nodes[packageName] = new DependencyDeploymentNode
            {
                Name = packageName,
                SolutionUniqueName = projectedManifest.Name,
                Publisher = RequireNonEmpty(manifest.Publisher, $"{packageName}.publisher"),
                Version = packageVersion,
                SolutionPackageVersion = projectedManifest.SolutionPackageVersion,
                PackageTransportName = projectedManifest.PackageTransportName,
                PackageTransportVersion = projectedManifest.PackageTransportVersion,
                DownloadUrl = RequireNonEmpty(resolved.Package.DownloadUrl, $"{packageName}.package.downloadUrl"),
                Dependencies = projectedManifest.Dependencies,
                Identities = projectedManifest.Identities,
                ConnectionReferences = projectedManifest.ConnectionReferences,
                EnvironmentVariables = projectedManifest.EnvironmentVariables,
                EnvironmentRequirements = projectedManifest.EnvironmentRequirements,
            };
        }

        foreach (var extension in AttachmentExtensionPolicy.NormalizeExtensions(sourceAllowedAttachmentExtensions))
            requiredAllowedAttachmentExtensions.Add(extension);

        graph.EnvironmentRequirements = new DeploymentEnvironmentRequirements
        {
            Dataverse = new DataverseDeploymentEnvironmentRequirements
            {
                DefaultBlockedAttachmentExtensions = AttachmentExtensionPolicy.DefaultBlockedAttachmentExtensions.ToList(),
                RequiredAllowedAttachmentExtensions = AttachmentExtensionPolicy.NormalizeExtensions(requiredAllowedAttachmentExtensions),
                BlockedAttachmentExtensions = AttachmentExtensionPolicy.NormalizeExtensions(
                    AttachmentExtensionPolicy.DefaultBlockedAttachmentExtensions.Except(
                        requiredAllowedAttachmentExtensions,
                        StringComparer.Ordinal)),
            },
        };

        return graph;
    }

    private static ProjectedManifest ProjectManifest(
        SolutionManifest manifest,
        IReadOnlyDictionary<string, string> canonicalNames)
    {
        var packageName = RequireNonEmpty(manifest.Name, "manifest.name");
        var metadata = manifest.Metadata ?? throw new PowerPackValidationException($"{packageName}.metadata must be an object.");

        var packageTransportName = RequireNonEmpty(
            metadata["package_name"]?.GetValue<string>(),
            $"{packageName}.metadata.package_name");
        var solutionPackageVersion = RequireNonEmpty(
            metadata["solution_package_version"]?.GetValue<string>(),
            $"{packageName}.metadata.solution_package_version");
        var packageTransportVersion = RequireNonEmpty(
            metadata["package_version"]?.GetValue<string>(),
            $"{packageName}.metadata.package_version");

        var dependencies = new List<SolutionReference>();
        foreach (var entry in manifest.Dependencies.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!canonicalNames.TryGetValue(entry.Key, out var canonicalDependencyName))
                continue;

            dependencies.Add(new SolutionReference
            {
                Name = canonicalDependencyName,
                Version = SolutionVersion.Parse(entry.Value).ToString(),
            });
        }

        var packageServiceRoles = new List<string>();
        var packageServiceApplicationPermissions = new List<DeploymentPermission>();
        var hasServiceConnection = false;
        var connectionReferences = new Dictionary<string, DeploymentConnectionReference>(StringComparer.Ordinal);

        foreach (var logicalName in manifest.Connections.Select(entry => entry.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            var connectionObject = manifest.Connections[logicalName] as JsonObject
                ?? throw new PowerPackValidationException($"{packageName}.connections.{logicalName} must be an object.");

            var auth = RequireNonEmpty(
                connectionObject["auth"]?.GetValue<string>(),
                $"{packageName}.connections.{logicalName}.auth");
            if (auth is not ("service" or "user" or "none"))
            {
                throw new PowerPackValidationException(
                    $"Package '{packageName}' declares unsupported auth '{auth}' for connection reference '{logicalName}'.");
            }

            var roles = ReadStringList(
                connectionObject["roles"],
                $"{packageName}.connections.{logicalName}.roles");

            var applicationPermissions = new List<DeploymentPermission>();
            var permissionsNode = connectionObject["permissions"];
            if (permissionsNode is not null)
            {
                var permissionsArray = permissionsNode as JsonArray
                    ?? throw new PowerPackValidationException($"{packageName}.connections.{logicalName}.permissions must be an array.");
                for (var index = 0; index < permissionsArray.Count; index++)
                {
                    var permissionObject = permissionsArray[index] as JsonObject
                        ?? throw new PowerPackValidationException(
                            $"{packageName}.connections.{logicalName}.permissions[{index}] must be an object.");

                    var permissionType = RequireNonEmpty(
                        permissionObject["type"]?.GetValue<string>(),
                        $"{packageName}.connections.{logicalName}.permissions[{index}].type");

                    if (auth == "service" && string.Equals(permissionType, "application", StringComparison.Ordinal))
                    {
                        applicationPermissions.Add(new DeploymentPermission
                        {
                            Resource = RequireNonEmpty(
                                permissionObject["resource"]?.GetValue<string>(),
                                $"{packageName}.connections.{logicalName}.permissions[{index}].resource"),
                            Type = permissionType,
                            Name = RequireNonEmpty(
                                permissionObject["name"]?.GetValue<string>(),
                                $"{packageName}.connections.{logicalName}.permissions[{index}].name"),
                        });
                    }
                }
            }

            var authMode = auth switch
            {
                "service" => "service_principal",
                "user" => "interactive",
                _ => "none",
            };

            string? resolvedIdentityName = authMode == "service_principal" ? PackageServiceIdentityName : null;
            if (resolvedIdentityName is not null)
            {
                hasServiceConnection = true;
                packageServiceRoles.AddRange(roles);
                packageServiceApplicationPermissions.AddRange(applicationPermissions);
            }

            var connectorId = RequireNonEmpty(
                connectionObject["type"]?.GetValue<string>(),
                $"{packageName}.connections.{logicalName}.type");

            DeploymentConnectionParametersSet? connectionParametersSet = null;
            var connectionReference = new DeploymentConnectionReference
            {
                Mode = "create",
                ConnectorName = ConnectorNameFromConnectorId(
                    connectorId,
                    $"{packageName}.connections.{logicalName}.type"),
                ConnectionType = connectorId,
                DisplayName = $"{packageName} {logicalName}",
                AuthMode = authMode,
                Identity = resolvedIdentityName,
                ConnectionParametersSet = null,
            };

            var connectionDefinition = connectionObject["connection"];
            var connectionParametersSetDefinition = connectionObject["connection_parameters_set"];

            if (authMode == "service_principal")
            {
                if (connectionDefinition is not JsonObject)
                {
                    throw new PowerPackValidationException(
                        $"Package '{packageName}' connection reference '{logicalName}' uses auth 'service' but does not declare a [connection] block.");
                }

                var parameterSetObject = connectionParametersSetDefinition as JsonObject
                    ?? throw new PowerPackValidationException(
                        $"Package '{packageName}' connection reference '{logicalName}' is missing the generated connection_parameters_set metadata.");

                var method = RequireNonEmpty(
                    parameterSetObject["name"]?.GetValue<string>(),
                    $"{packageName}.connections.{logicalName}.connection_parameters_set.name");
                var valuesObject = parameterSetObject["values"] as JsonObject
                    ?? throw new PowerPackValidationException(
                        $"{packageName}.connections.{logicalName}.connection_parameters_set.values must be an object.");

                var resolvedValues = new JsonObject();
                foreach (var parameterName in valuesObject.Select(entry => entry.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
                {
                    var parameterValueObject = valuesObject[parameterName] as JsonObject
                        ?? throw new PowerPackValidationException(
                            $"{packageName}.connections.{logicalName}.connection_parameters_set.values.{parameterName} must be an object.");
                    if (!parameterValueObject.TryGetPropertyValue("value", out var parameterValueNode))
                    {
                        throw new PowerPackValidationException(
                            $"Package '{packageName}' connection reference '{logicalName}' parameter '{parameterName}' is missing required field 'value'.");
                    }

                    var unexpectedKeys = parameterValueObject
                        .Select(entry => entry.Key)
                        .Where(key => key != "value")
                        .OrderBy(key => key, StringComparer.Ordinal)
                        .ToList();
                    if (unexpectedKeys.Count > 0)
                    {
                        throw new PowerPackValidationException(
                            $"Package '{packageName}' connection reference '{logicalName}' parameter '{parameterName}' contains unsupported fields: {string.Join(", ", unexpectedKeys)}.");
                    }

                    resolvedValues[parameterName] = parameterValueNode?.DeepClone();
                }

                connectionParametersSet = new DeploymentConnectionParametersSet
                {
                    Name = method,
                    Values = resolvedValues,
                };
            }
            else if (connectionDefinition is not null)
            {
                throw new PowerPackValidationException(
                    $"Package '{packageName}' connection reference '{logicalName}' declares a [connection] block, but auth '{auth}' does not support Terraform-populated connection parameters.");
            }

            connectionReference = new DeploymentConnectionReference
            {
                Mode = connectionReference.Mode,
                ConnectorName = connectionReference.ConnectorName,
                ConnectionType = connectionReference.ConnectionType,
                DisplayName = connectionReference.DisplayName,
                AuthMode = connectionReference.AuthMode,
                Identity = connectionReference.Identity,
                ConnectionParametersSet = connectionParametersSet,
            };

            connectionReferences[logicalName] = connectionReference;
        }

        var identities = new Dictionary<string, DeploymentIdentity>(StringComparer.Ordinal);
        if (hasServiceConnection)
        {
            identities[PackageServiceIdentityName] = new DeploymentIdentity
            {
                DisplayName = $"{packageName} service identity",
                DataverseSecurityRoles = DedupeStringList(packageServiceRoles),
                RequiredPermissions = DedupePermissions(packageServiceApplicationPermissions),
            };
        }

        var environmentVariables = new Dictionary<string, DeploymentEnvironmentVariable>(StringComparer.Ordinal);
        foreach (var schemaName in manifest.Variables.Select(entry => entry.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            var variableObject = manifest.Variables[schemaName] as JsonObject
                ?? throw new PowerPackValidationException($"{packageName}.variables.{schemaName} must be an object.");
            var variableType = RequireNonEmpty(
                variableObject["type"]?.GetValue<string>(),
                $"{packageName}.variables.{schemaName}.type");
            variableObject.TryGetPropertyValue("default", out var defaultValue);
            environmentVariables[schemaName] = new DeploymentEnvironmentVariable
            {
                Type = variableType,
                DefaultValue = defaultValue?.DeepClone(),
                StringValue = defaultValue is null
                    ? string.Empty
                    : StringifyEnvironmentValue(defaultValue, variableType, $"{packageName}.variables.{schemaName}.default"),
            };
        }

        var environmentRequirements = new SolutionEnvironmentRequirements
        {
            Dataverse = new DataverseSolutionEnvironmentRequirements
            {
                AllowedAttachmentExtensions = AttachmentExtensionPolicy.NormalizeExtensions(
                    manifest.EnvironmentRequirements.Dataverse.AllowedAttachmentExtensions),
            },
        };

        return new ProjectedManifest
        {
            Name = packageName,
            PackageTransportName = packageTransportName,
            SolutionPackageVersion = solutionPackageVersion,
            PackageTransportVersion = packageTransportVersion,
            Dependencies = dependencies,
            Identities = identities,
            ConnectionReferences = connectionReferences,
            EnvironmentVariables = environmentVariables,
            EnvironmentRequirements = environmentRequirements,
        };
    }

    private static IList<string> ReadStringList(JsonNode? node, string path)
    {
        if (node is null)
            return [];

        var array = node as JsonArray ?? throw new PowerPackValidationException($"{path} must be an array.");
        var values = new List<string>(array.Count);
        for (var index = 0; index < array.Count; index++)
            values.Add(RequireNonEmpty(array[index]?.GetValue<string>(), $"{path}[{index}]"));
        return values;
    }

    private static IList<string> DedupeStringList(IEnumerable<string> values)
    {
        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (seen.Add(value))
                deduped.Add(value);
        }

        return deduped;
    }

    private static IList<DeploymentPermission> DedupePermissions(IEnumerable<DeploymentPermission> values)
    {
        var deduped = new List<DeploymentPermission>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = $"{value.Resource}|{value.Type}|{value.Name}";
            if (seen.Add(key))
                deduped.Add(value);
        }

        return deduped;
    }

    private static string StringifyEnvironmentValue(JsonNode value, string variableType, string path)
    {
        return variableType switch
        {
            "text" or "datasource-sharepoint" or "datasource-sqlserver" or "datasource-dataverse" or "datasource-sap" or "secret-azurekeyvault"
                => value.GetValue<string>()?.Trim() is { Length: > 0 } stringValue
                    ? stringValue
                    : throw new PowerPackValidationException($"{path} must be a non-empty string."),
            "decimal" => value is JsonValue jsonValue && jsonValue.TryGetValue<decimal>(out var decimalValue)
                ? decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : throw new PowerPackValidationException($"{path} must be a number for variable type '{variableType}'."),
            "boolean" => value is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out var boolValue)
                ? (boolValue ? "true" : "false")
                : throw new PowerPackValidationException($"{path} must be a boolean for variable type '{variableType}'."),
            "json" => CanonicalizeJson(value).ToJsonString(),
            _ => throw new PowerPackValidationException($"{path} uses unsupported variable type '{variableType}'."),
        };
    }

    private static JsonNode CanonicalizeJson(JsonNode node) =>
        node switch
        {
            JsonObject jsonObject => new JsonObject(
                jsonObject
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => KeyValuePair.Create(entry.Key, entry.Value is null ? null : CanonicalizeJson(entry.Value)))),
            JsonArray jsonArray => new JsonArray(jsonArray.Select(item => item is null ? null : CanonicalizeJson(item)).ToArray()),
            _ => node.DeepClone(),
        };

    private static string ConnectorNameFromConnectorId(string connectorId, string path)
    {
        const string prefix = "/providers/Microsoft.PowerApps/apis/";
        if (!connectorId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new PowerPackValidationException(
                $"{path} must use a connector id in the form '/providers/Microsoft.PowerApps/apis/<name>'.");
        }

        var connectorName = connectorId[prefix.Length..];
        if (connectorName.Length == 0)
        {
            throw new PowerPackValidationException(
                $"{path} must use a connector id in the form '/providers/Microsoft.PowerApps/apis/<name>'.");
        }

        return connectorName;
    }

    private static string RequireNonEmpty(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PowerPackValidationException($"{path} must be a non-empty string.");
        return value.Trim();
    }

    private sealed class ProjectedManifest
    {
        public required string Name { get; init; }

        public required string PackageTransportName { get; init; }

        public required string SolutionPackageVersion { get; init; }

        public required string PackageTransportVersion { get; init; }

        public IList<SolutionReference> Dependencies { get; init; } = [];

        public IDictionary<string, DeploymentIdentity> Identities { get; init; } = new Dictionary<string, DeploymentIdentity>(StringComparer.Ordinal);

        public IDictionary<string, DeploymentConnectionReference> ConnectionReferences { get; init; } = new Dictionary<string, DeploymentConnectionReference>(StringComparer.Ordinal);

        public IDictionary<string, DeploymentEnvironmentVariable> EnvironmentVariables { get; init; } = new Dictionary<string, DeploymentEnvironmentVariable>(StringComparer.Ordinal);

        public SolutionEnvironmentRequirements EnvironmentRequirements { get; init; } = new();
    }
}
