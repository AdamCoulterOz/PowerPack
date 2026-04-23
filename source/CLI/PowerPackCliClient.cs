using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using PowerPack.Models;

namespace PowerPack.Cli;

internal sealed class PowerPackCliClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonNode> PublishAsync(
        string apiBaseUrl,
        string applicationIdUri,
        string packagePath,
        string quality,
        string? powerPlatformEnvironmentId,
        CancellationToken cancellationToken)
    {
        var packageFile = new FileInfo(packagePath);
        if (!packageFile.Exists)
            throw new CliException($"Package zip was not found: {packageFile.FullName}");

        string normalizedQuality;
        try
        {
            normalizedQuality = PackageQuality.Parse(quality);
        }
        catch (PowerPackValidationException exception)
        {
            throw new CliException(exception.Message);
        }

        var query = new List<string> { $"quality={Uri.EscapeDataString(normalizedQuality)}" };
        if (!string.IsNullOrWhiteSpace(powerPlatformEnvironmentId))
            query.Add($"powerPlatformEnvironmentId={Uri.EscapeDataString(powerPlatformEnvironmentId)}");

        using var httpClient = await CreateHttpClientAsync(applicationIdUri, cancellationToken);
        await using var packageStream = packageFile.OpenRead();
        using var content = new StreamContent(packageStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await httpClient.PostAsync(
            $"{apiBaseUrl.TrimEnd('/')}/api/packages?{string.Join("&", query)}",
            content,
            cancellationToken
        );
        return await ReadJsonResponseAsync(response, cancellationToken);
    }

    public async Task<JsonNode> ResolveSetAsync(
        string apiBaseUrl,
        string applicationIdUri,
        string requestPath,
        CancellationToken cancellationToken)
    {
        var requestFile = new FileInfo(requestPath);
        if (!requestFile.Exists)
            throw new CliException($"Resolve-set request file was not found: {requestFile.FullName}");

        JsonNode requestBody;
        try
        {
            requestBody = JsonNode.Parse(await File.ReadAllTextAsync(requestFile.FullName, cancellationToken))
                ?? throw new CliException($"Resolve-set request file '{requestFile.FullName}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new CliException(
                $"Resolve-set request file '{requestFile.FullName}' is invalid JSON: {exception.Message}"
            );
        }

        return await ResolveSetAsync(apiBaseUrl, applicationIdUri, requestBody, cancellationToken);
    }

    public async Task<ResolutionResult> ResolveSetResultAsync(
        string apiBaseUrl,
        string applicationIdUri,
        ResolveSetRequest request,
        CancellationToken cancellationToken)
    {
        var requestBody = JsonSerializer.SerializeToNode(request, JsonOptions)
            ?? throw new CliException("Resolve-set request body could not be serialized.");
        var responseBody = await ResolveSetAsync(apiBaseUrl, applicationIdUri, requestBody, cancellationToken);
        try
        {
            return responseBody.Deserialize<ResolutionResult>(JsonOptions)
                ?? throw new CliException("PowerPack API returned an empty resolve-set response.");
        }
        catch (JsonException exception)
        {
            throw new CliException($"PowerPack API returned invalid resolve-set JSON: {exception.Message}");
        }
    }

    public async Task DownloadPackageAsync(
        string downloadUrl,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new CliException("Package download URL is required.");

        var outputFile = new FileInfo(outputPath);
        Directory.CreateDirectory(outputFile.DirectoryName!);

        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new CliException(BuildHttpErrorMessage(response, payloadText));
        }

        await using var packageStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = outputFile.Create();
        await packageStream.CopyToAsync(outputStream, cancellationToken);
    }

    private static async Task<HttpClient> CreateHttpClientAsync(
        string applicationIdUri,
        CancellationToken cancellationToken)
    {
        var tokenScope = $"{applicationIdUri.TrimEnd('/')}/user_impersonation";
        var credential = new AzureCliCredential();
        AccessToken accessToken;
        try
        {
            accessToken = await credential.GetTokenAsync(
                new TokenRequestContext([tokenScope]),
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            throw new CliException(
                $"Failed to acquire an Azure CLI access token for '{tokenScope}': {exception.Message}"
            );
        }

        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private async Task<JsonNode> ResolveSetAsync(
        string apiBaseUrl,
        string applicationIdUri,
        JsonNode requestBody,
        CancellationToken cancellationToken)
    {
        using var httpClient = await CreateHttpClientAsync(applicationIdUri, cancellationToken);
        using var content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(
            $"{apiBaseUrl.TrimEnd('/')}/api/resolve-set",
            content,
            cancellationToken
        );
        return await ReadJsonResponseAsync(response, cancellationToken);
    }

    private static async Task<JsonNode> ReadJsonResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CliException(BuildHttpErrorMessage(response, payloadText));

        try
        {
            return JsonNode.Parse(payloadText, documentOptions: default, nodeOptions: default)
                ?? throw new CliException("PowerPack API returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new CliException($"PowerPack API returned invalid JSON: {exception.Message}");
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
