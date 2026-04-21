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
        await store.UpsertManifestAsync(CreateManifest("SharedFoundation", "1.0.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest("SharedFoundation", "1.2.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "WorkspaceForms",
            "2.0.0.0",
            new Dictionary<string, string> { ["SharedFoundation"] = "1.1.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "WorkspaceForms" }, default);

        Assert.Equal("resolved", result.Status);
        Assert.Collection(
            result.Resolved,
            core =>
            {
                Assert.Equal("SharedFoundation", core.Name);
                Assert.Equal("1.2.0.0", core.Version);
                Assert.Equal("SharedFoundation", core.Manifest.Name);
                Assert.Equal("1.2.0.0", core.Manifest.Version);
            },
            workspaceForms =>
            {
                Assert.Equal("WorkspaceForms", workspaceForms.Name);
                Assert.Equal("2.0.0.0", workspaceForms.Version);
                Assert.Equal("WorkspaceForms", workspaceForms.Manifest.Name);
                Assert.Equal("2.0.0.0", workspaceForms.Manifest.Version);
            }
        );
    }

    [Fact]
    public async Task ResolveSet_Merges_MinimumVersions_AcrossInputs()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("SharedFoundation", "1.0.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest("SharedFoundation", "1.5.0.0"), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "ExperienceHub",
            "3.0.0.0",
            new Dictionary<string, string> { ["SharedFoundation"] = "1.2.0.0" }), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "FieldOperations",
            "4.0.0.0",
            new Dictionary<string, string> { ["SharedFoundation"] = "1.4.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveSetAsync(
            new ResolveSetRequest
            {
                Solutions =
                [
                    new SolutionReference { Name = "ExperienceHub" },
                    new SolutionReference { Name = "FieldOperations" }
                ]
            },
            default
        );

        Assert.Equal("1.4.0.0", result.Constraints["SharedFoundation"]);
        Assert.Equal("1.5.0.0", result.Resolved.Single(solution => solution.Name == "SharedFoundation").Version);
    }

    [Fact]
    public async Task Store_And_Resolver_Are_CaseInvariant_But_Preserve_ExactCase()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("CaseAwareGrid", "1.0.44.0"), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "caseawaregrid" }, default);

        var resolved = Assert.Single(result.Resolved);
        Assert.Equal("CaseAwareGrid", resolved.Name);
    }

    [Fact]
    public async Task Store_Rejects_MixedCaseManifestNames_InSamePartition()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("core", "1.5.96.0"), null, default);

        var exception = await Assert.ThrowsAsync<PowerPackValidationException>(() =>
            store.UpsertManifestAsync(CreateManifest("Core", "1.5.105.0"), null, default));

        Assert.Equal(
            "Manifest index contains solution names that differ only by case: core, Core.",
            exception.Message);
    }

    [Fact]
    public async Task ResolveSet_Ignores_BuiltIn_Dependencies()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest(
            "Core",
            "1.5.105.0",
            new Dictionary<string, string>
            {
                ["AppModuleWebResources"] = "2.5.0.0",
                ["msdyn_AppFrameworkInfraExtensions"] = "1.0.0.16",
            }), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "Core" }, default);

        Assert.Equal("resolved", result.Status);
        Assert.Empty(result.Missing);

        var resolved = Assert.Single(result.Resolved);
        Assert.Equal("Core", resolved.Name);
    }

    [Fact]
    public async Task ResolveSet_Ignores_BuiltIn_RootSolutions()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest("Core", "1.5.105.0"), null, default);

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveSetAsync(
            new ResolveSetRequest
            {
                Solutions =
                [
                    new SolutionReference { Name = "AppModuleWebResources", Version = "2.5.0.0" },
                    new SolutionReference { Name = "Core", Version = "1.5.105.0" },
                ]
            },
            default
        );

        Assert.Equal("resolved", result.Status);
        Assert.Empty(result.Missing);
        Assert.Single(result.Roots);
        Assert.Equal("Core", result.Roots[0].Name);
        Assert.Single(result.Resolved);
        Assert.Equal("Core", result.Resolved[0].Name);
    }

    [Fact]
    public async Task Dependents_Return_ReverseLookupRows()
    {
        var store = new InMemoryManifestIndexStore();
        await store.UpsertManifestAsync(CreateManifest(
            "WorkspaceForms",
            "2.0.0.0",
            new Dictionary<string, string> { ["SharedFoundation"] = "1.1.0.0" }), null, default);
        await store.UpsertManifestAsync(CreateManifest(
            "ExperienceHub",
            "3.0.0.0",
            new Dictionary<string, string> { ["SharedFoundation"] = "1.2.0.0" }), null, default);

        var resolver = new DependencyResolver(store);
        var response = await resolver.GetDependentsAsync("sharedfoundation", default);

        Assert.Equal("sharedfoundation", response.Name);
        Assert.Equal(2, response.Dependents.Count);
        Assert.Contains(response.Dependents, item => item.Dependent == "WorkspaceForms" && item.RequiredVersion == "1.1.0.0");
        Assert.Contains(response.Dependents, item => item.Dependent == "ExperienceHub" && item.RequiredVersion == "1.2.0.0");
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
