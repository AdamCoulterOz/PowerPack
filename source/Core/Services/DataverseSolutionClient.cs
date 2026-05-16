using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace PowerPack.Services;

public sealed class DataverseSolutionClient(HttpClient httpClient, TokenCredential credential)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly TokenCredential _credential = credential;

    public static string NormalizeEnvironmentUrl(string environmentUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        var trimmed = environmentUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new PowerPackServiceException($"Dataverse environment URL must be an absolute HTTPS URL: {environmentUrl}");
        return trimmed;
    }

    public async Task<string> ResolvePowerPlatformEnvironmentIdAsync(
        string environmentUrl,
        CancellationToken cancellationToken)
    {
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Get,
            "/api/data/v9.2/RetrieveCurrentOrganization(AccessType=@p1)?@p1=Microsoft.Dynamics.CRM.EndpointAccessType'Default'",
            null,
            cancellationToken);

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new PowerPackServiceException("RetrieveCurrentOrganization did not return a JSON object.");
        var environmentId = json["Detail"]?["EnvironmentId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new PowerPackServiceException("RetrieveCurrentOrganization response did not include Detail.EnvironmentId.");
        return environmentId.Trim();
    }

    public async Task<byte[]> ExportSolutionAsync(
        string environmentUrl,
        DataverseSolutionExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SolutionName))
            throw new PowerPackServiceException("Solution export requires a non-empty solution unique name.");

        var body = new JsonObject
        {
            ["SolutionName"] = options.SolutionName.Trim(),
            ["Managed"] = options.Managed,
        };
        AddOptional(body, "ExportAutoNumberingSettings", options.ExportAutoNumberingSettings);
        AddOptional(body, "ExportCalendarSettings", options.ExportCalendarSettings);
        AddOptional(body, "ExportCustomizationSettings", options.ExportCustomizationSettings);
        AddOptional(body, "ExportEmailTrackingSettings", options.ExportEmailTrackingSettings);
        AddOptional(body, "ExportGeneralSettings", options.ExportGeneralSettings);
        AddOptional(body, "ExportIsvConfig", options.ExportIsvConfig);
        AddOptional(body, "ExportMarketingSettings", options.ExportMarketingSettings);
        AddOptional(body, "ExportOutlookSynchronizationSettings", options.ExportOutlookSynchronizationSettings);
        AddOptional(body, "ExportRelationshipRoles", options.ExportRelationshipRoles);
        AddOptional(body, "ExportSales", options.ExportSales);

        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            "/api/data/v9.2/ExportSolution",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new PowerPackServiceException("ExportSolution did not return a JSON object.");
        var packageBase64 = json["ExportSolutionFile"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(packageBase64))
            throw new PowerPackServiceException("ExportSolution response did not include ExportSolutionFile.");

        try
        {
            return Convert.FromBase64String(packageBase64);
        }
        catch (FormatException exception)
        {
            throw new PowerPackServiceException($"ExportSolutionFile was not valid base64: {exception.Message}", exception);
        }
    }

    public async Task<DataverseSolutionImportJob> StartImportSolutionAsync(
        string environmentUrl,
        byte[] packageBytes,
        DataverseSolutionImportOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        if (packageBytes.Length == 0)
            throw new PowerPackServiceException("Solution import requires a non-empty package zip.");

        options ??= new DataverseSolutionImportOptions();
        var importJobId = options.ImportJobId ?? Guid.NewGuid();
        var body = new JsonObject
        {
            ["OverwriteUnmanagedCustomizations"] = options.OverwriteUnmanagedCustomizations,
            ["PublishWorkflows"] = options.PublishWorkflows,
            ["CustomizationFile"] = Convert.ToBase64String(packageBytes),
            ["ImportJobId"] = importJobId,
        };
        AddOptional(body, "SkipProductUpdateDependencies", options.SkipProductUpdateDependencies);
        AddOptional(body, "HoldingSolution", options.HoldingSolution);
        if (options.ComponentParameters is not null)
            body["ComponentParameters"] = options.ComponentParameters.DeepClone();
        if (options.SolutionParameters is not null)
            body["SolutionParameters"] = options.SolutionParameters.DeepClone();

        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            "/api/data/v9.2/ImportSolutionAsync",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new PowerPackServiceException("ImportSolutionAsync did not return a JSON object.");
        var asyncOperationId = ReadGuid(json, "AsyncOperationId");
        var importJobKey = json["ImportJobKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(importJobKey))
            throw new PowerPackServiceException("ImportSolutionAsync response did not include ImportJobKey.");

        return new DataverseSolutionImportJob(asyncOperationId, importJobKey, importJobId);
    }

    public async Task<DataverseAsyncOperationStatus> GetAsyncOperationStatusAsync(
        string environmentUrl,
        Guid asyncOperationId,
        CancellationToken cancellationToken)
    {
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Get,
            $"/api/data/v9.2/asyncoperations({asyncOperationId:D})?$select=statecode,statuscode,message,friendlymessage,errorcode",
            null,
            cancellationToken);

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new PowerPackServiceException("AsyncOperation lookup did not return a JSON object.");
        return new DataverseAsyncOperationStatus(
            asyncOperationId,
            ReadInt32(json, "statecode"),
            ReadInt32(json, "statuscode"),
            json["message"]?.GetValue<string>(),
            json["friendlymessage"]?.GetValue<string>(),
            json["errorcode"]?.GetValue<int?>());
    }

    public async Task<DataverseAsyncOperationStatus> WaitForAsyncOperationAsync(
        string environmentUrl,
        Guid asyncOperationId,
        TimeSpan timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new PowerPackServiceException("Async operation wait timeout must be greater than zero.");

        var deadline = DateTimeOffset.UtcNow + timeout;
        var interval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(10));
        if (interval <= TimeSpan.Zero)
            throw new PowerPackServiceException("Async operation poll interval must be greater than zero.");

        while (true)
        {
            var status = await GetAsyncOperationStatusAsync(environmentUrl, asyncOperationId, cancellationToken);
            if (status.IsCompleted)
                return status;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new PowerPackServiceException(
                    $"Dataverse async operation '{asyncOperationId:D}' did not complete within {timeout}.");

            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken);
        }
    }

    public async Task<DataverseAsyncOperationStatus> ImportSolutionAndWaitAsync(
        string environmentUrl,
        byte[] packageBytes,
        DataverseSolutionImportOptions? options,
        TimeSpan timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken)
    {
        var job = await StartImportSolutionAsync(environmentUrl, packageBytes, options, cancellationToken);
        var status = await WaitForAsyncOperationAsync(
            environmentUrl,
            job.AsyncOperationId,
            timeout,
            pollInterval,
            cancellationToken);

        if (!status.Succeeded)
            throw new PowerPackServiceException(
                $"Dataverse solution import failed with async status {status.StateCode}/{status.StatusCode}: {status.Message}");

        if (options?.PublishAllChangesAfterImport ?? false)
            await PublishAllXmlAsync(environmentUrl, cancellationToken);

        return status;
    }

    public async Task PublishAllXmlAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            "/api/data/v9.2/PublishAllXml",
            "{}",
            cancellationToken);
    }

    private async Task<string> SendDataverseRequestAsync(
        string environmentUrl,
        HttpMethod method,
        string relativePath,
        string? body,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironmentUrl = NormalizeEnvironmentUrl(environmentUrl);
        var accessToken = await _credential.GetTokenAsync(
            new TokenRequestContext([$"{normalizedEnvironmentUrl}/.default"]),
            cancellationToken);

        using var request = new HttpRequestMessage(method, normalizedEnvironmentUrl + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("OData-MaxVersion", "4.0");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PowerPackServiceException(
                $"Dataverse {method} {relativePath} failed: HTTP {(int)response.StatusCode}: {payload}");
        return payload;
    }

    private static void AddOptional(JsonObject body, string propertyName, bool? value)
    {
        if (value is not null)
            body[propertyName] = value.Value;
    }

    private static Guid ReadGuid(JsonObject json, string propertyName)
    {
        var value = json[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var parsed))
            throw new PowerPackServiceException($"{propertyName} was missing or was not a GUID.");
        return parsed;
    }

    private static int ReadInt32(JsonObject json, string propertyName)
    {
        if (json[propertyName] is null)
            throw new PowerPackServiceException($"{propertyName} was missing from the Dataverse response.");
        return json[propertyName]!.GetValue<int>();
    }
}

