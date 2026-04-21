using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PowerPack.Models;

namespace PowerPack.Services;

public sealed class SolutionPackageManifestBuilder(PowerPlatformConnectorMetadataClient connectorMetadataClient)
{
    private static readonly Regex DescriptionSectionHeaderPattern = new(@"^\[([A-Za-z][A-Za-z0-9_-]*)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex DependencySolutionPattern = new(@"(.+?)\s+\((\d+(?:\.\d+)*)\)$", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> WebResourceTypeToExtension = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["1"] = "htm",
        ["2"] = "css",
        ["3"] = "js",
        ["4"] = "xml",
        ["5"] = "png",
        ["6"] = "jpg",
        ["7"] = "gif",
        ["8"] = "xap",
        ["9"] = "xsl",
        ["10"] = "ico",
        ["11"] = "svg",
        ["12"] = "resx",
    };

    private readonly PowerPlatformConnectorMetadataClient _connectorMetadataClient = connectorMetadataClient;

    public async Task<SolutionManifest> BuildAsync(
        byte[] packageBytes,
        string? powerPlatformEnvironmentId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        if (packageBytes.Length == 0)
            throw new PowerPackValidationException("Managed solution package zip is empty.");

        using var archive = new ZipArchive(new MemoryStream(packageBytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);

        var solutionXmlEntry = GetRequiredEntry(
            archive,
            ["solution.xml"],
            "Managed solution package zip is missing solution.xml."
        );
        var solutionXml = await ReadEntryAsStringAsync(solutionXmlEntry, cancellationToken);
        var solutionXmlPath = solutionXmlEntry.FullName;

        var customizationsXmlEntry = GetRequiredEntry(
            archive,
            ["Other/Customizations.xml", "customizations.xml"],
            "Managed solution package zip is missing Other/Customizations.xml or customizations.xml."
        );
        var customizationsXml = await ReadEntryAsStringAsync(customizationsXmlEntry, cancellationToken);
        var customizationsXmlPath = customizationsXmlEntry.FullName;

        var solutionDocument = ParseXml(solutionXml, solutionXmlPath);
        var customizationsDocument = ParseXml(customizationsXml, customizationsXmlPath);

        var manifestRoot = solutionDocument.Descendants().FirstOrDefault(node => LocalName(node) == "SolutionManifest")
            ?? throw new PowerPackValidationException($"{solutionXmlPath} is missing SolutionManifest.");

        var uniqueName = ChildValue(manifestRoot, "UniqueName", $"{solutionXmlPath} SolutionManifest.UniqueName");
        var version = SolutionVersion.Parse(ChildValue(manifestRoot, "Version", $"{solutionXmlPath} SolutionManifest.Version"));
        var publisherNode = manifestRoot.Elements().FirstOrDefault(node => LocalName(node) == "Publisher")
            ?? throw new PowerPackValidationException($"{solutionXmlPath} is missing SolutionManifest.Publisher.");
        var publisher = ChildValue(publisherNode, "UniqueName", $"{solutionXmlPath} SolutionManifest.Publisher.UniqueName");

        var dependencies = ExtractDependencies(solutionDocument, uniqueName);
        var connections = await ExtractConnectionsAsync(
            customizationsDocument,
            customizationsXmlPath,
            powerPlatformEnvironmentId,
            cancellationToken
        );
        var variables = ExtractVariables(customizationsDocument, customizationsXmlPath);
        var environmentRequirements = ExtractEnvironmentRequirements(archive, customizationsDocument);

        return ManifestNormalizer.Normalize(new SolutionManifest
        {
            Name = uniqueName,
            Version = version.ToString(),
            Publisher = publisher,
            Dependencies = dependencies,
            Connections = connections,
            Variables = variables,
            EnvironmentRequirements = environmentRequirements,
            Metadata = new JsonObject
            {
                ["package_name"] = uniqueName.ToLowerInvariant(),
                ["package_version"] = version.ToString(),
                ["solution_package_version"] = version.ToString(),
            },
        });
    }

    private static SolutionEnvironmentRequirements ExtractEnvironmentRequirements(
        ZipArchive archive,
        XDocument customizationsDocument)
    {
        var allowedAttachmentExtensions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            var normalizedExtension = AttachmentExtensionPolicy.NormalizeExtension(Path.GetExtension(entry.Name));
            if (normalizedExtension is not null &&
                AttachmentExtensionPolicy.DefaultBlockedAttachmentExtensionSet.Contains(normalizedExtension))
            {
                allowedAttachmentExtensions.Add(normalizedExtension);
            }
        }

        foreach (var webResourceNode in customizationsDocument.Descendants().Where(node => LocalName(node) == "WebResource"))
        {
            var extensionsToCheck = new List<string?>();
            var webResourceName = webResourceNode.Elements().FirstOrDefault(node => LocalName(node) == "Name")?.Value;
            var fileName = webResourceNode.Elements().FirstOrDefault(node => LocalName(node) == "FileName")?.Value;
            var webResourceType = webResourceNode.Elements().FirstOrDefault(node => LocalName(node) == "WebResourceType")?.Value;

            extensionsToCheck.Add(AttachmentExtensionPolicy.NormalizeExtension(Path.GetExtension(webResourceName ?? string.Empty)));
            extensionsToCheck.Add(AttachmentExtensionPolicy.NormalizeExtension(Path.GetExtension(fileName ?? string.Empty)));
            if (webResourceType is not null && WebResourceTypeToExtension.TryGetValue(webResourceType.Trim(), out var mappedExtension))
                extensionsToCheck.Add(AttachmentExtensionPolicy.NormalizeExtension(mappedExtension));

            foreach (var normalizedExtension in extensionsToCheck.Where(value => value is not null).Cast<string>())
            {
                if (AttachmentExtensionPolicy.DefaultBlockedAttachmentExtensionSet.Contains(normalizedExtension))
                    allowedAttachmentExtensions.Add(normalizedExtension);
            }
        }

        return new SolutionEnvironmentRequirements
        {
            Dataverse = new DataverseSolutionEnvironmentRequirements
            {
                AllowedAttachmentExtensions = AttachmentExtensionPolicy.NormalizeExtensions(allowedAttachmentExtensions),
            },
        };
    }

