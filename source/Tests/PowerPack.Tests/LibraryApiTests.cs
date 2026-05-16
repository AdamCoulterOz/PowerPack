using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using PowerPack.Models;
using PowerPack.Services;

namespace PowerPack.Tests;

public sealed class LibraryApiTests
{
    [Fact]
    public void BuiltInSolutionRegistry_exposes_solution_unique_names()
    {
        var names = BuiltInSolutionRegistry.Default.SolutionUniqueNames;

        Assert.NotEmpty(names);
        Assert.Contains("AccessTeam", names);
        Assert.True(BuiltInSolutionRegistry.Default.Contains("accessteam"));
    }

    [Fact]
    public async Task DataverseSolutionClient_exports_solution_through_Web_API()
    {
        var packageBytes = Encoding.UTF8.GetBytes("solution zip");
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.crm.dynamics.com/api/data/v9.2/ExportSolution", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);

            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!.AsObject();
            Assert.Equal("WorkspaceForms", body["SolutionName"]!.GetValue<string>());
            Assert.True(body["Managed"]!.GetValue<bool>());

            return JsonResponse(new JsonObject
            {
                ["ExportSolutionFile"] = Convert.ToBase64String(packageBytes),
            });
        }));

        var client = new DataverseSolutionClient(httpClient, new StaticTokenCredential());
        var exported = await client.ExportSolutionAsync(
            "https://example.crm.dynamics.com/",
            new DataverseSolutionExportOptions
            {
                SolutionName = " WorkspaceForms ",
                Managed = true,
            },
            CancellationToken.None);

        Assert.Equal(packageBytes, exported);
    }

    [Fact]
    public async Task DataverseSolutionClient_starts_async_import_with_direct_Dataverse_action()
    {
        var asyncOperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.crm.dynamics.com/api/data/v9.2/ImportSolutionAsync", request.RequestUri!.ToString());

            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!.AsObject();
            Assert.True(body["OverwriteUnmanagedCustomizations"]!.GetValue<bool>());
            Assert.True(body["PublishWorkflows"]!.GetValue<bool>());
            Assert.Equal(Convert.ToBase64String([1, 2, 3]), body["CustomizationFile"]!.GetValue<string>());
            Assert.Equal("22222222-2222-2222-2222-222222222222", body["ImportJobId"]!.GetValue<string>());

            return JsonResponse(new JsonObject
            {
                ["AsyncOperationId"] = asyncOperationId,
                ["ImportJobKey"] = "import-key",
            });
        }));

        var client = new DataverseSolutionClient(httpClient, new StaticTokenCredential());
        var job = await client.StartImportSolutionAsync(
            "https://example.crm.dynamics.com",
            [1, 2, 3],
            new DataverseSolutionImportOptions
            {
                PublishWorkflows = true,
                ImportJobId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            },
            CancellationToken.None);

        Assert.Equal(asyncOperationId, job.AsyncOperationId);
        Assert.Equal("import-key", job.ImportJobKey);
    }

    [Fact]
    public async Task PowerPackApiClient_publishes_package_with_stable_response_model()
    {
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://powerpack.test/api/packages?quality=release&powerPlatformEnvironmentId=env-id",
                request.RequestUri!.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Equal("application/zip", request.Content!.Headers.ContentType!.MediaType);

            return JsonResponse(new JsonObject
            {
                ["manifest"] = new JsonObject
                {
                    ["name"] = "WorkspaceForms",
                    ["version"] = "1.2.3.4",
                    ["publisher"] = "Workspace",
                    ["dependencies"] = new JsonObject(),
                },
                ["package"] = new JsonObject
                {
                    ["blobName"] = "packages/workspaceforms.zip",
                    ["fileName"] = "WorkspaceForms.zip",
                    ["contentType"] = "application/zip",
                    ["contentLength"] = 12,
                    ["quality"] = "release",
                },
            });
        }));

        var client = new PowerPackApiClient(
            httpClient,
            new PowerPackApiClientOptions
            {
                Credential = new StaticTokenCredential(),
                ApplicationIdUri = "api://powerpack",
            });
        await using var packageContent = new MemoryStream([1, 2, 3]);
        var published = await client.PublishPackageAsync(
            new PowerPackPackagePublishRequest
            {
                ApiBaseUrl = "https://powerpack.test",
                PackageContent = packageContent,
                Quality = "release",
                PowerPlatformEnvironmentId = "env-id",
            },
            CancellationToken.None);

        Assert.Equal("WorkspaceForms", published.Manifest.Name);
        Assert.Equal("packages/workspaceforms.zip", published.Package.BlobName);
    }

    [Fact]
    public async Task PowerPackApiClient_downloads_package_without_powerpack_api_token()
    {
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6]),
            };
        }));

        var client = new PowerPackApiClient(
            httpClient,
            new PowerPackApiClientOptions
            {
                Credential = new StaticTokenCredential(),
                ApplicationIdUri = "api://powerpack",
            });
        await using var destination = new MemoryStream();
        await client.DownloadPackageAsync("https://powerpack.test/api/packages/x/1.0.0.0/download?token=t", destination, CancellationToken.None);

        Assert.Equal([4, 5, 6], destination.ToArray());
    }

    private static HttpResponseMessage JsonResponse(JsonObject payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Contains(requestContext.Scopes, scope => scope.EndsWith("/.default", StringComparison.Ordinal));
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handle(request));
        }
    }
}
