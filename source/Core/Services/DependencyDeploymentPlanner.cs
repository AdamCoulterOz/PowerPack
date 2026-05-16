using PowerPack.Models;

namespace PowerPack.Services;

public static class DependencyDeploymentPlanner
{
    public static IReadOnlyList<string> GetDependencyFirstOrder(DependencyDeploymentGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var preferredIndex = graph.TopologicalOrder
            .Select((packageName, index) => (packageName, index))
            .ToDictionary(item => item.packageName, item => item.index, StringComparer.Ordinal);
        var remainingDependencyCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentsByPackage = graph.Nodes.Keys.ToDictionary(
            packageName => packageName,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var (packageName, node) in graph.Nodes)
        {
            var dependencies = node.Dependencies
                .Select(dependency => dependency.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var dependencyName in dependencies)
            {
                if (!graph.Nodes.ContainsKey(dependencyName))
                    throw new PowerPackValidationException(
                        $"Resolved graph dependency '{dependencyName}' referenced by '{packageName}' was not present in nodes.");

                dependentsByPackage[dependencyName].Add(packageName);
            }

            remainingDependencyCount[packageName] = dependencies.Count;
        }

        var readyPackages = remainingDependencyCount
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key)
            .OrderBy(packageName => preferredIndex.GetValueOrDefault(packageName, int.MaxValue))
            .ToList();
        var installOrder = new List<string>();

        while (readyPackages.Count > 0)
        {
            var packageName = readyPackages[0];
            readyPackages.RemoveAt(0);
            installOrder.Add(packageName);

            foreach (var dependentName in dependentsByPackage[packageName]
                         .OrderBy(value => preferredIndex.GetValueOrDefault(value, int.MaxValue)))
            {
                remainingDependencyCount[dependentName] -= 1;
                if (remainingDependencyCount[dependentName] == 0)
                {
                    readyPackages.Add(dependentName);
                    readyPackages = readyPackages
                        .OrderBy(value => preferredIndex.GetValueOrDefault(value, int.MaxValue))
                        .ToList();
                }
            }
        }

        if (installOrder.Count != graph.Nodes.Count)
        {
            var unresolvedPackages = remainingDependencyCount
                .Where(entry => entry.Value > 0)
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal);
            throw new PowerPackValidationException(
                "Resolved graph dependencies contain a cycle or unresolved references: " +
                string.Join(", ", unresolvedPackages));
        }

        return installOrder;
    }
}
