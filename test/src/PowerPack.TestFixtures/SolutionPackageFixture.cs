namespace PowerPack.TestFixtures;

public sealed record SolutionPackageFixture(
    string Name,
    string Version,
    string Publisher,
    IReadOnlyList<SolutionDependencyFixture> Dependencies,
    IReadOnlyList<ConnectionReferenceFixture> Connections,
    IReadOnlyList<FlowFixture>? Flows = null
);

public sealed record SolutionDependencyFixture(string Name, string MinimumVersion);

public sealed record ConnectionReferenceFixture(
    string LogicalName,
    string ConnectorId,
    string DisplayName,
    string Description
);

public sealed record FlowFixture(
    string WorkflowId,
    string Name,
    int StateCode = 1,
    int StatusCode = 2,
    int Category = 5
);
