using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using PowerPack.Models;
using PowerPack.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class ResolveSetCommand : AsyncCommand<ResolveSetCommand.Settings>
{
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

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var requestFile = new FileInfo(settings.RequestPath);
            if (!requestFile.Exists)
                throw new CliException($"Resolve-set request file was not found: {requestFile.FullName}");

            ResolveSetRequest requestBody;
            try
            {
                requestBody = JsonNode.Parse(await File.ReadAllTextAsync(requestFile.FullName, cancellationToken))
                    ?.Deserialize<ResolveSetRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new CliException($"Resolve-set request file '{requestFile.FullName}' is empty.");
            }
            catch (JsonException exception)
            {
                throw new CliException(
                    $"Resolve-set request file '{requestFile.FullName}' is invalid JSON: {exception.Message}");
            }

            using var httpClient = new HttpClient();
            var client = new PowerPackApiClient(
                httpClient,
                new PowerPackApiClientOptions
                {
                    Credential = new AzureCliCredential(),
                    ApplicationIdUri = settings.ApplicationIdUri,
                });
            var payload = await client.ResolveSetAsync(settings.ApiBaseUrl, requestBody, cancellationToken);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
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
        catch (PowerPackServiceException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
