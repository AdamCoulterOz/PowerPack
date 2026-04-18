using PowerPack.Models;
using PowerPack.Services;
using PowerPack.Storage;

namespace PowerPack.Tests;

public sealed class DependencyResolverTests
{
    [Fact]
    public void SolutionVersion_Normalizes_MissingSegments()
    {
        Assert.Equal("1.0.0.0", SolutionVersion.Parse("1").ToString());
        Assert.Equal("1.2.0.0", SolutionVersion.Parse("1.2").ToString());
        Assert.Equal("1.2.3.0", SolutionVersion.Parse("1.2.3").ToString());
        Assert.Equal("1.2.3.4", SolutionVersion.Parse("1.2.3.4").ToString());
    }

    [Fact]
    public async Task ResolveSet_Picks_HighestAvailableVersion_ThatSatisfiesMinimums()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("Core", "1.0.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest("Core", "1.2.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "MetaForm",
            "2.0.0.0",
            new Dictionary<string, string> { ["Core"] = "1.1.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "MetaForm" }, default);

        Assert.Equal("resolved", result.Status);
        Assert.Collection(
            result.Resolved,
            core =>
            {
                Assert.Equal("Core", core.Name);
                Assert.Equal("1.2.0.0", core.Version);
                Assert.Equal("Core", core.Manifest.Name);
                Assert.Equal("1.2.0.0", core.Manifest.Version);
            },
            metaForm =>
            {
                Assert.Equal("MetaForm", metaForm.Name);
                Assert.Equal("2.0.0.0", metaForm.Version);
                Assert.Equal("MetaForm", metaForm.Manifest.Name);
                Assert.Equal("2.0.0.0", metaForm.Manifest.Version);
            }
        );
    }

    [Fact]
    public async Task ResolveSet_Merges_MinimumVersions_AcrossInputs()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("Core", "1.0.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest("Core", "1.5.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "Portal",
            "3.0.0.0",
            new Dictionary<string, string> { ["Core"] = "1.2.0.0" }), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "WorkOrders",
            "4.0.0.0",
            new Dictionary<string, string> { ["Core"] = "1.4.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveSetAsync(
            new ResolveSetRequest
            {
                Solutions =
                [
                    new SolutionReference { Name = "Portal" },
                    new SolutionReference { Name = "WorkOrders" }
                ]
            },
            default
        );

        Assert.Equal("1.4.0.0", result.Constraints["Core"]);
        Assert.Equal("1.5.0.0", result.Resolved.Single(solution => solution.Name == "Core").Version);
    }

    [Fact]
    public async Task Store_And_Resolver_Are_CaseInvariant_But_Preserve_ExactCase()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("smartGridPCF", "1.0.44.0"), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "smartgridpcf" }, default);

        var resolved = Assert.Single(result.Resolved);
        Assert.Equal("smartGridPCF", resolved.Name);
    }

    [Fact]
    public async Task Dependents_Return_ReverseLookupRows()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest(
            "MetaForm",
            "2.0.0.0",
            new Dictionary<string, string> { ["Core"] = "1.1.0.0" }), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "Portal",
            "3.0.0.0",
            new Dictionary<string, string> { ["Core"] = "1.2.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var response = await resolver.GetDependentsAsync("core", default);

        Assert.Equal("core", response.Name);
        Assert.Equal(2, response.Dependents.Count);
        Assert.Contains(response.Dependents, item => item.Dependent == "MetaForm" && item.RequiredVersion == "1.1.0.0");
        Assert.Contains(response.Dependents, item => item.Dependent == "Portal" && item.RequiredVersion == "1.2.0.0");
    }

    private static SolutionManifest CreateManifest(
        string name,
        string version,
        IDictionary<string, string>? dependencies = null) =>
        new()
        {
            Name = name,
            Version = version,
            Publisher = "ExamplePublisher",
            Dependencies = dependencies ?? new Dictionary<string, string>(),
        };
}
