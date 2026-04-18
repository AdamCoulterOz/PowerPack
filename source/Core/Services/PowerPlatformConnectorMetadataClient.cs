using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using PowerPack.Models;

namespace PowerPack.Services; 

public sealed class PowerPlatformConnectorMetadataClient(HttpClient httpClient, TokenCredential credential)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly TokenCredential _credential = credential;

    public async Task<JsonObject> GetConnectorMetadataAsync(
        string environmentId,
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        var accessToken = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://api.powerplatform.com/.default"]),
            cancellationToken
        );

        var requestUri =
            $"https://api.powerplatform.com/connectivity/environments/{Uri.EscapeDataString(environmentId)}/connectors/{Uri.EscapeDataString(connectorName)}" +
            $"?$filter={Uri.EscapeDataString($"environment eq '{environmentId}'")}&api-version=2022-03-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PowerPackValidationException(
                $"Failed to fetch connector metadata for '{connectorName}' in environment '{environmentId}': " +
                $"HTTP {(int)response.StatusCode}: {payload}"
            );

        var jsonNode = JsonNode.Parse(payload) as JsonObject;
        return jsonNode ?? throw new PowerPackValidationException(
            $"Connector metadata response for '{connectorName}' was not a JSON object."
        );
    }
}
