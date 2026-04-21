using System.Reflection;
using System.Text.Json.Serialization;

namespace PowerPack.Models;

public sealed class SolutionEnvironmentRequirements
{
    [JsonPropertyName("dataverse")]
    public DataverseSolutionEnvironmentRequirements Dataverse { get; init; } = new();
}

public sealed class DataverseSolutionEnvironmentRequirements
{
    [JsonPropertyName("allowed_attachment_extensions")]
    public IList<string> AllowedAttachmentExtensions { get; init; } = [];
}

public sealed class DeploymentEnvironmentRequirements
{
    [JsonPropertyName("dataverse")]
    public DataverseDeploymentEnvironmentRequirements Dataverse { get; init; } = new();
}

public sealed class DataverseDeploymentEnvironmentRequirements
{
    [JsonPropertyName("default_blocked_attachment_extensions")]
    public IList<string> DefaultBlockedAttachmentExtensions { get; init; } = [];

    [JsonPropertyName("required_allowed_attachment_extensions")]
    public IList<string> RequiredAllowedAttachmentExtensions { get; init; } = [];

    [JsonPropertyName("blocked_attachment_extensions")]
    public IList<string> BlockedAttachmentExtensions { get; init; } = [];
}

public static class AttachmentExtensionPolicy
{
    private const string ResourceName = "PowerPack.default-blocked-attachment-extensions.txt";

    private static readonly Lazy<IReadOnlyList<string>> LazyDefaultBlockedAttachmentExtensions = new(LoadDefaultBlockedAttachmentExtensions);

    public static IReadOnlyList<string> DefaultBlockedAttachmentExtensions => LazyDefaultBlockedAttachmentExtensions.Value;

    public static IReadOnlySet<string> DefaultBlockedAttachmentExtensionSet =>
        LazyDefaultBlockedAttachmentExtensions.Value.ToHashSet(StringComparer.Ordinal);

    public static IList<string> NormalizeExtensions(IEnumerable<string>? values)
    {
        if (values is null)
            return [];

        return values
            .Select(NormalizeExtension)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    public static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().TrimStart('.').ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyList<string> LoadDefaultBlockedAttachmentExtensions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return NormalizeExtensions(content.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
    }
}