    private async Task<JsonObject> ExtractConnectionsAsync(
        XDocument customizationsDocument,
        string customizationsXmlPath,
        string? powerPlatformEnvironmentId,
        CancellationToken cancellationToken
    )
    {
        var connectionDefinitions = customizationsDocument
            .Descendants()
            .Where(node => LocalName(node) == "connectionreference")
            .Select(node =>
            {
                var fields = node.Elements().ToDictionary(
                    child => LocalName(child),
                    child => (child.Value ?? string.Empty).Trim(),
                    StringComparer.OrdinalIgnoreCase
                );
                var logicalName = (node.Attribute("connectionreferencelogicalname")?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(logicalName))
                    logicalName = fields.GetValueOrDefault("connectionreferencelogicalname", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(logicalName))
                    throw new PowerPackValidationException(
                        "Managed customizations XML contains a connection reference without connectionreferencelogicalname."
                    );

                var connectorId = fields.GetValueOrDefault("connectorid", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(connectorId))
                    throw new PowerPackValidationException(
                        $"Connection reference '{logicalName}' is missing connectorid."
                    );

                return new ConnectionReferenceDefinition
                {
                    LogicalName = logicalName,
                    ConnectorId = connectorId,
                    DisplayName = fields.GetValueOrDefault("connectionreferencedisplayname", string.Empty),
                    Description = fields.GetValueOrDefault("description", string.Empty),
                };
            })
            .OrderBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var connections = new JsonObject();
        var metadataCache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in connectionDefinitions)
        {
            var (humanDescription, sections) = SplitDescriptionSections(
                definition.Description,
                $"{customizationsXmlPath}.[connectionreference:{definition.LogicalName}].description"
            );
            var requirements = ParseRequirementsBlock(
                sections["requirements"],
                $"{customizationsXmlPath}.[connectionreference:{definition.LogicalName}].description.[requirements]"
            );

            var connectionObject = new JsonObject
            {
                ["type"] = definition.ConnectorId,
                ["description"] = humanDescription,
                ["auth"] = requirements.Auth,
                ["roles"] = JsonSerializer.SerializeToNode(requirements.Roles),
                ["permissions"] = JsonSerializer.SerializeToNode(
                    requirements.Permissions
                        .Select(permission => ExpandPermission(
                            permission,
                            $"{customizationsXmlPath}.[connectionreference:{definition.LogicalName}].description.[requirements].permissions"
                        ))
                        .ToArray()
                ),
            };

            if (sections.TryGetValue("connection", out var connectionSectionText))
            {
                var connectionDefinition = ParseConnectionBlock(
                    connectionSectionText,
                    $"{customizationsXmlPath}.[connectionreference:{definition.LogicalName}].description.[connection]"
                );

                if (string.IsNullOrWhiteSpace(powerPlatformEnvironmentId))
                    throw new PowerPackValidationException(
                        $"Connection reference '{definition.LogicalName}' requires Power Platform environment metadata enrichment, " +
                        "but no environment id was provided."
                    );

                var connectorName = definition.ConnectorId.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                if (!metadataCache.TryGetValue(connectorName, out var connectorMetadata))
                {
                    connectorMetadata = await _connectorMetadataClient.GetConnectorMetadataAsync(
                        powerPlatformEnvironmentId,
                        connectorName,
                        cancellationToken
                    );
                    metadataCache[connectorName] = connectorMetadata;
                }

                connectionObject["connection"] = JsonSerializer.SerializeToNode(new
                {
                    method = connectionDefinition.Method,
                    parameters = connectionDefinition.Parameters,
                });
                connectionObject["connection_parameters_set"] = BuildConnectionParametersSet(
                    connectionDefinition,
                    connectorMetadata,
                    $"{customizationsXmlPath}.[connectionreference:{definition.LogicalName}].description.[connection]"
                );
            }

            connections[definition.LogicalName] = connectionObject;
        }

