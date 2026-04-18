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
});

return await app.RunAsync(args);
