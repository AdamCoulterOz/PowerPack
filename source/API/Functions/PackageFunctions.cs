using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using PowerPack.Models;
using PowerPack.Services;
using PowerPack.Storage;

namespace PowerPack.Functions;

public sealed class PackageFunctions(
    IManifestIndexStore store,
    IPackageBlobStore packageBlobStore,
    PackageDownloadTokenService tokenService)
{
    private readonly IManifestIndexStore _store = store;
    private readonly IPackageBlobStore _packageBlobStore = packageBlobStore;
    private readonly PackageDownloadTokenService _tokenService = tokenService;

    [Function("DownloadPackage")]
    public async Task<IActionResult> DownloadPackage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "packages/{name}/{version}")] HttpRequest request,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsedVersion = SolutionVersion.Parse(version);
            _tokenService.ValidateToken(request.Query["token"].ToString(), name, parsedVersion.ToString());

            var packageMetadata = await _store.GetPackageMetadataAsync(name, parsedVersion, cancellationToken);
            if (packageMetadata is null)
                return NotFound($"Package '{name}' version '{parsedVersion}' was not found.");

            var package = await _packageBlobStore.DownloadAsync(packageMetadata, cancellationToken);
            if (package is null)
                return NotFound($"Package blob for '{name}' version '{parsedVersion}' was not found.");

            request.HttpContext.Response.ContentLength = package.ContentLength;
            request.HttpContext.Response.Headers.ContentDisposition = $@"attachment; filename=""{package.FileName}""";
            return new FileStreamResult(package.Content, package.ContentType)
            {
                FileDownloadName = package.FileName,
            };
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private static IActionResult NotFound(string message) => new NotFoundObjectResult(new { message });

    private static IActionResult BadRequest(string message) => new BadRequestObjectResult(new { message });
}
