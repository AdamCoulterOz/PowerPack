using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using PowerPack.Models;
using PowerPack.Services;
using PowerPack.Storage;

namespace PowerPack.Functions;

public sealed class ResolverFunctions(
    DependencyResolver resolver,
    IManifestIndexStore store,
    PackageDownloadTokenService tokenService)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DependencyResolver _resolver = resolver;
    private readonly IManifestIndexStore _store = store;
    private readonly PackageDownloadTokenService _tokenService = tokenService;

    [Function("ResolveSolution")]
    public async Task<IActionResult> ResolveSolution(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "resolve")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var solution = await request.ReadFromJsonAsync<SolutionReference>(JsonSerializerOptions, cancellationToken);
            if (solution is null)
            {
                return BadRequest("resolve requires a JSON body with name and optional version.");
            }

            var result = await _resolver.ResolveAsync(solution, cancellationToken);
            return new OkObjectResult(await AttachPackageUrlsAsync(result, request, cancellationToken));
        }
        catch (JsonException exception)
        {
            return BadRequest($"resolve request is invalid JSON: {exception.Message}");
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [Function("ResolveSet")]
    public async Task<IActionResult> ResolveSet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "resolve-set")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<ResolveSetRequest>(JsonSerializerOptions, cancellationToken);
            if (payload is null)
            {
                return BadRequest("resolve-set requires a JSON body with solutions[].");
            }

            var result = await _resolver.ResolveSetAsync(payload, cancellationToken);
            return new OkObjectResult(await AttachPackageUrlsAsync(result, request, cancellationToken));
        }
        catch (JsonException exception)
        {
            return BadRequest($"resolve-set request is invalid JSON: {exception.Message}");
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [Function("ValidateDependencies")]
    public async Task<IActionResult> ValidateDependencies(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "validate")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<ValidateRequest>(JsonSerializerOptions, cancellationToken);
            if (payload is null)
            {
                return BadRequest("validate requires a JSON body with dependencies{}.");
            }

            var result = await _resolver.ValidateAsync(payload, cancellationToken);
            return new OkObjectResult(await AttachPackageUrlsAsync(result, request, cancellationToken));
        }
        catch (JsonException exception)
        {
            return BadRequest($"validate request is invalid JSON: {exception.Message}");
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [Function("GetDependents")]
    public async Task<IActionResult> GetDependents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dependents/{name}")] HttpRequest request,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return new OkObjectResult(await _resolver.GetDependentsAsync(name, cancellationToken));
        }
        catch (PowerPackValidationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private static BadRequestObjectResult BadRequest(string message) => new(new { message });

    private async Task<ResolutionResult> AttachPackageUrlsAsync(
        ResolutionResult result,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedSolution>(result.Resolved.Count);
        foreach (var solution in result.Resolved)
        {
            var packageMetadata = await _store.GetPackageMetadataAsync(
                solution.Name,
                solution.Manifest.ParsedVersion,
                cancellationToken
            );
            if (packageMetadata is null)
                throw new PowerPackValidationException(
                    $"Indexed manifest '{solution.Name}' version '{solution.Version}' does not have a stored package."
                );

            var token = _tokenService.CreateToken(solution.Name, solution.Version);
            var downloadUrl = BuildDownloadUrl(request, solution.Name, solution.Version, token);
            resolved.Add(new ResolvedSolution
            {
                Name = solution.Name,
                Version = solution.Version,
                Publisher = solution.Publisher,
                Dependencies = solution.Dependencies,
                Manifest = solution.Manifest,
                Package = new ResolvedPackage
                {
                    FileName = packageMetadata.FileName,
                    ContentType = packageMetadata.ContentType,
                    ContentLength = packageMetadata.ContentLength,
                    Quality = packageMetadata.Quality,
                    DownloadUrl = downloadUrl,
                },
            });
        }

        return new ResolutionResult
        {
            Status = result.Status,
            Roots = result.Roots,
            Constraints = result.Constraints,
            Resolved = resolved,
            Missing = result.Missing,
            Invalid = result.Invalid,
        };
    }

    private static string BuildDownloadUrl(HttpRequest request, string name, string version, string token)
    {
        var builder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host = request.Host.Host,
            Port = request.Host.Port ?? -1,
            Path = $"{request.PathBase}/api/packages/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}",
            Query = $"token={Uri.EscapeDataString(token)}",
        };
        return builder.Uri.ToString();
    }
}
