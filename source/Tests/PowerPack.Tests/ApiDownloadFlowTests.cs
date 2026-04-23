using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PowerPack.Functions;
using PowerPack.Models;
using PowerPack.Options;
using PowerPack.Services;
using PowerPack.Storage;
using PowerPack.TestFixtures;

namespace PowerPack.Tests;

public sealed class ApiDownloadFlowTests
{
    [Fact]
    public async Task ResolveSolution_ProducesTokenizedDownloadUrl_ThatDownloadEndpointStreams()
    {
        var environment = await CreateEnvironmentAsync();
        var request = CreateJsonRequest(
            "POST",
            "https://powerpack.test/api/resolve",
            new SolutionReference { Name = "WorkspaceForms" }
        );
        request.Headers.Authorization = $"Bearer {environment.ApiAccessToken}";

        var actionResult = await environment.ResolverFunctions.ResolveSolution(request, default);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payload = Assert.IsType<ResolutionResult>(ok.Value);
        Assert.Equal("resolved", payload.Status);
        Assert.Equal(
            ["SharedFoundation", "TableToolkit", "ExperienceHub", "WorkspaceForms"],
            payload.Resolved.Select(item => item.Name).ToArray()
        );

        var resolvedWorkspaceForms = payload.Resolved.Single(item => item.Name == "WorkspaceForms");
        var downloadUri = new Uri(resolvedWorkspaceForms.Package!.DownloadUrl);
        var query = QueryHelpers.ParseQuery(downloadUri.Query);

        Assert.Equal("/api/packages/WorkspaceForms/1.44.0.0/download", downloadUri.AbsolutePath);
        Assert.True(query.TryGetValue("token", out var token));
        Assert.False(string.IsNullOrWhiteSpace(token.ToString()));

        var downloadRequest = CreateRequest("GET", downloadUri.ToString());
        var downloadResult = await environment.PackageFunctions.DownloadPackage(
            downloadRequest,
            "WorkspaceForms",
            "1.44.0.0",
            default
        );

        var file = Assert.IsType<FileStreamResult>(downloadResult);
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal("WorkspaceForms_1.44.0.0.zip", file.FileDownloadName);
        Assert.Equal(environment.PackageBytes["WorkspaceForms"], await ReadAllBytesAsync(file.FileStream));
        Assert.Equal(environment.PackageBytes["WorkspaceForms"].Length, downloadRequest.HttpContext.Response.ContentLength);
        Assert.Equal(
            @"attachment; filename=""WorkspaceForms_1.44.0.0.zip""",
            downloadRequest.HttpContext.Response.Headers.ContentDisposition.ToString()
        );
    }

