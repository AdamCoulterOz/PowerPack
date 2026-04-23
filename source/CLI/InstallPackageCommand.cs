using System.ComponentModel;
using System.Diagnostics;
using PowerPack.Models;
using PowerPack.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PowerPack.Cli;

internal sealed class InstallPackageCommand : AsyncCommand<InstallPackageCommand.Settings>
{
    private readonly PowerPackCliClient _client = new();
    private readonly DependencyDeploymentGraphBuilder _graphBuilder = new();

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

        [CommandOption("--environment <URL_OR_ID>")]
        [Description("Target Dataverse environment URL or environment id.")]
        public required string Environment { get; init; }

        [CommandOption("--download-directory <PATH>")]
        [Description("Optional directory for downloaded package zips. Defaults to a temporary directory.")]
        public string? DownloadDirectory { get; init; }

        [CommandOption("--settings-directory <PATH>")]
        [Description("Optional directory containing pac deployment settings files named by package, transport, or solution unique name.")]
        public string? SettingsDirectory { get; init; }

        [CommandOption("--pac-path <PATH>")]
        [Description("Power Platform CLI executable path. Defaults to 'pac'.")]
        public string PacPath { get; init; } = "pac";

        [CommandOption("--max-async-wait-time <MINUTES>")]
        [Description("pac solution import asynchronous wait time in minutes.")]
        public int MaxAsyncWaitTimeMinutes { get; init; } = 60;

        [CommandOption("--activate-plugins")]
        [Description("Pass --activate-plugins to pac solution import.")]
        public bool ActivatePlugins { get; init; }

        [CommandOption("--skip-lower-version")]
        [Description("Pass --skip-lower-version to pac solution import.")]
        public bool SkipLowerVersion { get; init; }

        [CommandOption("--no-force-overwrite")]
        [Description("Do not pass --force-overwrite to pac solution import.")]
        public bool NoForceOverwrite { get; init; }

        [CommandOption("--no-publish-changes")]
        [Description("Do not pass --publish-changes to pac solution import.")]
        public bool NoPublishChanges { get; init; }

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

            if (MaxAsyncWaitTimeMinutes <= 0)
                return ValidationResult.Error("--max-async-wait-time must be greater than zero.");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var requestedVersion = SolutionVersion.Parse(settings.Version.Trim()).ToString();
            var graph = await ResolveGraphAsync(settings.PackageName.Trim(), requestedVersion, settings, CancellationToken.None);
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

            var downloadDirectory = PrepareDownloadDirectory(settings.DownloadDirectory);
            foreach (var packageName in installOrder)
            {
                var node = graph.Nodes[packageName];
                var packagePath = Path.Combine(downloadDirectory.FullName, SafeFileName($"{node.PackageTransportName}_{node.PackageTransportVersion}.zip"));

                AnsiConsole.MarkupLine($"Downloading [green]{node.Name}[/] {node.Version}...");
                await _client.DownloadPackageAsync(node.DownloadUrl, packagePath, CancellationToken.None);

                AnsiConsole.MarkupLine($"Importing [green]{node.Name}[/] {node.Version}...");
                await ImportSolutionAsync(settings, node, packagePath, CancellationToken.None);
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
    }

    private async Task<DependencyDeploymentGraph> ResolveGraphAsync(
        string packageName,
        string version,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var resolution = await _client.ResolveSetResultAsync(
            settings.ApiBaseUrl,
            settings.ApplicationIdUri,
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

        return _graphBuilder.Build(resolution);
    }

    private static IReadOnlyList<string> DependencyDeploymentOrder(DependencyDeploymentGraph graph)
    {
        var preferredIndex = graph.TopologicalOrder
            .Select((packageName, index) => (packageName, index))
            .ToDictionary(item => item.packageName, item => item.index, StringComparer.Ordinal);
        var remainingDependencyCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependentsByPackage = graph.Nodes.Keys.ToDictionary(
            packageName => packageName,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var (packageName, node) in graph.Nodes)
        {
            var dependencies = node.Dependencies
                .Select(dependency => dependency.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var dependencyName in dependencies)
            {
                if (!graph.Nodes.ContainsKey(dependencyName))
                    throw new CliException(
                        $"Resolved graph dependency '{dependencyName}' referenced by '{packageName}' was not present in nodes.");

                dependentsByPackage[dependencyName].Add(packageName);
            }

            remainingDependencyCount[packageName] = dependencies.Count;
        }

        var readyPackages = remainingDependencyCount
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key)
            .OrderBy(packageName => preferredIndex.GetValueOrDefault(packageName, int.MaxValue))
            .ToList();
        var installOrder = new List<string>();

        while (readyPackages.Count > 0)
        {
            var packageName = readyPackages[0];
            readyPackages.RemoveAt(0);
            installOrder.Add(packageName);

            foreach (var dependentName in dependentsByPackage[packageName]
                         .OrderBy(value => preferredIndex.GetValueOrDefault(value, int.MaxValue)))
            {
                remainingDependencyCount[dependentName] -= 1;
                if (remainingDependencyCount[dependentName] == 0)
                {
                    readyPackages.Add(dependentName);
                    readyPackages = readyPackages
                        .OrderBy(value => preferredIndex.GetValueOrDefault(value, int.MaxValue))
                        .ToList();
                }
            }
        }

        if (installOrder.Count != graph.Nodes.Count)
        {
            var unresolvedPackages = remainingDependencyCount
                .Where(entry => entry.Value > 0)
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal);
            throw new CliException(
                "Resolved graph dependencies contain a cycle or unresolved references: " +
                string.Join(", ", unresolvedPackages));
        }

        return installOrder;
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
        Settings settings,
        DependencyDeploymentNode node,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "solution",
            "import",
            "--environment",
            settings.Environment.Trim(),
            "--path",
            packagePath,
            "--async",
            "--max-async-wait-time",
            settings.MaxAsyncWaitTimeMinutes.ToString(),
        };

        if (!settings.NoForceOverwrite)
            arguments.Add("--force-overwrite");
        if (!settings.NoPublishChanges)
            arguments.Add("--publish-changes");
        if (settings.ActivatePlugins)
            arguments.Add("--activate-plugins");
        if (settings.SkipLowerVersion)
            arguments.Add("--skip-lower-version");

        var settingsFile = ResolveSettingsFile(settings.SettingsDirectory, node);
        if (settingsFile is not null)
        {
            arguments.Add("--settings-file");
            arguments.Add(settingsFile.FullName);
        }
        else if (node.ConnectionReferences.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Package {node.Name} declares connection references. Import will rely on Dataverse defaults or fail if deployment settings are required.[/]");
        }

        var result = await RunProcessAsync(settings.PacPath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new CliException(
                $"pac solution import failed with exit code {result.ExitCode}.\n{result.Output}");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new CliException($"Failed to start process '{fileName}'.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask + Environment.NewLine + await errorTask).Trim();
        if (!string.IsNullOrWhiteSpace(output))
            Console.Out.WriteLine(output);

        return new ProcessResult(process.ExitCode, output);
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
