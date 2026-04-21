using System.Text.RegularExpressions;
using PowerPack.Models;

namespace PowerPack.Services;

public sealed partial class MissingDependenciesParser
{
    [GeneratedRegex(@"(.+?)\s+\((\d+(?:\.\d+)*)\)$")]
    private static partial Regex DependencySolutionPattern();

    public IList<SolutionReference> Parse(string content, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int? requiredBlockIndent = null;
        string? requiredSolutionPath = null;
        var dependencies = new Dictionary<string, SolutionReference>(StringComparer.OrdinalIgnoreCase);

        void FlushRequiredBlock()
        {
            if (requiredBlockIndent is null)
                return;

            if (requiredSolutionPath is null)
                throw new PowerPackValidationException(
                    $"{sourceName} contains a Required block without an @solution value.");

            var rawSolution = requiredSolutionPath;
            if (rawSolution is "" or "Active")
            {
                throw new PowerPackValidationException(
                    $"{sourceName} contains Required.@solution with unsupported value '{rawSolution}'. Expected '<Name> (<Version)>'.");
            }

            var match = DependencySolutionPattern().Match(rawSolution);
            if (!match.Success)
            {
                throw new PowerPackValidationException(
                    $"{sourceName} contains Required.@solution with unsupported value '{rawSolution}'. Expected '<Name> (<Version)>'.");
            }

            var dependencyName = match.Groups[1].Value.Trim();
            if (dependencyName.Length == 0)
                throw new PowerPackValidationException(
                    $"{sourceName} contains Required.@solution with an empty dependency name.");

            var dependencyVersion = SolutionVersion.Parse(match.Groups[2].Value);
            if (dependencies.TryGetValue(dependencyName, out var existing))
            {
                if (!string.Equals(existing.Name, dependencyName, StringComparison.Ordinal))
                {
                    throw new PowerPackValidationException(
                        $"{sourceName} contains solution names that differ only by case: {existing.Name}, {dependencyName}.");
                }

                if (dependencyVersion.CompareTo(SolutionVersion.Parse(existing.Version!)) > 0)
                    dependencies[dependencyName] = new SolutionReference { Name = dependencyName, Version = dependencyVersion.ToString() };
            }
            else
            {
                dependencies[dependencyName] = new SolutionReference
                {
                    Name = dependencyName,
                    Version = dependencyVersion.ToString(),
                };
            }

            requiredBlockIndent = null;
            requiredSolutionPath = null;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index].TrimStart('\ufeff');
            var stripped = rawLine.Trim();
            var indent = rawLine.Length - rawLine.TrimStart(' ').Length;

            if (requiredBlockIndent is not null && stripped.Length > 0 && indent <= requiredBlockIndent.Value)
                FlushRequiredBlock();

            if (stripped is "Required:" or "- Required:")
            {
                FlushRequiredBlock();
                requiredBlockIndent = indent;
                requiredSolutionPath = null;
                continue;
            }

            if (requiredBlockIndent is null)
                continue;

            if (stripped.StartsWith("'@solution':", StringComparison.Ordinal))
            {
                if (requiredSolutionPath is not null)
                {
                    throw new PowerPackValidationException(
                        $"{sourceName} line {index + 1} declares multiple @solution values in one Required block.");
                }

                var separatorIndex = stripped.IndexOf(':', StringComparison.Ordinal);
                requiredSolutionPath = CanonicalizeScalar(stripped[(separatorIndex + 1)..].Trim());
            }
        }

        FlushRequiredBlock();

        return dependencies.Values
            .OrderBy(solution => solution.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CanonicalizeScalar(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
            return string.Empty;

        if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);

        return value;
    }
}
