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
        [CommandOption("--package <PATH>")]
        [Description("Managed solution zip to inspect.")]
        public required string PackagePath { get; init; }

        [CommandOption("--power-platform-environment-id <ID>")]
        [Description("Power Platform environment id used for connector metadata enrichment when required.")]
        public string? PowerPlatformEnvironmentId { get; init; }

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
            if (string.IsNullOrWhiteSpace(PackagePath))
                return ValidationResult.Error("--package is required.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var packageFile = new FileInfo(settings.PackagePath);
            if (!packageFile.Exists)
                throw new CliException($"Package zip was not found: {packageFile.FullName}");

            var packageBytes = await File.ReadAllBytesAsync(packageFile.FullName);
            using var httpClient = new HttpClient();
            var connectorMetadataClient = new PowerPlatformConnectorMetadataClient(
                httpClient,
                new AzureCliCredential()
            );
            var manifestBuilder = new SolutionPackageManifestBuilder(connectorMetadataClient);
            var manifest = await manifestBuilder.BuildAsync(
                packageBytes,
                string.IsNullOrWhiteSpace(settings.PowerPlatformEnvironmentId)
                    ? null
                    : settings.PowerPlatformEnvironmentId.Trim(),
                CancellationToken.None
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
