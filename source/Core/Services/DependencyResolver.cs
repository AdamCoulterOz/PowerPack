using PowerPack.Models;
using PowerPack.Storage;

namespace PowerPack.Services;

public sealed class DependencyResolver(IManifestIndexStore store)
{
    private readonly IManifestIndexStore _store = store;
    private readonly BuiltInSolutionRegistry _builtInSolutions = BuiltInSolutionRegistry.Default;

	public Task<ResolutionResult> ResolveAsync(SolutionReference solution, CancellationToken cancellationToken) =>
        ResolveSetAsync(new ResolveSetRequest { Solutions = [solution] }, cancellationToken);

    public async Task<ResolutionResult> ResolveSetAsync(ResolveSetRequest request, CancellationToken cancellationToken)
    {
        if (request.Solutions.Count == 0)
            throw new PowerPackValidationException("resolve-set requires at least one solution.");

        var effectiveRoots = request.Solutions
            .Where(solution => !_builtInSolutions.Contains(solution.Name))
            .ToList();

        if (effectiveRoots.Count == 0)
        {
            return new ResolutionResult
            {
                Status = "resolved",
                Roots = [],
                Constraints = new Dictionary<string, string>(StringComparer.Ordinal),
                Resolved = [],
                Missing = [],
                Invalid = [],
            };
        }

        var rootConstraints = MergeReferences(effectiveRoots);
        var selected = new Dictionary<string, SolutionManifest>(StringComparer.OrdinalIgnoreCase);
        var missing = new Dictionary<string, MissingRequirement>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();

        Dictionary<string, ConstraintState> constraints = rootConstraints;

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var previousConstraintSignature = Signature(constraints);
            var previousSelectionSignature = Signature(selected);

            selected = new Dictionary<string, SolutionManifest>(StringComparer.OrdinalIgnoreCase);
            missing = new Dictionary<string, MissingRequirement>(StringComparer.OrdinalIgnoreCase);

            foreach (var constraint in constraints.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var candidates = await _store.ListManifestsAsync(constraint.Name, cancellationToken);
                var match = candidates
                    .Where(candidate => candidate.ParsedVersion.CompareTo(constraint.MinimumVersion) >= 0)
                    .OrderByDescending(candidate => candidate.ParsedVersion)
                    .FirstOrDefault();

                if (match is null)
                {
                    missing[constraint.Name] = new MissingRequirement
                    {
                        Name = constraint.Name,
                        MinimumVersion = constraint.MinimumVersion.ToString(),
                        Reason = "No indexed manifest satisfies the minimum required version.",
                    };
                    continue;
                }

                selected[match.Name] = match;
            }

            var expandedConstraints = CloneConstraints(rootConstraints);
            foreach (var manifest in selected.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var dependency in manifest.Dependencies)
                {
                    if (_builtInSolutions.Contains(dependency.Key))
                        continue;

                    MergeConstraint(
                        expandedConstraints,
                        dependency.Key,
                        SolutionVersion.Parse(dependency.Value)
                    );
                }

            constraints = expandedConstraints;

            if (previousConstraintSignature == Signature(constraints) &&
                previousSelectionSignature == Signature(selected))
                break;

            if (iteration == 99)
                invalid.Add("Resolution did not converge after 100 iterations.");
        }

        var ordered = TopologicallyOrder(selected, invalid);

