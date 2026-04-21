using System.ComponentModel;
using System.Text.Json;
using PowerPack.Models;
using PowerPack.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class ResolveDeploymentGraphCommand : AsyncCommand<ResolveDeploymentGraphCommand.Settings>
{
    private readonly PowerPackCliClient _client = new();
    private readonly MissingDependenciesParser _parser = new();
    private readonly DependencyDeploymentGraphBuilder _graphBuilder = new();

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--api-base-url <URL>")]
        [Description("PowerPack API base URL.")]
        public required string ApiBaseUrl { get; init; }

        [CommandOption("--application-id-uri <URI>")]
        [Description("Entra application ID URI used to request the PowerPack API token scope.")]
        public required string ApplicationIdUri { get; init; }

        [CommandOption("--missingdependencies <PATH>")]
        [Description("Source solution missingdependencies.yml path.")]
        public required string MissingDependenciesPath { get; init; }

        [CommandOption("--output <PATH>")]
        [Description("Optional path to write the deployment graph JSON.")]
        public string? OutputPath { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
                return ValidationResult.Error("--api-base-url is required.");

            if (string.IsNullOrWhiteSpace(ApplicationIdUri))
                return ValidationResult.Error("--application-id-uri is required.");

            if (string.IsNullOrWhiteSpace(MissingDependenciesPath))
                return ValidationResult.Error("--missingdependencies is required.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var missingDependenciesFile = new FileInfo(settings.MissingDependenciesPath);
            if (!missingDependenciesFile.Exists)
                throw new CliException($"MissingDependencies file was not found: {missingDependenciesFile.FullName}");

            var content = await File.ReadAllTextAsync(missingDependenciesFile.FullName);
            var roots = _parser.Parse(content, missingDependenciesFile.FullName);
            var resolution = await _client.ResolveSetResultAsync(
                settings.ApiBaseUrl,
                settings.ApplicationIdUri,
                new ResolveSetRequest
                {
                    Solutions = roots,
                },
                CancellationToken.None);

            var graph = _graphBuilder.Build(resolution);
            var json = JsonSerializer.Serialize(
                graph,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
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
        catch (PowerPackValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
