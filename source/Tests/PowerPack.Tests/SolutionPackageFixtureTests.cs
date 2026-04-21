using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
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
    public async Task ManifestBuilder_ParsesLegacyFlatManagedSolutionPackage()
    {
        var fixture = FixtureCatalog.All.Single(item => item.Name == "ExperienceHub");
        var builder = CreateManifestBuilder();

        var manifest = await builder.BuildAsync(
            SolutionPackageFixtureWriter.CreateZipBytes(fixture, useLegacyFlatLayout: true),
            null,
            default
        );

        Assert.Equal("ExperienceHub", manifest.Name);
        Assert.Equal("2.0.0.0", manifest.Version);

        var connection = Assert.IsType<JsonObject>(manifest.Connections["pp_experience_approvals"]);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_approvals", connection["type"]?.GetValue<string>());
        Assert.Equal("user", connection["auth"]?.GetValue<string>());
        Assert.Equal("Approval workflow connection for experience orchestration.", connection["description"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManifestBuilder_PreservesConnectionParameterKeysContainingColons()
    {
        var fixture = new SolutionPackageFixture(
            "NotifyLike",
            "1.3.3.0",
            "ExamplePublisher",
            [],
            [
                new ConnectionReferenceFixture(
                    "pp_notify_dataverse",
                    "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps",
                    "Dataverse Notify",
                    """
                    Used by flows to read and update Dataverse data.
                    [requirements]
                    auth: service
                    roles:
                    - integration
                    [connection]
                    method: ServicePrincipalOauth
                    parameters:
                      token:clientId: ${client_id}
                      token:clientSecret: ${client_secret}
                      token:TenantId: ${tenant_id}
                    """
                ),
            ]
        );

        var httpClient = new HttpClient(new StaticJsonHttpMessageHandler(
            """
            {
              "properties": {
                "connectionParameterSets": {
                  "values": [
                    {
                      "name": "ServicePrincipalOauth",
                      "parameters": {
                        "token:clientId": {
                          "type": "string",
                          "uiDefinition": { "constraints": { "required": true } }
                        },
                        "token:clientSecret": {
                          "type": "string",
                          "uiDefinition": { "constraints": { "required": true } }
                        },
                        "token:TenantId": {
                          "type": "string",
                          "uiDefinition": { "constraints": { "required": true } }
                        }
                      }
                    }
                  ]
                }
              }
            }
            """
        ));
        var builder = new SolutionPackageManifestBuilder(new PowerPlatformConnectorMetadataClient(httpClient, new StaticTokenCredential()));

        var manifest = await builder.BuildAsync(
            SolutionPackageFixtureWriter.CreateZipBytes(fixture),
            "test-environment-id",
            default
        );

        var connection = Assert.IsType<JsonObject>(manifest.Connections["pp_notify_dataverse"]);
        var parameterSet = Assert.IsType<JsonObject>(connection["connection_parameters_set"]);
        var values = Assert.IsType<JsonObject>(parameterSet["values"]);

        Assert.Equal("${client_id}", values["token:clientId"]?["value"]?.GetValue<string>());
        Assert.Equal("${client_secret}", values["token:clientSecret"]?["value"]?.GetValue<string>());
        Assert.Equal("${tenant_id}", values["token:TenantId"]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManifestBuilder_InfersAllowedAttachmentExtensionsFromPackageContents()
    {
        var builder = CreateManifestBuilder();

        var manifest = await builder.BuildAsync(CreateManagedSolutionZipWithBlockedAttachmentContent(), null, default);

        Assert.Equal(["js"], manifest.EnvironmentRequirements.Dataverse.AllowedAttachmentExtensions);
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

    private static byte[] CreateManagedSolutionZipWithBlockedAttachmentContent()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(
                archive,
                "solution.xml",
                """
                <ImportExportXml>
                  <SolutionManifest>
                    <UniqueName>PortalLike</UniqueName>
                    <Version>1.0.0.0</Version>
                    <Publisher>
                      <UniqueName>ExamplePublisher</UniqueName>
                    </Publisher>
                  </SolutionManifest>
                </ImportExportXml>
                """
            );
            WriteTextEntry(
                archive,
                "customizations.xml",
                """
                <ImportExportXml>
                  <WebResources>
                    <WebResource>
                      <WebResourceId>{00000000-0000-0000-0000-000000000001}</WebResourceId>
                      <Name>sch_lookupdropdown</Name>
                      <DisplayName>Lookup Dropdown JS</DisplayName>
                      <WebResourceType>3</WebResourceType>
                      <FileName>/WebResources/sch_lookupdropdown00000000-0000-0000-0000-000000000001</FileName>
                    </WebResource>
                  </WebResources>
                </ImportExportXml>
                """
            );
            WriteTextEntry(archive, "powerpagecomponents/example/filecontent/lookup-dropdown.js", "console.log('hi');");
        }

        return buffer.ToArray();
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture tests should not call live connector metadata endpoints.");
    }

    private sealed class StaticJsonHttpMessageHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(JsonNode.Parse(payload))
            });
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
