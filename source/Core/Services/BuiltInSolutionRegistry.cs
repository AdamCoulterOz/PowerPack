using System.Reflection;
using System.Text.Json;

namespace PowerPack.Services;

public sealed class BuiltInSolutionRegistry
{
    private const string ResourceName = "PowerPack.built-in-solutions.json";

    private readonly HashSet<string> _solutionNames;

    private BuiltInSolutionRegistry(HashSet<string> solutionNames)
    {
        _solutionNames = solutionNames;
    }

    public static BuiltInSolutionRegistry Default { get; } = LoadDefault();

    public bool Contains(string solutionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionName);
        return _solutionNames.Contains(solutionName.Trim());
    }

    private static BuiltInSolutionRegistry LoadDefault()
    {
        var assembly = typeof(BuiltInSolutionRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' was not found in assembly '{assembly.GetName().Name}'.");

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("solution_unique_names", out var solutionNamesElement) ||
            solutionNamesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' must contain a solution_unique_names array.");
        }

        var indexedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in solutionNamesElement.EnumerateArray())
        {
            var solutionName = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(solutionName))
                throw new InvalidOperationException(
                    $"Embedded resource '{ResourceName}' contains an empty solution name.");

            if (indexedNames.TryGetValue(solutionName, out var existingName) &&
                !string.Equals(existingName, solutionName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Embedded resource '{ResourceName}' contains solution names that differ only by case: " +
                    $"{existingName}, {solutionName}.");
            }

            indexedNames[solutionName] = solutionName;
        }

        return new BuiltInSolutionRegistry(
            new HashSet<string>(indexedNames.Keys, StringComparer.OrdinalIgnoreCase));
    }
}
