using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace PowerPack.Services;

public sealed class DataverseSolutionClient(HttpClient httpClient, TokenCredential credential)
{
    private const string DataverseApiVersion = "v9.2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly TokenCredential _credential = credential;

    public static string NormalizeEnvironmentUrl(string environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new PowerPackServiceException("Dataverse environment URL must be a non-empty string.");

        var trimmed = environmentUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new PowerPackServiceException($"Dataverse environment URL must be an absolute HTTPS URL: {environmentUrl}");

        return trimmed;
    }

    public async Task<string> ResolvePowerPlatformEnvironmentIdAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Get,
            $"/api/data/{DataverseApiVersion}/RetrieveCurrentOrganization(AccessType=@p1)?@p1=Microsoft.Dynamics.CRM.EndpointAccessType'Default'",
            null,
            cancellationToken);

        var json = ParseJsonObject(response, "RetrieveCurrentOrganization");
        var environmentId = json["Detail"]?["EnvironmentId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new PowerPackServiceException("RetrieveCurrentOrganization response did not include Detail.EnvironmentId.");

        return environmentId.Trim();
    }

    public async Task SetSolutionVersionAsync(
        string environmentUrl,
        string solutionUniqueName,
        string solutionVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedEnvironmentUrl = NormalizeEnvironmentUrl(environmentUrl);
        var normalizedSolutionName = Required(solutionUniqueName, "Solution unique name");
        var normalizedVersion = Required(solutionVersion, "Solution version");
        var solutionId = await GetSolutionIdAsync(normalizedEnvironmentUrl, normalizedSolutionName, cancellationToken);

        var body = new JsonObject
        {
            ["version"] = normalizedVersion,
        };

        await SendDataverseRequestAsync(
            normalizedEnvironmentUrl,
            HttpMethod.Patch,
            $"/api/data/{DataverseApiVersion}/solutions({solutionId})",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        var versionAfterUpdate = await GetSolutionVersionAsync(normalizedEnvironmentUrl, normalizedSolutionName, cancellationToken);
        if (!string.Equals(versionAfterUpdate, normalizedVersion, StringComparison.Ordinal))
        {
            throw new PowerPackServiceException(
                $"SetSolutionVersion verification failed for '{normalizedSolutionName}'. Expected '{normalizedVersion}' and found '{versionAfterUpdate}'.");
        }
    }

    public async Task<byte[]> ExportSolutionAsync(
        string environmentUrl,
        string solutionUniqueName,
        bool managed,
        bool useAsync = false,
        TimeSpan? maxAsyncWaitTime = null,
        CancellationToken cancellationToken = default)
    {
        return await ExportSolutionAsync(
            environmentUrl,
            new DataverseSolutionExportOptions
            {
                SolutionName = solutionUniqueName,
                Managed = managed,
                UseAsync = useAsync,
                MaxAsyncWaitTime = maxAsyncWaitTime,
            },
            cancellationToken);
    }

    public async Task<byte[]> ExportSolutionAsync(
        string environmentUrl,
        DataverseSolutionExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedEnvironmentUrl = NormalizeEnvironmentUrl(environmentUrl);
        var normalizedSolutionName = Required(options.SolutionName, "Solution export solution unique name");

        return options.UseAsync
            ? await ExportSolutionWithAsyncJobAsync(
                normalizedEnvironmentUrl,
                options,
                normalizedSolutionName,
                options.MaxAsyncWaitTime ?? TimeSpan.FromMinutes(60),
                cancellationToken)
            : await ExportSolutionImmediatelyAsync(
                normalizedEnvironmentUrl,
                options,
                normalizedSolutionName,
                cancellationToken);
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
            $"/api/data/{DataverseApiVersion}/ImportSolutionAsync",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        var json = ParseJsonObject(response, "ImportSolutionAsync");
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
            $"/api/data/{DataverseApiVersion}/asyncoperations({asyncOperationId})?$select=statecode,statuscode,errorcode,message,messagename,friendlymessage,correlationid",
            null,
            cancellationToken);

        var json = ParseJsonObject(response, "AsyncOperation lookup");
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
            $"/api/data/{DataverseApiVersion}/PublishAllXml",
            "{}",
            cancellationToken);
    }

    private async Task<byte[]> ExportSolutionImmediatelyAsync(
        string environmentUrl,
        DataverseSolutionExportOptions options,
        string solutionUniqueName,
        CancellationToken cancellationToken)
    {
        var body = CreateExportSolutionBody(options, solutionUniqueName);
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            $"/api/data/{DataverseApiVersion}/ExportSolution",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        return ExportSolutionFile(ParseJsonObject(response, "ExportSolution"), "ExportSolution");
    }

    private async Task<byte[]> ExportSolutionWithAsyncJobAsync(
        string environmentUrl,
        DataverseSolutionExportOptions options,
        string solutionUniqueName,
        TimeSpan maxAsyncWaitTime,
        CancellationToken cancellationToken)
    {
        if (maxAsyncWaitTime <= TimeSpan.Zero)
            throw new PowerPackServiceException("Maximum async wait time must be greater than zero.");

        var body = CreateExportSolutionBody(options, solutionUniqueName);
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            $"/api/data/{DataverseApiVersion}/ExportSolutionAsync",
            body.ToJsonString(JsonOptions),
            cancellationToken);

        var json = ParseJsonObject(response, "ExportSolutionAsync");
        var asyncOperationId = ReadGuid(json, "AsyncOperationId");
        var exportJobId = ReadGuid(json, "ExportJobId");
        var status = await WaitForAsyncOperationAsync(
            environmentUrl,
            asyncOperationId,
            maxAsyncWaitTime,
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!status.Succeeded)
            throw new PowerPackServiceException(
                $"Dataverse solution export failed with async status {status.StateCode}/{status.StatusCode}: {status.Message}");

        var downloadBody = new JsonObject
        {
            ["ExportJobId"] = exportJobId,
        };
        var download = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Post,
            $"/api/data/{DataverseApiVersion}/DownloadSolutionExportData",
            downloadBody.ToJsonString(JsonOptions),
            cancellationToken);

        return ExportSolutionFile(ParseJsonObject(download, "DownloadSolutionExportData"), "DownloadSolutionExportData");
    }

    private async Task<Guid> GetSolutionIdAsync(
        string environmentUrl,
        string solutionUniqueName,
        CancellationToken cancellationToken)
    {
        var solution = await GetSolutionAsync(environmentUrl, solutionUniqueName, cancellationToken);
        var solutionId = solution["solutionid"]?.GetValue<string>();
        if (!Guid.TryParse(solutionId, out var parsed))
            throw new PowerPackServiceException($"Solution '{solutionUniqueName}' did not include a valid solutionid.");

        return parsed;
    }

    private async Task<string> GetSolutionVersionAsync(
        string environmentUrl,
        string solutionUniqueName,
        CancellationToken cancellationToken)
    {
        var solution = await GetSolutionAsync(environmentUrl, solutionUniqueName, cancellationToken);
        var version = solution["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version))
            throw new PowerPackServiceException($"Solution '{solutionUniqueName}' did not include a version.");

        return version.Trim();
    }

    private async Task<JsonObject> GetSolutionAsync(
        string environmentUrl,
        string solutionUniqueName,
        CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString($"uniquename eq '{ODataString(solutionUniqueName)}'");
        var response = await SendDataverseRequestAsync(
            environmentUrl,
            HttpMethod.Get,
            $"/api/data/{DataverseApiVersion}/solutions?$select=solutionid,uniquename,version&$filter={filter}",
            null,
            cancellationToken);

        var json = ParseJsonObject(response, "Retrieve solution");
        var values = json["value"] as JsonArray
            ?? throw new PowerPackServiceException("Retrieve solution response did not include a value array.");
        if (values.Count == 0)
            throw new PowerPackServiceException($"Solution '{solutionUniqueName}' was not found.");
        if (values.Count > 1)
            throw new PowerPackServiceException($"Solution '{solutionUniqueName}' matched multiple solution rows.");

        return values[0] as JsonObject
            ?? throw new PowerPackServiceException($"Solution '{solutionUniqueName}' response row was not a JSON object.");
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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);
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

    private static JsonObject CreateExportSolutionBody(
        DataverseSolutionExportOptions options,
        string solutionUniqueName)
    {
        var body = new JsonObject
        {
            ["SolutionName"] = solutionUniqueName,
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
        return body;
    }

    private static byte[] ExportSolutionFile(JsonObject json, string operation)
    {
        var packageBase64 = json["ExportSolutionFile"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(packageBase64))
            throw new PowerPackServiceException($"{operation} response did not include ExportSolutionFile.");

        try
        {
            return Convert.FromBase64String(packageBase64);
        }
        catch (FormatException exception)
        {
            throw new PowerPackServiceException($"{operation} returned an invalid ExportSolutionFile payload: {exception.Message}", exception);
        }
    }

    private static JsonObject ParseJsonObject(string json, string operation)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new PowerPackServiceException($"{operation} did not return a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new PowerPackServiceException($"{operation} returned invalid JSON: {exception.Message}", exception);
        }
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

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new PowerPackServiceException($"{name} must be a non-empty string.")
            : value.Trim();

    private static string ODataString(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public sealed class DataverseSolutionExportOptions
{
    public required string SolutionName { get; init; }

    public bool Managed { get; init; }

    public bool UseAsync { get; init; }

    public TimeSpan? MaxAsyncWaitTime { get; init; }

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
