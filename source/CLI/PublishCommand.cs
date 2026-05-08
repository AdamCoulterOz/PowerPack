using System.ComponentModel;
using System.Text.Json;
using Azure.Identity;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class PublishCommand : AsyncCommand<PublishCommand.Settings>
{
    private readonly PowerPackCliClient _client = new();

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--api-base-url <URL>")]
        [Description("PowerPack API base URL.")]
        public required string ApiBaseUrl { get; init; }

        [CommandOption("--application-id-uri <URI>")]
        [Description("Entra application ID URI used to request the PowerPack API token scope.")]
        public required string ApplicationIdUri { get; init; }

        [CommandOption("--package <PATH>")]
        [Description("Managed solution zip to upload.")]
        public required string PackagePath { get; init; }

        [CommandOption("--quality <QUALITY>")]
        [Description("Package quality tag: local, prerelease, or release.")]
        public required string Quality { get; init; }

        [CommandOption("--environment-url <URL>")]
        [Description("Dataverse environment URL used to resolve the Power Platform environment id for connector metadata enrichment.")]
        public required string EnvironmentUrl { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
                return ValidationResult.Error("--api-base-url is required.");

            if (string.IsNullOrWhiteSpace(ApplicationIdUri))
                return ValidationResult.Error("--application-id-uri is required.");

            if (string.IsNullOrWhiteSpace(PackagePath))
                return ValidationResult.Error("--package is required.");

            if (string.IsNullOrWhiteSpace(Quality))
                return ValidationResult.Error("--quality is required.");

            if (string.IsNullOrWhiteSpace(EnvironmentUrl))
                return ValidationResult.Error("--environment-url is required.");

            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var environmentUrl = BuildManifestCommand.NormalizeEnvironmentUrl(settings.EnvironmentUrl);
            using var httpClient = new HttpClient();
            var powerPlatformEnvironmentId = await BuildManifestCommand.ResolvePowerPlatformEnvironmentIdAsync(
                httpClient,
                new AzureCliCredential(),
                environmentUrl,
                cancellationToken
            );

            var payload = await _client.PublishAsync(
                settings.ApiBaseUrl,
                settings.ApplicationIdUri,
                settings.PackagePath,
                settings.Quality,
                powerPlatformEnvironmentId,
                cancellationToken
            );
            Console.Out.WriteLine(payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            }));
            return 0;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
