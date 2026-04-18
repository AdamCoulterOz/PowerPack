using System.Text.Json.Nodes;
using Azure.Core;
using PowerPack.Models;
using PowerPack.Services;
using PowerPack.Storage;
using PowerPack.TestFixtures;

namespace PowerPack.Tests;

public sealed class SolutionPackageFixtureTests
{
    [Fact]
    public void FixtureWriter_WritesExpectedPowerPlatformSolutionZipFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "powerpack-fixtures", Guid.NewGuid().ToString("N"));

        try
        {
            var writtenPaths = SolutionPackageFixtureWriter.WriteAll(outputDirectory);

            Assert.Equal(4, writtenPaths.Count);
            Assert.Contains(Path.Combine(outputDirectory, "SharedFoundation_1.0.0.0.zip"), writtenPaths);
            Assert.Contains(Path.Combine(outputDirectory, "TableToolkit_1.1.0.0.zip"), writtenPaths);
            Assert.Contains(Path.Combine(outputDirectory, "ExperienceHub_2.0.0.0.zip"), writtenPaths);
            Assert.Contains(Path.Combine(outputDirectory, "WorkspaceForms_1.44.0.0.zip"), writtenPaths);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestBuilder_ParsesGeneratedPowerPlatformSolutionPackage()
    {
        var fixture = FixtureCatalog.All.Single(item => item.Name == "ExperienceHub");
        var builder = CreateManifestBuilder();

        var manifest = await builder.BuildAsync(SolutionPackageFixtureWriter.CreateZipBytes(fixture), null, default);

        Assert.Equal("ExperienceHub", manifest.Name);
        Assert.Equal("2.0.0.0", manifest.Version);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SharedFoundation"] = "1.0.0.0",
                ["TableToolkit"] = "1.1.0.0",
            },
            manifest.Dependencies
        );

        var connection = Assert.IsType<JsonObject>(manifest.Connections["pp_experience_approvals"]);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_approvals", connection["type"]?.GetValue<string>());
        Assert.Equal("user", connection["auth"]?.GetValue<string>());
        Assert.Equal("Approval workflow connection for experience orchestration.", connection["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task DependencyResolver_ResolvesGeneratedFixturePackages()
    {
        var builder = CreateManifestBuilder();
        var store = new InMemoryManifestIndexStore();

        foreach (var fixture in FixtureCatalog.All)
        {
            var manifest = await builder.BuildAsync(SolutionPackageFixtureWriter.CreateZipBytes(fixture), null, default);
            await store.UpsertManifestAsync(manifest, null, default);
        }

        var resolver = new DependencyResolver(store);
        var result = await resolver.ResolveAsync(new SolutionReference { Name = "WorkspaceForms" }, default);

        Assert.Equal("resolved", result.Status);
        Assert.Equal(
            ["SharedFoundation", "TableToolkit", "ExperienceHub", "WorkspaceForms"],
            result.Resolved.Select(solution => solution.Name).ToArray()
        );
        Assert.Equal("1.1.0.0", result.Constraints["TableToolkit"]);
        Assert.Equal("2.0.0.0", result.Constraints["ExperienceHub"]);
    }

    private static SolutionPackageManifestBuilder CreateManifestBuilder()
    {
        var httpClient = new HttpClient(new ThrowingHttpMessageHandler());
        var tokenCredential = new StaticTokenCredential();
        return new SolutionPackageManifestBuilder(new PowerPlatformConnectorMetadataClient(httpClient, tokenCredential));
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture tests should not call live connector metadata endpoints.");
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fixture-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
