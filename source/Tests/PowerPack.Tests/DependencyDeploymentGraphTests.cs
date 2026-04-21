using System.Text.Json.Nodes;
using PowerPack.Models;
using PowerPack.Services;

namespace PowerPack.Tests;

public sealed class DependencyDeploymentGraphTests
{
    [Fact]
    public void MissingDependenciesParser_ParsesHighestVersionPerSolution()
    {
        const string content = """
MissingDependencies:
  MissingDependency:
  - Required:
      '@solution': Core (1.2.2)
  - Required:
      '@solution': Core (1.3.3)
  - Required:
      '@solution': Portal (1.3.6)
""";

        var parser = new MissingDependenciesParser();
        var roots = parser.Parse(content, "missingdependencies.yml");

        Assert.Collection(
            roots,
            core =>
            {
                Assert.Equal("Core", core.Name);
                Assert.Equal("1.3.3.0", core.Version);
            },
            portal =>
            {
                Assert.Equal("Portal", portal.Name);
                Assert.Equal("1.3.6.0", portal.Version);
            });
    }

    [Fact]
    public void DependencyDeploymentGraphBuilder_ProjectsGenericGraph()
    {
        var builder = new DependencyDeploymentGraphBuilder();
        var graph = builder.Build(
            new ResolutionResult
            {
                Status = "resolved",
                Roots =
                [
                    new SolutionReference { Name = "Core", Version = "1.5.105.0" },
                ],
                Resolved =
                [
                    new ResolvedSolution
                    {
                        Name = "Core",
                        Version = "1.5.105.0",
                        Publisher = "ExamplePublisher",
                        Dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["AppModuleWebResources"] = "2.5.0.0",
                            ["WorkspaceForms"] = "2.1.0.0",
                        },
                        Manifest = CreateManifest("Core", "1.5.105.0"),
                        Package = new ResolvedPackage
                        {
                            DownloadUrl = "https://example.test/core.zip",
                            ContentLength = 123,
                            ContentType = "application/zip",
                            FileName = "Core.zip",
                            Quality = "prerelease",
                        },
                    },
                    new ResolvedSolution
                    {
                        Name = "WorkspaceForms",
                        Version = "2.1.0.0",
                        Publisher = "ExamplePublisher",
                        Dependencies = new Dictionary<string, string>(),
                        Manifest = CreateManifest("WorkspaceForms", "2.1.0.0", dependencies: new Dictionary<string, string>()),
                        Package = new ResolvedPackage
                        {
                            DownloadUrl = "https://example.test/workspaceforms.zip",
                            ContentLength = 456,
                            ContentType = "application/zip",
                            FileName = "WorkspaceForms.zip",
                            Quality = "prerelease",
                        },
                    },
                ],
            });

        Assert.Collection(
            graph.Roots,
            root =>
            {
                Assert.Equal("Core", root.Name);
                Assert.Equal("1.5.105.0", root.Version);
            });

        Assert.Equal(["Core", "WorkspaceForms"], graph.TopologicalOrder);

        var coreNode = graph.Nodes["Core"];
        Assert.Equal("core", coreNode.PackageTransportName);
        Assert.Equal("1.5.105.0", coreNode.SolutionPackageVersion);
        Assert.Equal("https://example.test/core.zip", coreNode.DownloadUrl);
        Assert.Equal(["js"], coreNode.EnvironmentRequirements.Dataverse.AllowedAttachmentExtensions);
        Assert.Collection(
            coreNode.Dependencies,
            dependency =>
            {
                Assert.Equal("WorkspaceForms", dependency.Name);
                Assert.Equal("2.1.0.0", dependency.Version);
            });

        var identity = coreNode.Identities["default"];
        Assert.Equal("Core service identity", identity.DisplayName);
        Assert.Contains("Core User", identity.DataverseSecurityRoles);
        Assert.Contains(identity.RequiredPermissions, permission =>
            permission.Resource == "Microsoft Graph" &&
            permission.Type == "application" &&
            permission.Name == "Directory.Read.All");

        var connection = coreNode.ConnectionReferences["shared_graph"];
        Assert.Equal("service_principal", connection.AuthMode);
        Assert.Equal("shared_webcontents", connection.ConnectorName);
        Assert.Equal("default", connection.Identity);
        Assert.NotNull(connection.ConnectionParametersSet);

