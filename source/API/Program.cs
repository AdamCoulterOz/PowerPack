using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Azure.Core;
using Azure.Identity;
using PowerPack.Options;
using PowerPack.Services;
using PowerPack.Storage;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services
    .AddOptions<PowerPackOptions>()
    .Bind(builder.Configuration.GetSection(PowerPackOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IManifestIndexStore>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PowerPackOptions>>().Value;
    return new TableManifestIndexStore(options);
});
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<IPackageBlobStore, PackageBlobStore>();
builder.Services.AddSingleton<PackageDownloadTokenService>();
builder.Services.AddSingleton<SolutionPackageManifestBuilder>();
builder.Services.AddHttpClient<PowerPlatformConnectorMetadataClient>();
builder.Services.AddSingleton<DependencyResolver>();

builder.Build().Run();
