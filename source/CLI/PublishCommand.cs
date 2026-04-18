using System.ComponentModel;
using System.Text.Json;
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

        [CommandOption("--power-platform-environment-id <ID>")]
        [Description("Power Platform environment id used for connector metadata enrichment when required.")]
        public string? PowerPlatformEnvironmentId { get; init; }

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

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var payload = await _client.PublishAsync(
                settings.ApiBaseUrl,
                settings.ApplicationIdUri,
                settings.PackagePath,
                settings.Quality,
                settings.PowerPlatformEnvironmentId,
                CancellationToken.None
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
