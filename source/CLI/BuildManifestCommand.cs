using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using PowerPack.Models;
using PowerPack.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class BuildManifestCommand : AsyncCommand<BuildManifestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--environment-url <URL>")]
        [Description("Dataverse environment URL used for connector metadata and, when --solution is used, solution export.")]
        public required string EnvironmentUrl { get; init; }

        [CommandOption("--package <PATH>")]
        [Description("Managed solution zip to inspect.")]
        public string? PackagePath { get; init; }

        [CommandOption("--solution <NAME>")]
        [Description("Unmanaged solution unique name to export from the Dataverse environment before building the manifest.")]
        public string? SolutionName { get; init; }

        [CommandOption("--expected-version <VERSION>")]
        [Description("Optional expected manifest version.")]
        public string? ExpectedVersion { get; init; }

        [CommandOption("--expected-package-name <NAME>")]
        [Description("Optional expected package transport name.")]
        public string? ExpectedPackageName { get; init; }

        [CommandOption("--output <PATH>")]
        [Description("Optional path to write the manifest JSON.")]
        public string? OutputPath { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(EnvironmentUrl))
                return ValidationResult.Error("--environment-url is required.");

            var hasPackage = !string.IsNullOrWhiteSpace(PackagePath);
            var hasSolution = !string.IsNullOrWhiteSpace(SolutionName);
            if (hasPackage == hasSolution)
                return ValidationResult.Error("Specify exactly one of --package or --solution.");

            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var credential = new AzureCliCredential();
            using var httpClient = new HttpClient();
            var environmentUrl = NormalizeEnvironmentUrl(settings.EnvironmentUrl);
            var powerPlatformEnvironmentId = await ResolvePowerPlatformEnvironmentIdAsync(
                httpClient,
                credential,
                environmentUrl,
                cancellationToken
            );

            byte[] packageBytes;
            if (!string.IsNullOrWhiteSpace(settings.PackagePath))
            {
                var packageFile = new FileInfo(settings.PackagePath);
                if (!packageFile.Exists)
                    throw new CliException($"Package zip was not found: {packageFile.FullName}");

                packageBytes = await File.ReadAllBytesAsync(packageFile.FullName);
            }
            else
            {
                packageBytes = await ExportUnmanagedSolutionAsync(
                    httpClient,
                    credential,
                    environmentUrl,
                    settings.SolutionName!,
                    cancellationToken
                );
            }

            var connectorMetadataClient = new PowerPlatformConnectorMetadataClient(
                httpClient,
                credential
            );
            var manifestBuilder = new SolutionPackageManifestBuilder(connectorMetadataClient);
            var manifest = await manifestBuilder.BuildAsync(
                packageBytes,
                powerPlatformEnvironmentId,
                cancellationToken
            );

            ValidateExpectedVersion(manifest, settings.ExpectedVersion);
            ValidateExpectedPackageName(manifest, settings.ExpectedPackageName);

            var json = JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }
            );

            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                var outputPath = Path.GetFullPath(settings.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
            }
            else
                Console.Out.WriteLine(json);

            return 0;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (PowerPackValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static string NormalizeEnvironmentUrl(string environmentUrl)
    {
        var trimmed = environmentUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CliException($"--environment-url must be an absolute HTTPS Dataverse URL: {environmentUrl}");
        return trimmed;
    }

    internal static async Task<string> ResolvePowerPlatformEnvironmentIdAsync(
        HttpClient httpClient,
        TokenCredential credential,
        string environmentUrl,
        CancellationToken cancellationToken)
    {
        var response = await SendDataverseRequestAsync(
            httpClient,
            credential,
            environmentUrl,
            HttpMethod.Get,
            "/api/data/v9.2/RetrieveCurrentOrganization(AccessType=@p1)?@p1=Microsoft.Dynamics.CRM.EndpointAccessType'Default'",
            null,
            cancellationToken
        );

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new CliException("RetrieveCurrentOrganization did not return a JSON object.");
        var environmentId = json["Detail"]?["EnvironmentId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new CliException("RetrieveCurrentOrganization response did not include Detail.EnvironmentId.");
        return environmentId.Trim();
    }

    private static async Task<byte[]> ExportUnmanagedSolutionAsync(
        HttpClient httpClient,
        TokenCredential credential,
        string environmentUrl,
        string solutionName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new CliException("--solution must be a non-empty solution unique name.");

        var body = new JsonObject
        {
            ["SolutionName"] = solutionName.Trim(),
            ["Managed"] = false,
        };
        var response = await SendDataverseRequestAsync(
            httpClient,
            credential,
            environmentUrl,
            HttpMethod.Post,
            "/api/data/v9.2/ExportSolution",
            body.ToJsonString(),
            cancellationToken
        );

        var json = JsonNode.Parse(response) as JsonObject
            ?? throw new CliException("ExportSolution did not return a JSON object.");
        var packageBase64 = json["ExportSolutionFile"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(packageBase64))
            throw new CliException("ExportSolution response did not include ExportSolutionFile.");

        try
        {
            return Convert.FromBase64String(packageBase64);
        }
        catch (FormatException exception)
        {
            throw new CliException($"ExportSolutionFile was not valid base64: {exception.Message}");
        }
    }

    private static async Task<string> SendDataverseRequestAsync(
        HttpClient httpClient,
        TokenCredential credential,
        string environmentUrl,
        HttpMethod method,
        string relativePath,
        string? body,
        CancellationToken cancellationToken)
    {
        var accessToken = await credential.GetTokenAsync(
            new TokenRequestContext([$"{environmentUrl}/.default"]),
            cancellationToken
        );

        using var request = new HttpRequestMessage(method, environmentUrl + relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("OData-MaxVersion", "4.0");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CliException(
                $"Dataverse {method} {relativePath} failed: HTTP {(int)response.StatusCode}: {payload}"
            );
        return payload;
    }

    private static void ValidateExpectedVersion(SolutionManifest manifest, string? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
            return;

        var normalizedExpectedVersion = SolutionVersion.Parse(expectedVersion.Trim()).ToString();
        if (!string.Equals(manifest.Version, normalizedExpectedVersion, StringComparison.Ordinal))
        {
            throw new CliException(
                $"Manifest version '{manifest.Version}' does not match expected version '{normalizedExpectedVersion}'."
            );
        }
    }

    private static void ValidateExpectedPackageName(SolutionManifest manifest, string? expectedPackageName)
    {
        if (string.IsNullOrWhiteSpace(expectedPackageName))
            return;

        var actualPackageName = manifest.Metadata?["package_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(actualPackageName))
            throw new CliException("Manifest metadata.package_name is missing.");

        var normalizedExpectedPackageName = expectedPackageName.Trim().ToLowerInvariant();
        if (!string.Equals(actualPackageName, normalizedExpectedPackageName, StringComparison.Ordinal))
        {
            throw new CliException(
                $"Manifest package name '{actualPackageName}' does not match expected package name '{normalizedExpectedPackageName}'."
            );
        }
    }
}
