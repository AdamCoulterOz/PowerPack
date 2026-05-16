using System.ComponentModel;
using Azure.Identity;
using PowerPack.Models;
using PowerPack.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class InstallPackageCommand : AsyncCommand<InstallPackageCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--api-base-url <URL>")]
        [Description("PowerPack API base URL.")]
        public required string ApiBaseUrl { get; init; }

        [CommandOption("--application-id-uri <URI>")]
        [Description("Entra application ID URI used to request the PowerPack API token scope.")]
        public required string ApplicationIdUri { get; init; }

        [CommandOption("--package <NAME>")]
        [Description("Root PowerPack package/solution name to install.")]
        public required string PackageName { get; init; }

        [CommandOption("--version <VERSION>")]
        [Description("Minimum root package version to resolve and install.")]
        public required string Version { get; init; }

        [CommandOption("--environment <URL>")]
        [Description("Target Dataverse environment URL.")]
        public required string Environment { get; init; }

        [CommandOption("--download-directory <PATH>")]
        [Description("Optional directory for downloaded package zips. Defaults to a temporary directory.")]
        public string? DownloadDirectory { get; init; }

        [CommandOption("--settings-directory <PATH>")]
        [Description("Optional directory checked for PAC deployment settings files; matching files fail because PAC settings are not a runtime import contract.")]
        public string? SettingsDirectory { get; init; }

        [CommandOption("--max-async-wait-time <MINUTES>")]
        [Description("Dataverse ImportSolutionAsync wait time in minutes.")]
        public int MaxAsyncWaitTimeMinutes { get; init; } = 60;

        [CommandOption("--no-force-overwrite")]
        [Description("Do not overwrite unmanaged customizations during solution import.")]
        public bool NoForceOverwrite { get; init; }

        [CommandOption("--no-publish-changes")]
        [Description("Do not publish all changes after solution import.")]
        public bool NoPublishChanges { get; init; }

        [CommandOption("--publish-workflows")]
        [Description("Activate workflows included in each imported solution.")]
        public bool PublishWorkflows { get; init; }

        [CommandOption("--dry-run")]
        [Description("Resolve and show the install plan without downloading or importing packages.")]
        public bool DryRun { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
                return ValidationResult.Error("--api-base-url is required.");

            if (string.IsNullOrWhiteSpace(ApplicationIdUri))
                return ValidationResult.Error("--application-id-uri is required.");

            if (string.IsNullOrWhiteSpace(PackageName))
                return ValidationResult.Error("--package is required.");

            if (string.IsNullOrWhiteSpace(Version))
                return ValidationResult.Error("--version is required.");

            if (string.IsNullOrWhiteSpace(Environment))
                return ValidationResult.Error("--environment is required.");

            try
            {
                DataverseSolutionClient.NormalizeEnvironmentUrl(Environment);
            }
            catch (PowerPackServiceException exception)
            {
                return ValidationResult.Error(exception.Message);
            }

            if (MaxAsyncWaitTimeMinutes <= 0)
                return ValidationResult.Error("--max-async-wait-time must be greater than zero.");

            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var requestedVersion = SolutionVersion.Parse(settings.Version.Trim()).ToString();
            var graph = await ResolveGraphAsync(settings.PackageName.Trim(), requestedVersion, settings, cancellationToken);
            var installOrder = DependencyDeploymentOrder(graph);

            AnsiConsole.MarkupLine("[bold]PowerPack install plan[/]");
            AnsiConsole.MarkupLine($"Root: [cyan]{settings.PackageName.Trim()}[/] >= [cyan]{requestedVersion}[/]");
            AnsiConsole.MarkupLine($"Environment: [cyan]{settings.Environment.Trim()}[/]");
            AnsiConsole.MarkupLine("Install order:");
            foreach (var packageName in installOrder)
            {
                var node = graph.Nodes[packageName];
                AnsiConsole.MarkupLine($"  - [green]{node.Name}[/] {node.Version} ({node.PackageTransportName})");
            }

            if (settings.DryRun)
                return 0;

            using var httpClient = new HttpClient();
            var credential = new AzureCliCredential();
            var apiClient = CreateApiClient(httpClient, credential, settings.ApplicationIdUri);
            var dataverseClient = new DataverseSolutionClient(httpClient, credential);
            var downloadDirectory = PrepareDownloadDirectory(settings.DownloadDirectory);
            foreach (var packageName in installOrder)
            {
                var node = graph.Nodes[packageName];
                var packagePath = Path.Combine(downloadDirectory.FullName, SafeFileName($"{node.PackageTransportName}_{node.PackageTransportVersion}.zip"));

                AnsiConsole.MarkupLine($"Downloading [green]{node.Name}[/] {node.Version}...");
                await using (var outputStream = File.Create(packagePath))
                    await apiClient.DownloadPackageAsync(node.DownloadUrl, outputStream, cancellationToken);

                AnsiConsole.MarkupLine($"Importing [green]{node.Name}[/] {node.Version}...");
                await ImportSolutionAsync(dataverseClient, settings, node, packagePath, cancellationToken);
            }

            AnsiConsole.MarkupLine("[green]PowerPack package install completed.[/]");
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

    private static async Task<DependencyDeploymentGraph> ResolveGraphAsync(
        string packageName,
        string version,
        Settings settings,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var resolution = await CreateApiClient(httpClient, new AzureCliCredential(), settings.ApplicationIdUri).ResolveSetAsync(
            settings.ApiBaseUrl,
            new ResolveSetRequest
            {
                Solutions =
                [
                    new SolutionReference
                    {
                        Name = packageName,
                        Version = version,
                    },
                ],
            },
            cancellationToken);

        return new DependencyDeploymentGraphBuilder().Build(resolution);
    }

    private static DirectoryInfo PrepareDownloadDirectory(string? downloadDirectory)
    {
        var path = string.IsNullOrWhiteSpace(downloadDirectory)
            ? Path.Combine(Path.GetTempPath(), "powerpack-install", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(downloadDirectory);
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    private static async Task ImportSolutionAsync(
        DataverseSolutionClient dataverseClient,
        Settings settings,
        DependencyDeploymentNode node,
        string packagePath,
        CancellationToken cancellationToken)
    {
        if (ResolveSettingsFile(settings.SettingsDirectory, node) is not null)
            throw new CliException(
                "PAC deployment settings files are not a PowerPack runtime contract. Use DataverseSolutionImportOptions.ComponentParameters through the library API for direct imports.");

        if (node.ConnectionReferences.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Package {node.Name} declares connection references. Import will rely on Dataverse defaults or fail if deployment settings are required.[/]");
        }

        var packageBytes = await File.ReadAllBytesAsync(packagePath, cancellationToken);
        await dataverseClient.ImportSolutionAndWaitAsync(
            settings.Environment.Trim(),
            packageBytes,
            new DataverseSolutionImportOptions
            {
                OverwriteUnmanagedCustomizations = !settings.NoForceOverwrite,
                PublishAllChangesAfterImport = !settings.NoPublishChanges,
                PublishWorkflows = settings.PublishWorkflows,
            },
            TimeSpan.FromMinutes(settings.MaxAsyncWaitTimeMinutes),
            null,
            cancellationToken);
    }

    private static PowerPackApiClient CreateApiClient(
        HttpClient httpClient,
        AzureCliCredential credential,
        string applicationIdUri)
    {
        return new PowerPackApiClient(
            httpClient,
            new PowerPackApiClientOptions
            {
                Credential = credential,
                ApplicationIdUri = applicationIdUri,
            });
    }

    private static IReadOnlyList<string> DependencyDeploymentOrder(DependencyDeploymentGraph graph)
    {
        try
        {
            return DependencyDeploymentPlanner.GetDependencyFirstOrder(graph);
        }
        catch (PowerPackValidationException exception)
        {
            throw new CliException(exception.Message);
        }
    }

    private static FileInfo? ResolveSettingsFile(string? settingsDirectory, DependencyDeploymentNode node)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            return null;

        var directory = new DirectoryInfo(settingsDirectory);
        if (!directory.Exists)
            throw new CliException($"Deployment settings directory was not found: {directory.FullName}");

        var candidates = new[]
        {
            node.Name,
            node.PackageTransportName,
            node.SolutionUniqueName,
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var file = new FileInfo(Path.Combine(directory.FullName, SafeFileName(candidate) + ".json"));
            if (file.Exists)
                return file;
        }

        return null;
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidCharacter, '_');
        return value;
    }
}
