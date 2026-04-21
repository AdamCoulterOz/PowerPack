using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PowerPack.Models;

public sealed class DependencyDeploymentGraph
{
    [JsonPropertyName("roots")]
    public IList<SolutionReference> Roots { get; init; } = [];

    [JsonPropertyName("topological_order")]
    public IList<string> TopologicalOrder { get; init; } = [];

    [JsonPropertyName("nodes")]
    public IDictionary<string, DependencyDeploymentNode> Nodes { get; init; } = new Dictionary<string, DependencyDeploymentNode>(StringComparer.Ordinal);
}

public sealed class DependencyDeploymentNode
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("solution_unique_name")]
    public required string SolutionUniqueName { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("solution_package_version")]
    public required string SolutionPackageVersion { get; init; }

    [JsonPropertyName("package_transport_name")]
    public required string PackageTransportName { get; init; }

    [JsonPropertyName("package_transport_version")]
    public required string PackageTransportVersion { get; init; }

    [JsonPropertyName("download_url")]
    public required string DownloadUrl { get; init; }

    [JsonPropertyName("dependencies")]
    public IList<SolutionReference> Dependencies { get; init; } = [];

    [JsonPropertyName("identities")]
    public IDictionary<string, DeploymentIdentity> Identities { get; init; } = new Dictionary<string, DeploymentIdentity>(StringComparer.Ordinal);

    [JsonPropertyName("connection_references")]
    public IDictionary<string, DeploymentConnectionReference> ConnectionReferences { get; init; } = new Dictionary<string, DeploymentConnectionReference>(StringComparer.Ordinal);

    [JsonPropertyName("environment_variables")]
    public IDictionary<string, DeploymentEnvironmentVariable> EnvironmentVariables { get; init; } = new Dictionary<string, DeploymentEnvironmentVariable>(StringComparer.Ordinal);
}

public sealed class DeploymentIdentity
{
    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("dataverse_security_roles")]
    public IList<string> DataverseSecurityRoles { get; init; } = [];

    [JsonPropertyName("required_permissions")]
    public IList<DeploymentPermission> RequiredPermissions { get; init; } = [];
}

public sealed class DeploymentPermission
{
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class DeploymentConnectionReference
{
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("connector_name")]
    public required string ConnectorName { get; init; }

    [JsonPropertyName("connection_type")]
    public required string ConnectionType { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("auth_mode")]
    public required string AuthMode { get; init; }

    [JsonPropertyName("identity")]
    public string? Identity { get; init; }

    [JsonPropertyName("connection_parameters_set")]
    public DeploymentConnectionParametersSet? ConnectionParametersSet { get; init; }
}

public sealed class DeploymentConnectionParametersSet
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("values")]
    public required JsonObject Values { get; init; }
}

public sealed class DeploymentEnvironmentVariable
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("default_value")]
    public JsonNode? DefaultValue { get; init; }

    [JsonPropertyName("string_value")]
    public required string StringValue { get; init; }
}
