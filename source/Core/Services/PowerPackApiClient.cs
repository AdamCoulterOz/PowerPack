using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using PowerPack.Models;

namespace PowerPack.Services;

public sealed class PowerPackApiClient(HttpClient httpClient, PowerPackApiClientOptions? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly PowerPackApiClientOptions _options = options ?? new PowerPackApiClientOptions();

    public async Task<PublishedPackageResponse> PublishPackageAsync(
        PowerPackPackagePublishRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PackageContent);
        if (string.IsNullOrWhiteSpace(request.ApiBaseUrl))
            throw new PowerPackServiceException("PowerPack publish requires an API base URL.");

        var normalizedQuality = PackageQuality.Parse(request.Quality);
        var query = new List<string> { $"quality={Uri.EscapeDataString(normalizedQuality)}" };
        if (!string.IsNullOrWhiteSpace(request.PowerPlatformEnvironmentId))
            query.Add($"powerPlatformEnvironmentId={Uri.EscapeDataString(request.PowerPlatformEnvironmentId.Trim())}");

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{request.ApiBaseUrl.TrimEnd('/')}/api/packages?{string.Join("&", query)}");
        httpRequest.Content = new StreamContent(request.PackageContent);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        await AuthorizeAsync(httpRequest, cancellationToken);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var payload = await ReadJsonResponseAsync(response, cancellationToken);
        try
        {
            return payload.Deserialize<PublishedPackageResponse>(JsonOptions)
                ?? throw new PowerPackServiceException("PowerPack API returned an empty publish response.");
        }
        catch (JsonException exception)
        {
            throw new PowerPackServiceException($"PowerPack API returned invalid publish JSON: {exception.Message}", exception);
        }
    }

    public async Task<ResolutionResult> ResolveSetAsync(
        string apiBaseUrl,
        ResolveSetRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new PowerPackServiceException("Resolve-set requires a PowerPack API base URL.");
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = JsonSerializer.SerializeToNode(request, JsonOptions)
            ?? throw new PowerPackServiceException("Resolve-set request body could not be serialized.");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl.TrimEnd('/')}/api/resolve-set")
        {
            Content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
        };
        await AuthorizeAsync(httpRequest, cancellationToken);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var payload = await ReadJsonResponseAsync(response, cancellationToken);
        try
        {
            return payload.Deserialize<ResolutionResult>(JsonOptions)
                ?? throw new PowerPackServiceException("PowerPack API returned an empty resolve-set response.");
        }
        catch (JsonException exception)
        {
            throw new PowerPackServiceException($"PowerPack API returned invalid resolve-set JSON: {exception.Message}", exception);
        }
    }

    public async Task DownloadPackageAsync(
        string downloadUrl,
        Stream destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new PowerPackServiceException("Package download URL is required.");
        ArgumentNullException.ThrowIfNull(destination);

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new PowerPackServiceException(BuildHttpErrorMessage(response, payloadText));
        }

        await using var packageStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await packageStream.CopyToAsync(destination, cancellationToken);
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.Credential is null)
            return;
        if (string.IsNullOrWhiteSpace(_options.ApplicationIdUri))
            throw new PowerPackServiceException("PowerPack API authentication requires an application ID URI.");

        var tokenScope = $"{_options.ApplicationIdUri.TrimEnd('/')}/.default";
        var accessToken = await _options.Credential.GetTokenAsync(new TokenRequestContext([tokenScope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<JsonNode> ReadJsonResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PowerPackServiceException(BuildHttpErrorMessage(response, payloadText));

        try
        {
            return JsonNode.Parse(payloadText, documentOptions: default, nodeOptions: default)
                ?? throw new PowerPackServiceException("PowerPack API returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new PowerPackServiceException($"PowerPack API returned invalid JSON: {exception.Message}", exception);
        }
    }

    private static string BuildHttpErrorMessage(HttpResponseMessage response, string payloadText)
    {
        try
        {
            var payload = JsonNode.Parse(payloadText) as JsonObject;
            var message = payload?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                return $"PowerPack API request failed: HTTP {(int)response.StatusCode}: {message}";
        }
        catch (JsonException)
        {
        }

        return $"PowerPack API request failed: HTTP {(int)response.StatusCode}: {payloadText}";
    }
}

public sealed class PowerPackApiClientOptions
{
    public TokenCredential? Credential { get; init; }

    public string? ApplicationIdUri { get; init; }
}

public sealed class PowerPackPackagePublishRequest
{
    public required string ApiBaseUrl { get; init; }

    public required Stream PackageContent { get; init; }

    public required string Quality { get; init; }

    public string? PowerPlatformEnvironmentId { get; init; }
}

public sealed class PowerPackServiceException(string message, Exception? innerException = null)
    : Exception(message, innerException);