public sealed class DataverseSolutionExportOptions
{
    public required string SolutionName { get; init; }

    public bool Managed { get; init; }

    public bool? ExportAutoNumberingSettings { get; init; }

    public bool? ExportCalendarSettings { get; init; }

    public bool? ExportCustomizationSettings { get; init; }

    public bool? ExportEmailTrackingSettings { get; init; }

    public bool? ExportGeneralSettings { get; init; }

    public bool? ExportIsvConfig { get; init; }

    public bool? ExportMarketingSettings { get; init; }

    public bool? ExportOutlookSynchronizationSettings { get; init; }

    public bool? ExportRelationshipRoles { get; init; }

    public bool? ExportSales { get; init; }
}

public sealed class DataverseSolutionImportOptions
{
    public bool OverwriteUnmanagedCustomizations { get; init; } = true;

    public bool PublishWorkflows { get; init; }

    public Guid? ImportJobId { get; init; }

    public bool? SkipProductUpdateDependencies { get; init; }

    public bool? HoldingSolution { get; init; }

    public JsonArray? ComponentParameters { get; init; }

    public JsonObject? SolutionParameters { get; init; }

    public bool PublishAllChangesAfterImport { get; init; } = true;
}

public sealed record DataverseSolutionImportJob(
    Guid AsyncOperationId,
    string ImportJobKey,
    Guid ImportJobId);

public sealed record DataverseAsyncOperationStatus(
    Guid AsyncOperationId,
    int StateCode,
    int StatusCode,
    string? Message,
    string? FriendlyMessage,
    int? ErrorCode)
{
    public bool IsCompleted => StateCode == 3;

    public bool Succeeded => StateCode == 3 && StatusCode == 30;
}
