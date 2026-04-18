using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PowerPack.Models;

public sealed class SolutionManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("dependencies")]
    public IDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("connections")]
    public JsonObject Connections { get; init; } = new();

    [JsonPropertyName("variables")]
    public JsonObject Variables { get; init; } = new();

    [JsonPropertyName("metadata")]
    public JsonObject? Metadata { get; init; }

    public SolutionVersion ParsedVersion => SolutionVersion.Parse(Version);
}

public sealed class SolutionReference
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class ValidateRequest
{
    [JsonPropertyName("dependencies")]
    public IDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
}

public sealed class ResolveSetRequest
{
    [JsonPropertyName("solutions")]
    public IList<SolutionReference> Solutions { get; init; } = [];
}

public sealed class ResolvedSolution
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("dependencies")]
    public IDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("manifest")]
    public required SolutionManifest Manifest { get; init; }

    [JsonPropertyName("package")]
    public ResolvedPackage? Package { get; init; }
}

public sealed class MissingRequirement
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("minimumVersion")]
    public required string MinimumVersion { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed class ResolutionResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("roots")]
    public IList<SolutionReference> Roots { get; init; } = [];

    [JsonPropertyName("constraints")]
    public IDictionary<string, string> Constraints { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("resolved")]
    public IList<ResolvedSolution> Resolved { get; init; } = [];

    [JsonPropertyName("missing")]
    public IList<MissingRequirement> Missing { get; init; } = [];

    [JsonPropertyName("invalid")]
    public IList<string> Invalid { get; init; } = [];
}

public sealed class ManifestSummary
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("dependencies")]
    public IDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
}

public sealed class DependentRecord
{
    [JsonPropertyName("dependency")]
    public required string Dependency { get; init; }

    [JsonPropertyName("dependent")]
    public required string Dependent { get; init; }

    [JsonPropertyName("dependentVersion")]
    public required string DependentVersion { get; init; }

    [JsonPropertyName("requiredVersion")]
    public required string RequiredVersion { get; init; }
}

public sealed class DependentsResponse
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("dependents")]
    public IList<DependentRecord> Dependents { get; init; } = [];
}
