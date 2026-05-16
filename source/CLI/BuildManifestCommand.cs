using System.ComponentModel;
using System.Text.Json;
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
            var dataverseClient = new DataverseSolutionClient(httpClient, credential);
            var environmentUrl = DataverseSolutionClient.NormalizeEnvironmentUrl(settings.EnvironmentUrl);
            var powerPlatformEnvironmentId = await dataverseClient.ResolvePowerPlatformEnvironmentIdAsync(
                environmentUrl,
                cancellationToken);

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
                packageBytes = await dataverseClient.ExportSolutionAsync(
                    environmentUrl,
                    new DataverseSolutionExportOptions
                    {
                        SolutionName = settings.SolutionName!,
                        Managed = false,
                    },
                    cancellationToken);
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
        catch (PowerPackServiceException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
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