    [Fact]
    public async Task DownloadPackage_RejectsTokenForDifferentPackage()
    {
        var environment = await CreateEnvironmentAsync();
        var token = environment.TokenService.CreateToken("ExperienceHub", "2.0.0.0");
        var request = CreateRequest(
            "GET",
            $"https://powerpack.test/api/packages/WorkspaceForms/1.44.0.0/download?token={Uri.EscapeDataString(token)}"
        );

        var actionResult = await environment.PackageFunctions.DownloadPackage(
            request,
            "WorkspaceForms",
            "1.44.0.0",
            default
        );

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult);
        var payload = JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("does not match the requested package", payload, StringComparison.Ordinal);
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var store = new InMemoryManifestIndexStore();
        var packageBlobStore = new InMemoryPackageBlobStore();
        var manifestBuilder = CreateManifestBuilder();
        var authorizationService = CreateAuthorizationService(out var apiAccessToken);
        var packageBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var fixture in FixtureCatalog.All)
        {
            var bytes = SolutionPackageFixtureWriter.CreateZipBytes(fixture);
            var manifest = await manifestBuilder.BuildAsync(bytes, null, default);
            var metadata = new ManifestPackageMetadata
            {
                BlobName = fixture.Name,
                FileName = $"{fixture.Name}_{fixture.Version}.zip",
                ContentType = "application/zip",
                ContentLength = bytes.Length,
                Quality = "release",
            };

            await store.UpsertManifestAsync(manifest, metadata, default);
            packageBlobStore.Add(metadata, bytes);
            packageBytes[fixture.Name] = bytes;
        }

        var tokenService = new PackageDownloadTokenService(Microsoft.Extensions.Options.Options.Create(new PowerPackOptions
        {
            Storage = new StorageOptions(),
            Downloads = new DownloadOptions
            {
                TokenSigningKey = "0123456789abcdef0123456789abcdef",
                TokenLifetimeMinutes = 30,
            },
        }));

        return new TestEnvironment
        {
            ResolverFunctions = new ResolverFunctions(
                new DependencyResolver(store),
                store,
                authorizationService,
                tokenService
            ),
            PackageFunctions = new PackageFunctions(store, packageBlobStore, tokenService),
            TokenService = tokenService,
            ApiAccessToken = apiAccessToken,
            PackageBytes = packageBytes,
        };
    }

    private static SolutionPackageManifestBuilder CreateManifestBuilder()
    {
        var httpClient = new HttpClient(new ThrowingHttpMessageHandler());
        var tokenCredential = new StaticTokenCredential();
        return new SolutionPackageManifestBuilder(new PowerPlatformConnectorMetadataClient(httpClient, tokenCredential));
    }

    private static HttpRequest CreateJsonRequest(string method, string uri, object payload)
    {
        var request = CreateRequest(method, uri);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        request.Body = new MemoryStream(bytes);
        request.ContentType = "application/json";
        request.ContentLength = bytes.Length;
        return request;
    }

    private static HttpRequest CreateRequest(string method, string uri)
    {
        var requestUri = new Uri(uri);
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = method;
        request.Scheme = requestUri.Scheme;
        request.Host = requestUri.IsDefaultPort
            ? new HostString(requestUri.Host)
            : new HostString(requestUri.Host, requestUri.Port);
        request.Path = requestUri.AbsolutePath;
        request.QueryString = new QueryString(requestUri.Query);
        return request;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static PowerPackApiAuthorizationService CreateAuthorizationService(out string apiAccessToken)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa.ExportParameters(true));
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = "https://login.microsoftonline.com/test-tenant/v2.0",
        };
        configuration.SigningKeys.Add(new RsaSecurityKey(rsa.ExportParameters(false)));

        apiAccessToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: configuration.Issuer,
            audience: "api://powerpack.test",
            claims:
            [
                new Claim("roles", "PowerPack.Access"),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        ));

        return new PowerPackApiAuthorizationService(
            new AuthOptions
            {
                ApplicationClientId = "powerpack-client-id",
                ApplicationIdUri = "api://powerpack.test",
                TenantId = "test-tenant",
                RequiredRole = "PowerPack.Access",
                RequiredScope = "PowerPack.Access",
            },
            new StaticConfigurationManager(configuration)
        );
    }

    private sealed class TestEnvironment
    {
        public required ResolverFunctions ResolverFunctions { get; init; }

        public required PackageFunctions PackageFunctions { get; init; }

        public required PackageDownloadTokenService TokenService { get; init; }

        public required string ApiAccessToken { get; init; }

        public required IReadOnlyDictionary<string, byte[]> PackageBytes { get; init; }
    }

    private sealed class StaticConfigurationManager(OpenIdConnectConfiguration configuration)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }
    }

    private sealed class InMemoryPackageBlobStore : IPackageBlobStore
    {
        private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);

        public void Add(ManifestPackageMetadata metadata, byte[] content) => _packages[metadata.BlobName] = content;

        public Task<ManifestPackageMetadata> UploadAsync(
            string packageName,
            SolutionVersion version,
            string quality,
            Stream content,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PackageDownloadResult?> DownloadAsync(
            ManifestPackageMetadata packageMetadata,
            CancellationToken cancellationToken)
        {
            if (!_packages.TryGetValue(packageMetadata.BlobName, out var content))
                return Task.FromResult<PackageDownloadResult?>(null);

            return Task.FromResult<PackageDownloadResult?>(new PackageDownloadResult
            {
                Content = new MemoryStream(content, writable: false),
                FileName = packageMetadata.FileName,
                ContentType = packageMetadata.ContentType,
                ContentLength = packageMetadata.ContentLength,
            });
        }

        public Task DeleteAsync(ManifestPackageMetadata packageMetadata, CancellationToken cancellationToken)
        {
            _packages.Remove(packageMetadata.BlobName);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("API flow tests should not call live connector metadata endpoints.");
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
