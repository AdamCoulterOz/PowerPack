using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PowerPack.Models;
using PowerPack.Services;
using PowerPack.Storage;

namespace PowerPack.Functions;

public sealed class ManifestFunctions(
    IManifestIndexStore store,
    IPackageBlobStore packageBlobStore,
    SolutionPackageManifestBuilder manifestBuilder,
    ILogger<ManifestFunctions> logger)
{
    private readonly IManifestIndexStore _store = store;
    private readonly IPackageBlobStore _packageBlobStore = packageBlobStore;
    private readonly SolutionPackageManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly ILogger<ManifestFunctions> _logger = logger;

    [Function("ListManifests")]
    public async Task<IActionResult> ListManifests(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "packages/{name}")] HttpRequest request,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifests = await _store.ListManifestsAsync(name, cancellationToken);
            return new OkObjectResult(
                manifests.Select(manifest => new ManifestSummary
                {
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Publisher = manifest.Publisher,
                    Dependencies = new Dictionary<string, string>(manifest.Dependencies, StringComparer.Ordinal),
                })
            );
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [Function("GetManifest")]
    public async Task<IActionResult> GetManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "packages/{name}/{version}")] HttpRequest request,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(name, SolutionVersion.Parse(version), cancellationToken);
            return manifest is null
                ? new NotFoundObjectResult(new { message = $"Manifest '{name}' version '{version}' was not found." })
                : new OkObjectResult(manifest);
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [Function("CreateManifest")]
    public async Task<IActionResult> CreateManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "packages")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        return await PublishPackageInternal(request, null, null, cancellationToken);
    }

    [Function("UpsertManifest")]
    public async Task<IActionResult> UpsertManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "packages/{name}/{version}")] HttpRequest request,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        return await PublishPackageInternal(request, name, version, cancellationToken);
    }

    [Function("DeleteManifest")]
    public async Task<IActionResult> DeleteManifest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "packages/{name}/{version}")] HttpRequest request,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsedVersion = SolutionVersion.Parse(version);
            var packageMetadata = await _store.GetPackageMetadataAsync(name, parsedVersion, cancellationToken);
            if (packageMetadata is not null)
                await _packageBlobStore.DeleteAsync(packageMetadata, cancellationToken);

            await _store.DeleteManifestAsync(name, parsedVersion, cancellationToken);
            return new StatusCodeResult((int)HttpStatusCode.NoContent);
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private async Task<IActionResult> PublishPackageInternal(
        HttpRequest request,
        string? routeName,
        string? routeVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureZipContentType(request.ContentType);

            var quality = PackageQuality.Parse(request.Query["quality"]);
            var powerPlatformEnvironmentId = request.Query["powerPlatformEnvironmentId"].ToString();

            await using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length == 0)
                return BadRequest("Managed solution package zip body is required.");

            var packageBytes = buffer.ToArray();
            var manifest = await _manifestBuilder.BuildAsync(
                packageBytes,
                powerPlatformEnvironmentId,
                cancellationToken
            );
            ValidateRouteIdentity(manifest, routeName, routeVersion);

            buffer.Position = 0;
            ManifestPackageMetadata? packageMetadata = null;
            try
            {
                packageMetadata = await _packageBlobStore.UploadAsync(
                    manifest.Name,
                    manifest.ParsedVersion,
                    quality,
                    buffer,
                    cancellationToken
                );

                await _store.UpsertManifestAsync(manifest, packageMetadata, cancellationToken);
            }
            catch
            {
                if (packageMetadata is not null)
                    await _packageBlobStore.DeleteAsync(packageMetadata, cancellationToken);
                throw;
            }
            _logger.LogInformation(
                "Published PowerPack package {Name} {Version} ({Quality}).",
                manifest.Name,
                manifest.Version,
                quality
            );
            return new OkObjectResult(new PublishedPackageResponse
            {
                Manifest = manifest,
                Package = packageMetadata!,
            });
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private static void EnsureZipContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return;

        var normalized = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (normalized is "application/zip" or "application/octet-stream")
            return;

        throw new PowerPackValidationException(
            "Managed solution package upload must use content type application/zip or application/octet-stream."
        );
    }

    private static void ValidateRouteIdentity(
        SolutionManifest manifest,
        string? routeName,
        string? routeVersion)
    {
        if (!string.IsNullOrWhiteSpace(routeName) &&
            !manifest.Name.Equals(routeName, StringComparison.OrdinalIgnoreCase))
            throw new PowerPackValidationException(
                $"Package solution name '{manifest.Name}' does not match route name '{routeName}'."
            );

        if (!string.IsNullOrWhiteSpace(routeVersion) &&
            manifest.Version != SolutionVersion.Parse(routeVersion).ToString())
            throw new PowerPackValidationException(
                $"Package solution version '{manifest.Version}' does not match route version '{routeVersion}'."
            );
    }

    private static BadRequestObjectResult BadRequest(string message) => new(new { message });
}
