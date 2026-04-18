namespace PowerPack.TestFixtures;

public sealed record SolutionPackageFixture(
    string Name,
    string Version,
    string Publisher,
    IReadOnlyList<SolutionDependencyFixture> Dependencies,
    IReadOnlyList<ConnectionReferenceFixture> Connections
);

public sealed record SolutionDependencyFixture(string Name, string MinimumVersion);

public sealed record ConnectionReferenceFixture(
    string LogicalName,
    string ConnectorId,
    string DisplayName,
    string Description
);