        return new ResolutionResult
        {
            Status = missing.Count == 0 && invalid.Count == 0 ? "resolved" : "unresolved",
            Roots = [.. effectiveRoots
                .Select(solution => new SolutionReference
                {
                    Name = solution.Name.Trim(),
                    Version = solution.Version is null ? null : SolutionVersion.Parse(solution.Version).ToString(),
                })],
            Constraints = constraints.Values
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(value => value.Name, value => value.MinimumVersion.ToString(), StringComparer.Ordinal),
            Resolved = [.. ordered.Select(manifest => new ResolvedSolution
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Publisher = manifest.Publisher,
                Dependencies = new Dictionary<string, string>(manifest.Dependencies, StringComparer.Ordinal),
                Manifest = manifest,
            })],
            Missing = [.. missing.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)],
            Invalid = invalid,
        };
    }

    public async Task<ResolutionResult> ValidateAsync(ValidateRequest request, CancellationToken cancellationToken)
    {
        var constraints = new Dictionary<string, ConstraintState>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in request.Dependencies)
        {
            if (_builtInSolutions.Contains(dependency.Key))
                continue;

            MergeConstraint(constraints, dependency.Key, SolutionVersion.Parse(dependency.Value));
        }

        var resolved = new List<ResolvedSolution>();
        var missing = new List<MissingRequirement>();
        foreach (var constraint in constraints.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = await _store.ListManifestsAsync(constraint.Name, cancellationToken);
            var match = candidates
                .Where(candidate => candidate.ParsedVersion.CompareTo(constraint.MinimumVersion) >= 0)
                .OrderByDescending(candidate => candidate.ParsedVersion)
                .FirstOrDefault();

            if (match is null)
            {
                missing.Add(new MissingRequirement
                {
                    Name = constraint.Name,
                    MinimumVersion = constraint.MinimumVersion.ToString(),
                    Reason = "No indexed manifest satisfies the minimum required version.",
                });
                continue;
            }

            resolved.Add(new ResolvedSolution
            {
                Name = match.Name,
                Version = match.Version,
                Publisher = match.Publisher,
                Dependencies = new Dictionary<string, string>(match.Dependencies, StringComparer.Ordinal),
                Manifest = match,
            });
        }

        return new ResolutionResult
        {
            Status = missing.Count == 0 ? "resolved" : "unresolved",
            Roots = [],
            Constraints = constraints.Values
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(value => value.Name, value => value.MinimumVersion.ToString(), StringComparer.Ordinal),
            Resolved = resolved,
            Missing = missing,
            Invalid = [],
        };
    }

    public async Task<DependentsResponse> GetDependentsAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PowerPackValidationException("Dependents lookup requires a non-empty solution name.");

        var dependents = await _store.GetDependentsAsync(name.Trim(), cancellationToken);
        return new DependentsResponse
        {
            Name = name.Trim(),
            Dependents = [.. dependents],
        };
    }

    private static Dictionary<string, ConstraintState> MergeReferences(IEnumerable<SolutionReference> solutions)
    {
        var constraints = new Dictionary<string, ConstraintState>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in solutions)
        {
            var name = solution.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new PowerPackValidationException("Every solution reference requires a non-empty name.");

            var minimumVersion = string.IsNullOrWhiteSpace(solution.Version)
                ? new SolutionVersion(0, 0, 0, 0)
                : SolutionVersion.Parse(solution.Version);

            MergeConstraint(constraints, name, minimumVersion);
        }

        return constraints;
    }

    private static void MergeConstraint(
        IDictionary<string, ConstraintState> constraints,
        string name,
        SolutionVersion minimumVersion)
    {
        if (constraints.TryGetValue(name, out var existing))
        {
            if (minimumVersion.CompareTo(existing.MinimumVersion) > 0)
                constraints[name] = existing with { MinimumVersion = minimumVersion };

            return;
        }

        constraints[name] = new ConstraintState(name.Trim(), minimumVersion);
    }

    private static Dictionary<string, ConstraintState> CloneConstraints(
        IReadOnlyDictionary<string, ConstraintState> source) =>
        source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase
        );

    private static string Signature(IReadOnlyDictionary<string, ConstraintState> constraints) =>
        string.Join(
            ";",
            constraints.Values
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"{value.Name}={value.MinimumVersion}")
        );

    private static string Signature(IReadOnlyDictionary<string, SolutionManifest> manifests) =>
        string.Join(
            ";",
            manifests.Values
                .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"{value.Name}={value.Version}")
        );

    private static IList<SolutionManifest> TopologicallyOrder(
        IReadOnlyDictionary<string, SolutionManifest> selected,
        IList<string> invalid)
    {
        var ordered = new List<SolutionManifest>();
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in selected.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            Visit(manifest, selected, state, ordered, invalid);

        return ordered;
    }

    private static void Visit(
        SolutionManifest manifest,
        IReadOnlyDictionary<string, SolutionManifest> selected,
        IDictionary<string, string> state,
        IList<SolutionManifest> ordered,
        IList<string> invalid)
    {
        if (state.TryGetValue(manifest.Name, out var current))
        {
            if (current == "resolved")
                return;

            if (current == "visiting")
            {
                invalid.Add($"Dependency cycle detected involving solution '{manifest.Name}'.");
                return;
            }
        }

        state[manifest.Name] = "visiting";
        foreach (var dependencyName in manifest.Dependencies.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.TryGetValue(dependencyName, out var dependency))
                Visit(dependency, selected, state, ordered, invalid);
        }

        state[manifest.Name] = "resolved";
        if (!ordered.Any(item => item.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)))
            ordered.Add(manifest);
    }

    private sealed record ConstraintState(string Name, SolutionVersion MinimumVersion);
}
