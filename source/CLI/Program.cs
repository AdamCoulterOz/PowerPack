using PowerPack.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("powerpack");
    config.AddCommand<BuildManifestCommand>("build-manifest")
        .WithDescription("Build a solution manifest locally from a managed solution zip using the shared PowerPack manifest builder.");
    config.AddCommand<PublishCommand>("publish")
        .WithDescription("Upload a managed solution zip to the PowerPack API for validation, indexing, and storage.");
    config.AddCommand<ResolveSetCommand>("resolve-set")
        .WithDescription("Resolve a set of solution dependencies through the PowerPack API.");
    config.AddCommand<ResolveDeploymentGraphCommand>("resolve-deployment-graph")
        .WithDescription("Parse missingdependencies.yml, resolve dependencies through the PowerPack API, and emit a generic deployment graph.");
    config.AddCommand<InstallPackageCommand>("install-package")
        .WithDescription("Resolve, download, and import a PowerPack package and its dependencies into a Power Platform environment.");
});

return await app.RunAsync(args);
