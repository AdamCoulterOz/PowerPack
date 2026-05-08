using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PowerPack.TestFixtures;

public static class SolutionPackageFixtureWriter
{
    public static byte[] CreateZipBytes(SolutionPackageFixture fixture, bool useLegacyFlatLayout = false)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "solution.xml", BuildSolutionXml(fixture));
            WriteTextEntry(
                archive,
                useLegacyFlatLayout ? "customizations.xml" : "Other/Customizations.xml",
                BuildCustomizationsXml(fixture)
            );
        }

        return buffer.ToArray();
    }

    public static IReadOnlyList<string> WriteAll(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var writtenPaths = new List<string>();
        foreach (var fixture in FixtureCatalog.All)
        {
            var filePath = Path.Combine(outputDirectory, $"{fixture.Name}_{fixture.Version}.zip");
            File.WriteAllBytes(filePath, CreateZipBytes(fixture));
            writtenPaths.Add(filePath);
        }

        return writtenPaths;
    }

    private static string BuildSolutionXml(SolutionPackageFixture fixture)
    {
        var document = new XDocument(
            new XElement("ImportExportXml",
                new XElement("SolutionManifest",
                    new XElement("UniqueName", fixture.Name),
                    new XElement("Version", fixture.Version),
                    new XElement("Publisher",
                        new XElement("UniqueName", fixture.Publisher)
                    )
                ),
                new XElement("MissingDependencies",
                    fixture.Dependencies.Select(dependency =>
                        new XElement("MissingDependency",
                            new XElement("Required",
                                new XAttribute("solution", $"{dependency.Name} ({dependency.MinimumVersion})")
                            )
                        )
                    )
                )
            )
        );

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildCustomizationsXml(SolutionPackageFixture fixture)
    {
        var document = new XDocument(
            new XElement("ImportExportXml",
                new XElement("ConnectionReferences",
                    fixture.Connections.Select(connection =>
                        new XElement("connectionreference",
                            new XAttribute("connectionreferencelogicalname", connection.LogicalName),
                            new XElement("connectorid", connection.ConnectorId),
                            new XElement("connectionreferencedisplayname", connection.DisplayName),
                            new XElement("description", connection.Description)
                        )
                    )
                ),
                new XElement("Workflows",
                    (fixture.Flows ?? []).Select(flow =>
                        new XElement("Workflow",
                            new XAttribute("WorkflowId", flow.WorkflowId),
                            new XAttribute("Name", flow.Name),
                            new XElement("JsonFileName", $"/modernflows/{flow.WorkflowId.Trim('{', '}')}/{flow.Name.Replace(" ", string.Empty, StringComparison.Ordinal)}.json"),
                            new XElement("Type", 1),
                            new XElement("Subprocess", 0),
                            new XElement("Category", flow.Category),
                            new XElement("Mode", 0),
                            new XElement("Scope", 4),
                            new XElement("OnDemand", 0),
                            new XElement("TriggerOnCreate", 0),
                            new XElement("TriggerOnDelete", 0),
                            new XElement("AsyncAutodelete", 0),
                            new XElement("SyncWorkflowLogOnFailure", 0),
                            new XElement("StateCode", flow.StateCode),
                            new XElement("StatusCode", flow.StatusCode),
                            new XElement("RunAs", 1),
                            new XElement("IsTransacted", 1),
                            new XElement("IntroducedVersion", fixture.Version),
                            new XElement("IsCustomizable", 1),
                            new XElement("IsCustomProcessingStepAllowedForOtherPublishers", 1),
                            new XElement("ModernFlowType", 0),
                            new XElement("PrimaryEntity", "none"),
                            new XElement("LocalizedNames",
                                new XElement("LocalizedName",
                                    new XAttribute("languagecode", 1033),
                                    new XAttribute("description", flow.Name)
                                )
                            )
                        )
                    )
                )
            )
        );

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
