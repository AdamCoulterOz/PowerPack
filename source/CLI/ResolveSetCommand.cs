using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class ResolveSetCommand : AsyncCommand<ResolveSetCommand.Settings>
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

        [CommandOption("--request <PATH>")]
        [Description("JSON file containing the resolve-set request payload.")]
        public required string RequestPath { get; init; }

        [CommandOption("--output <PATH>")]
        [Description("Optional path to write the resolve-set response JSON.")]
        public string? OutputPath { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
                return ValidationResult.Error("--api-base-url is required.");

            if (string.IsNullOrWhiteSpace(ApplicationIdUri))
                return ValidationResult.Error("--application-id-uri is required.");

            if (string.IsNullOrWhiteSpace(RequestPath))
                return ValidationResult.Error("--request is required.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var payload = await _client.ResolveSetAsync(
                settings.ApiBaseUrl,
                settings.ApplicationIdUri,
                settings.RequestPath,
                CancellationToken.None
            );

            var json = payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });

            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
                await File.WriteAllTextAsync(settings.OutputPath, json + Environment.NewLine);
            else
                Console.Out.WriteLine(json);

            return 0;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