        var environmentVariable = coreNode.EnvironmentVariables["sch_sampleJson"];
        Assert.Equal("json", environmentVariable.Type);
        Assert.Equal("{\"enabled\":true}", environmentVariable.StringValue);

        Assert.Contains("js", graph.EnvironmentRequirements.Dataverse.DefaultBlockedAttachmentExtensions);
        Assert.Equal(["js"], graph.EnvironmentRequirements.Dataverse.RequiredAllowedAttachmentExtensions);
        Assert.DoesNotContain("js", graph.EnvironmentRequirements.Dataverse.BlockedAttachmentExtensions);
    }

    [Fact]
    public void DependencyDeploymentGraphBuilder_MergesSourceControlledWebresourceExtensionsIntoEnvironmentRequirements()
    {
        var builder = new DependencyDeploymentGraphBuilder();
        var graph = builder.Build(
            new ResolutionResult
            {
                Status = "resolved",
                Roots =
                [
                    new SolutionReference { Name = "Core", Version = "1.5.105.0" },
                ],
                Resolved =
                [
                    new ResolvedSolution
                    {
                        Name = "Core",
                        Version = "1.5.105.0",
                        Publisher = "ExamplePublisher",
                        Dependencies = new Dictionary<string, string>(),
                        Manifest = CreateManifest("Core", "1.5.105.0", dependencies: new Dictionary<string, string>()),
                        Package = new ResolvedPackage
                        {
                            DownloadUrl = "https://example.test/core.zip",
                            ContentLength = 123,
                            ContentType = "application/zip",
                            FileName = "Core.zip",
                            Quality = "prerelease",
                        },
                    },
                ],
            },
            sourceAllowedAttachmentExtensions: ["js", "svg"]);

        Assert.Equal(["js", "svg"], graph.EnvironmentRequirements.Dataverse.RequiredAllowedAttachmentExtensions);
        Assert.DoesNotContain("js", graph.EnvironmentRequirements.Dataverse.BlockedAttachmentExtensions);
        Assert.DoesNotContain("svg", graph.EnvironmentRequirements.Dataverse.BlockedAttachmentExtensions);
    }

    private static SolutionManifest CreateManifest(
        string name,
        string version,
        IDictionary<string, string>? dependencies = null)
    {
        return new SolutionManifest
        {
            Name = name,
            Version = version,
            Publisher = "ExamplePublisher",
            Dependencies = dependencies ?? new Dictionary<string, string>
            {
                ["AppModuleWebResources"] = "2.5.0.0",
                ["WorkspaceForms"] = "2.1.0.0",
            },
            Metadata = new JsonObject
            {
                ["package_name"] = name.ToLowerInvariant(),
                ["package_version"] = version,
                ["solution_package_version"] = version,
            },
            EnvironmentRequirements = new SolutionEnvironmentRequirements
            {
                Dataverse = new DataverseSolutionEnvironmentRequirements
                {
                    AllowedAttachmentExtensions = ["js"],
                },
            },
            Connections = new JsonObject
            {
                ["shared_graph"] = new JsonObject
                {
                    ["auth"] = "service",
                    ["roles"] = new JsonArray("Core User"),
                    ["permissions"] = new JsonArray(
                        new JsonObject
                        {
                            ["resource"] = "Microsoft Graph",
                            ["type"] = "application",
                            ["name"] = "Directory.Read.All",
                        }),
                    ["type"] = "/providers/Microsoft.PowerApps/apis/shared_webcontents",
                    ["description"] = "Uses Graph.",
                    ["connection"] = new JsonObject
                    {
                        ["method"] = "CertOauth",
                    },
                    ["connection_parameters_set"] = new JsonObject
                    {
                        ["name"] = "CertOauth",
                        ["values"] = new JsonObject
                        {
                            ["token:clientId"] = new JsonObject
                            {
                                ["value"] = "${client_id}",
                            },
                        },
                    },
                },
            },
            Variables = new JsonObject
            {
                ["sch_sampleJson"] = new JsonObject
                {
                    ["type"] = "json",
                    ["default"] = new JsonObject
                    {
                        ["enabled"] = true,
                    },
                },
            },
        };
    }
}