        return connections;
    }

    private static JsonObject ExtractVariables(XDocument customizationsDocument, string customizationsXmlPath)
    {
        var variables = new JsonObject();
        var variableDefinitions = customizationsDocument
            .Descendants()
            .Where(node =>
            {
                var localName = LocalName(node);
                return localName is "environmentvariabledefinition" or "environmentvariable";
            })
            .Select(node =>
            {
                var fields = node.Elements().ToDictionary(
                    child => LocalName(child),
                    child => (child.Value ?? string.Empty).Trim(),
                    StringComparer.OrdinalIgnoreCase
                );
                var schemaName = (node.Attribute("schemaname")?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(schemaName))
                    schemaName = fields.GetValueOrDefault("schemaname", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(schemaName))
                    return null;

                return new EnvironmentVariableDefinition
                {
                    SchemaName = schemaName,
                    TypeCode = ValueOrDefault(node, fields, "type"),
                    ValueSchema = NullIfEmpty(ValueOrDefault(node, fields, "valueschema")),
                    DefaultValue = NullIfEmpty(ValueOrDefault(node, fields, "defaultvalue")),
                };
            })
            .Where(item => item is not null)
            .Cast<EnvironmentVariableDefinition>()
            .GroupBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in variableDefinitions)
        {
            var variableType = InferVariableType(
                definition.TypeCode,
                definition.ValueSchema,
                $"{customizationsXmlPath}.[environmentvariable:{definition.SchemaName}]"
            );

            var variableObject = new JsonObject
            {
                ["type"] = variableType,
            };
            if (!string.IsNullOrWhiteSpace(definition.DefaultValue))
                variableObject["default"] = JsonSerializer.SerializeToNode(
                    CoerceDefaultValue(
                        definition.DefaultValue,
                        variableType,
                        $"{customizationsXmlPath}.[environmentvariable:{definition.SchemaName}]"
                    )
                );

            variables[definition.SchemaName] = variableObject;
        }

        return variables;
    }

    private static Dictionary<string, string> ExtractDependencies(XDocument solutionDocument, string currentUniqueName)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentCaseFold = currentUniqueName.Trim().ToUpperInvariant();

        foreach (var missingDependency in solutionDocument.Descendants().Where(node => LocalName(node) == "MissingDependency"))
        {
            var requiredNode = missingDependency.Elements().FirstOrDefault(node => LocalName(node) == "Required")
                ?? throw new PowerPackValidationException(
                    "solution.xml contains a MissingDependency without a Required node."
                );

            var solutionValue = (requiredNode.Attribute("solution")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(solutionValue) || solutionValue == "Active")
                throw new PowerPackValidationException(
                    "solution.xml contains a MissingDependency.Required without a usable solution attribute."
                );

            var match = DependencySolutionPattern.Match(solutionValue);
            if (!match.Success)
                throw new PowerPackValidationException(
                    $"solution.xml contains a MissingDependency.Required with unsupported solution format: '{solutionValue}'."
                );

            var dependencyName = match.Groups[1].Value.Trim();
            var dependencyVersion = SolutionVersion.Parse(match.Groups[2].Value).ToString();
            if (dependencyName.ToUpperInvariant() == currentCaseFold)
                continue;

            if (!dependencies.TryGetValue(dependencyName, out var existingVersion) ||
                SolutionVersion.Parse(dependencyVersion).CompareTo(SolutionVersion.Parse(existingVersion)) > 0)
                dependencies[dependencyName] = dependencyVersion;
        }

        return dependencies
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static ZipArchiveEntry GetRequiredEntry(
        ZipArchive archive,
        IReadOnlyList<string> candidatePaths,
        string missingMessage)
    {
        foreach (var candidatePath in candidatePaths)
        {
            var exactMatch = archive.GetEntry(candidatePath);
            if (exactMatch is not null)
                return exactMatch;

            var caseInsensitiveMatch = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, candidatePath, StringComparison.OrdinalIgnoreCase));
            if (caseInsensitiveMatch is not null)
                return caseInsensitiveMatch;
        }

        throw new PowerPackValidationException(missingMessage);
    }

    private static (string HumanDescription, Dictionary<string, string> Sections) SplitDescriptionSections(
        string description,
        string path
    )
    {
        var humanLines = new List<string>();
        var sectionLines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? currentSection = null;

        foreach (var line in description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = DescriptionSectionHeaderPattern.Match(line.Trim());
            if (match.Success)
            {
                var sectionName = match.Groups[1].Value;
                if (sectionLines.ContainsKey(sectionName))
                    throw new PowerPackValidationException($"{path} declares duplicate section '[{sectionName}]'.");
                if (sectionName is not ("requirements" or "connection"))
                    throw new PowerPackValidationException($"{path} declares unsupported section '[{sectionName}]'.");

                sectionLines[sectionName] = [];
                currentSection = sectionName;
                continue;
            }

            if (currentSection is null)
            {
                humanLines.Add(line);
                continue;
            }

            sectionLines[currentSection].Add(line);
        }

        var sections = sectionLines.ToDictionary(
            item => item.Key,
            item => string.Join("\n", item.Value).Trim(),
            StringComparer.Ordinal
        );
        if (!sections.TryGetValue("requirements", out var requirementsText) || string.IsNullOrWhiteSpace(requirementsText))
            throw new PowerPackValidationException($"{path} is missing the required [requirements] block.");

        return (string.Join("\n", humanLines).Trim(), sections);
    }

    private static RequirementsDefinition ParseRequirementsBlock(string rawText, string path)
    {
        string? auth = null;
        List<string>? currentList = null;
        var roles = new List<string>();
        var permissions = new List<string>();

        foreach (var (rawLine, lineNumber) in EnumerateLines(rawText))
        {
            var line = rawLine.TrimEnd();
            var stripped = line.Trim();
            if (string.IsNullOrWhiteSpace(stripped) || stripped.StartsWith('#'))
                continue;

            if (stripped.StartsWith("- ", StringComparison.Ordinal))
            {
                if (currentList is null)
                    throw new PowerPackValidationException($"{path} line {lineNumber} contains a list item without a list key.");
                var item = stripped[2..].Trim();
                if (string.IsNullOrWhiteSpace(item))
                    throw new PowerPackValidationException($"{path} line {lineNumber} contains an empty list item.");
                currentList.Add(item);
                continue;
            }

            if (rawLine.StartsWith(' ') || rawLine.StartsWith('\t'))
                throw new PowerPackValidationException($"{path} line {lineNumber} uses unsupported indentation.");

            var (key, value) = SplitKeyValue(line, path, lineNumber);
            switch (key)
            {
                case "auth":
                    auth = value switch
                    {
                        "service" or "user" or "none" => value,
                        _ => throw new PowerPackValidationException(
                            $"{path} line {lineNumber} auth must be 'service', 'user', or 'none'."
                        ),
                    };
                    currentList = null;
                    break;
                case "roles":
                    EnsureEmptyInlineValue(value, path, lineNumber, key);
                    currentList = roles;
                    break;
                case "permissions":
                    EnsureEmptyInlineValue(value, path, lineNumber, key);
                    currentList = permissions;
                    break;
                default:
                    throw new PowerPackValidationException($"{path} line {lineNumber} uses unsupported key '{key}'.");
            }
        }

        if (auth is null)
            throw new PowerPackValidationException($"{path} is missing required key 'auth'.");

        return new RequirementsDefinition(auth, roles, permissions);
    }

    private static ConnectionBlockDefinition ParseConnectionBlock(string rawText, string path)
    {
        string? method = null;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var inParameters = false;

        foreach (var (rawLine, lineNumber) in EnumerateLines(rawText))
        {
            var line = rawLine.TrimEnd();
            var stripped = line.Trim();
            if (string.IsNullOrWhiteSpace(stripped) || stripped.StartsWith('#'))
                continue;

            if (inParameters)
            {
                if (!rawLine.StartsWith("  ", StringComparison.Ordinal))
                {
                    inParameters = false;
                }
                else
                {
                    var (parameterName, parameterValue) = SplitKeyValue(stripped, path, lineNumber);
                    parameters[CanonicalizeScalar(parameterName)] = CanonicalizeScalar(parameterValue);
                    continue;
                }
            }

            if (rawLine.StartsWith(' ') || rawLine.StartsWith('\t'))
                throw new PowerPackValidationException($"{path} line {lineNumber} uses unsupported indentation.");

            var (key, value) = SplitKeyValue(line, path, lineNumber);
            switch (key)
            {
                case "method":
                    if (string.IsNullOrWhiteSpace(value))
                        throw new PowerPackValidationException($"{path} line {lineNumber} must provide a method value.");
                    method = CanonicalizeScalar(value);
                    inParameters = false;
                    break;
                case "parameters":
                    EnsureEmptyInlineValue(value, path, lineNumber, key);
                    inParameters = true;
                    break;
                default:
                    throw new PowerPackValidationException($"{path} line {lineNumber} uses unsupported key '{key}'.");
            }
        }

        if (method is null)
            throw new PowerPackValidationException($"{path} is missing required key 'method'.");

        return new ConnectionBlockDefinition(
            method,
            parameters.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        );
    }

    private static JsonObject BuildConnectionParametersSet(
        ConnectionBlockDefinition connectionDefinition,
        JsonObject connectorMetadata,
        string path
    )
    {
        var valuesNode = connectorMetadata["properties"]?["connectionParameterSets"]?["values"] as JsonArray
            ?? throw new PowerPackValidationException($"{path} connector metadata does not expose connectionParameterSets.values.");

        var selectedParameterSet = valuesNode
            .Select(node => node as JsonObject)
            .FirstOrDefault(node =>
                node is not null &&
                string.Equals(
                    node["name"]?.GetValue<string>(),
                    connectionDefinition.Method,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            ?? throw new PowerPackValidationException(
                $"{path} method '{connectionDefinition.Method}' was not found in connector metadata."
            );

        var parameterDefinitions = selectedParameterSet["parameters"] as JsonObject
            ?? throw new PowerPackValidationException(
                $"{path} method '{connectionDefinition.Method}' is missing parameter metadata."
            );

        var parameterNames = parameterDefinitions
            .Select(item => item.Key)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var renderedValues = parameterNames.ToDictionary(
            name => name,
            name => ConnectorParameterBaseValue(name, parameterDefinitions[name] as JsonObject, path),
            StringComparer.Ordinal
        );

        foreach (var overlay in connectionDefinition.Parameters)
        {
            var (parameterRoot, leafPath) = FindMatchingParameterRoot(overlay.Key, parameterNames, path);
            if (leafPath.Count == 0)
            {
                renderedValues[parameterRoot] = overlay.Value;
                continue;
            }

            if (renderedValues[parameterRoot] is not JsonObject existingValue)
                throw new PowerPackValidationException(
                    $"{path}.parameters.{overlay.Key} targets nested metadata on '{parameterRoot}', but that parameter is not an object."
                );

            AssignNestedValue(existingValue, leafPath, overlay.Value, $"{path}.parameters.{overlay.Key}");
        }

        foreach (var parameterName in parameterNames)
            ValidateRenderedParameterValue(
                parameterName,
                parameterDefinitions[parameterName] as JsonObject,
                renderedValues[parameterName],
                path
            );

        var values = new JsonObject();
        foreach (var parameterName in parameterNames.Where(name => renderedValues[name] is not null))
            values[parameterName] = new JsonObject { ["value"] = renderedValues[parameterName]?.DeepClone() };

        return new JsonObject
        {
            ["name"] = selectedParameterSet["name"]?.GetValue<string>(),
            ["values"] = values,
        };
    }

    private static JsonNode? ConnectorParameterBaseValue(string parameterName, JsonObject? parameterDefinition, string path)
    {
        var parameterType = parameterDefinition?["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(parameterType))
            throw new PowerPackValidationException($"{path}.parameters.{parameterName} is missing parameter type metadata.");

        return parameterType switch
        {
            "oauthSetting" => JsonValue.Create(
                parameterDefinition!["oAuthSettings"]?["redirectUrl"]?.GetValue<string>()
                ?? throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName} oauthSetting is missing redirectUrl."
                )
            ),
            "clientCertificate" => new JsonObject
            {
                ["type"] = "ClientCertificate",
                ["pfx"] = null,
                ["password"] = string.Empty,
            },
            _ => null,
        };
    }

    private static void ValidateRenderedParameterValue(
        string parameterName,
        JsonObject? parameterDefinition,
        JsonNode? renderedValue,
        string path
    )
    {
        var parameterType = parameterDefinition?["type"]?.GetValue<string>()
            ?? throw new PowerPackValidationException(
                $"{path}.parameters.{parameterName} is missing parameter type metadata."
            );
        var required = FindRequiredFlag(parameterDefinition);

        if (parameterType == "clientCertificate")
        {
            if (renderedValue is not JsonObject renderedObject)
                throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName} must resolve to an object for type 'clientCertificate'."
                );

            if (!string.Equals(renderedObject["type"]?.GetValue<string>(), "ClientCertificate", StringComparison.Ordinal))
                throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName}.type must resolve to 'ClientCertificate'."
                );

            var pfx = renderedObject["pfx"]?.GetValue<string>();
            if (required && string.IsNullOrWhiteSpace(pfx))
                throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName}.pfx must be provided for required clientCertificate parameters."
                );

            if (renderedObject["password"] is not JsonValue passwordValue || !passwordValue.TryGetValue<string>(out _))
                throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName}.password must resolve to a string."
                );

            var unexpected = renderedObject.Select(item => item.Key)
                .Where(key => key is not ("type" or "pfx" or "password"))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (unexpected.Count > 0)
                throw new PowerPackValidationException(
                    $"{path}.parameters.{parameterName} contains unsupported clientCertificate fields: {string.Join(", ", unexpected)}."
                );

            return;
        }

        if (renderedValue is null)
        {
            if (required)
                throw new PowerPackValidationException($"{path}.parameters.{parameterName} is required but was not provided.");
            return;
        }

        if (renderedValue is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var stringValue))
            throw new PowerPackValidationException($"{path}.parameters.{parameterName} must resolve to a string.");

        if (required && string.IsNullOrWhiteSpace(stringValue))
            throw new PowerPackValidationException($"{path}.parameters.{parameterName} is required and must not be empty.");
    }

    private static bool FindRequiredFlag(JsonObject? parameterDefinition)
    {
        var requiredNode = parameterDefinition?["uiDefinition"]?["constraints"]?["required"];
        return requiredNode switch
        {
            JsonValue value when value.TryGetValue<bool>(out var required) => required,
            JsonValue value when value.TryGetValue<string>(out var requiredText) =>
                string.Equals(requiredText, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static (string Root, List<string> LeafPath) FindMatchingParameterRoot(
        string overlayKey,
        IReadOnlyList<string> parameterNames,
        string path
    )
    {
        var matches = parameterNames
            .Where(parameterName =>
                string.Equals(overlayKey, parameterName, StringComparison.Ordinal) ||
                overlayKey.StartsWith(parameterName + ".", StringComparison.Ordinal))
            .OrderByDescending(parameterName => parameterName.Length)
            .ToList();
        if (matches.Count == 0)
            throw new PowerPackValidationException($"{path}.parameters declares unsupported parameter '{overlayKey}'.");

        var parameterRoot = matches[0];
        if (overlayKey.Length == parameterRoot.Length)
            return (parameterRoot, []);

        return (parameterRoot, overlayKey[(parameterRoot.Length + 1)..].Split('.', StringSplitOptions.None).ToList());
    }

    private static void AssignNestedValue(JsonObject target, IReadOnlyList<string> leafPath, string value, string path)
    {
        if (leafPath.Count == 0)
            throw new PowerPackValidationException($"{path} cannot overwrite metadata-derived object roots directly.");

        JsonObject current = target;
        for (var index = 0; index < leafPath.Count; index++)
        {
            var segment = leafPath[index];
            if (index == leafPath.Count - 1)
            {
                current[segment] = value;
                return;
            }

            current = current[segment] as JsonObject
                ?? throw new PowerPackValidationException(
                    $"{path} targets unknown nested path '{string.Join(".", leafPath.Take(index + 1))}'."
                );
        }
    }

    private static Dictionary<string, string> ExpandPermission(string rawPermission, string path)
    {
        var parts = rawPermission.Split('/', 3, StringSplitOptions.None);
        if (parts.Length != 3)
            throw new PowerPackValidationException(
                $"{path} permission '{rawPermission}' must use the format '<resource>/<type>/<name>'."
            );

        var resource = parts[0].Trim();
        var permissionType = parts[1].Trim();
        var name = parts[2].Trim();
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(name))
            throw new PowerPackValidationException($"{path} permission '{rawPermission}' contains an empty segment.");
        if (permissionType is not ("application" or "delegated"))
            throw new PowerPackValidationException(
                $"{path} permission '{rawPermission}' must use type 'application' or 'delegated'."
            );

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["resource"] = resource,
            ["type"] = permissionType,
            ["name"] = name,
        };
    }

    private static string InferVariableType(string rawTypeCode, string? valueSchema, string path) =>
        rawTypeCode switch
        {
            "100000000" => "text",
            "100000001" => "decimal",
            "100000002" => "boolean",
            "100000003" => "json",
            "100000005" => "secret-azurekeyvault",
            "100000004" => InferDatasourceVariableType(valueSchema, path),
            _ => throw new PowerPackValidationException($"{path}.type '{rawTypeCode}' is not a supported environment variable type."),
        };

    private static string InferDatasourceVariableType(string? valueSchema, string path)
    {
        if (string.IsNullOrWhiteSpace(valueSchema))
            throw new PowerPackValidationException($"{path}.valueschema is required for datasource environment variables.");

        var normalized = valueSchema.ToLowerInvariant();
        if (normalized.Contains("sharepoint", StringComparison.Ordinal))
            return "datasource-sharepoint";
        if (normalized.Contains("sqlserver", StringComparison.Ordinal) || normalized.Contains("sql", StringComparison.Ordinal))
            return "datasource-sqlserver";
        if (normalized.Contains("commondataserviceforapps", StringComparison.Ordinal) || normalized.Contains("dataverse", StringComparison.Ordinal))
            return "datasource-dataverse";
        if (normalized.Contains("sap", StringComparison.Ordinal))
            return "datasource-sap";

        throw new PowerPackValidationException(
            $"{path}.valueschema does not identify a supported datasource type."
        );
    }

    private static object CoerceDefaultValue(string rawValue, string variableType, string path) =>
        variableType switch
        {
            "text" or "secret-azurekeyvault" => rawValue,
            var value when value.StartsWith("datasource-", StringComparison.Ordinal) => rawValue,
            "decimal" => decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue)
                ? decimalValue
                : throw new PowerPackValidationException($"{path}.defaultvalue '{rawValue}' is not a valid decimal."),
            "boolean" => rawValue.Trim().ToLowerInvariant() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw new PowerPackValidationException($"{path}.defaultvalue '{rawValue}' is not a valid boolean."),
            },
            "json" => JsonNode.Parse(rawValue)
                ?? throw new PowerPackValidationException($"{path}.defaultvalue is not valid JSON."),
            _ => throw new PowerPackValidationException($"{path} uses unsupported variable type '{variableType}'."),
        };

    private static string ChildValue(XElement parent, string localName, string path)
    {
        var child = parent.Elements().FirstOrDefault(node => LocalName(node) == localName)
            ?? throw new PowerPackValidationException($"{path} is missing.");
        var value = child.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new PowerPackValidationException($"{path} is missing.");
        return value;
    }

    private static string ValueOrDefault(XElement element, IReadOnlyDictionary<string, string> fields, string key) =>
        ((element.Attribute(key)?.Value ?? string.Empty).Trim() is { Length: > 0 } attributeValue)
            ? attributeValue
            : fields.GetValueOrDefault(key, string.Empty).Trim();

    private static XDocument ParseXml(string xml, string path)
    {
        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is not PowerPackValidationException)
        {
            throw new PowerPackValidationException($"{path} is invalid XML: {exception.Message}");
        }
    }

    private static async Task<string> ReadEntryAsStringAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string LocalName(XElement element) => element.Name.LocalName;

    private static IEnumerable<(string Line, int LineNumber)> EnumerateLines(string rawText)
    {
        var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
            yield return (lines[index], index + 1);
    }

    private static (string Key, string Value) SplitKeyValue(string rawLine, string path, int lineNumber)
    {
        var separatorIndex = FindKeyValueSeparatorIndex(rawLine);
        if (separatorIndex < 0)
            throw new PowerPackValidationException($"{path} line {lineNumber} is not valid YAML key syntax.");

        var key = rawLine[..separatorIndex].Trim();
        var value = rawLine[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new PowerPackValidationException($"{path} line {lineNumber} contains an empty key.");

        return (key, value);
    }

    private static int FindKeyValueSeparatorIndex(string rawLine)
    {
        for (var index = 0; index < rawLine.Length; index++)
        {
            if (rawLine[index] != ':')
                continue;

            if (index == rawLine.Length - 1)
                return index;

            var nextCharacter = rawLine[index + 1];
            if (nextCharacter is ' ' or '\t')
                return index;
        }

        return -1;
    }

    private static void EnsureEmptyInlineValue(string value, string path, int lineNumber, string key)
    {
        if (!string.IsNullOrEmpty(value))
            throw new PowerPackValidationException($"{path} line {lineNumber} must not inline the '{key}' block.");
    }

    private static string CanonicalizeScalar(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
        return value;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class ConnectionReferenceDefinition
    {
        public required string LogicalName { get; init; }

        public required string ConnectorId { get; init; }

        public required string DisplayName { get; init; }

        public required string Description { get; init; }
    }

    private sealed class EnvironmentVariableDefinition
    {
        public required string SchemaName { get; init; }

        public required string TypeCode { get; init; }

        public string? ValueSchema { get; init; }

        public string? DefaultValue { get; init; }
    }

    private sealed record RequirementsDefinition(
        string Auth,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions
    );

    private sealed record ConnectionBlockDefinition(
        string Method,
        IReadOnlyDictionary<string, string> Parameters
    );
}
