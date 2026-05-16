# PowerPack CLI

`PowerPack.Cli` is the .NET tool for publishing and resolving PowerPack solution packages.

It shares the same manifest and package-processing code as the PowerPack API so local validation and server-side validation stay aligned.

Core commands:

- `powerpack build-manifest`
- `powerpack publish`
- `powerpack resolve-set`
- `powerpack resolve-deployment-graph`
- `powerpack install-package`

The CLI uses the active Azure CLI identity and requests the PowerPack API default scope:

- `<application-id-uri>/.default`

For pipeline service principals this yields the assigned `PowerPack.Access` app role.
For signed-in users this yields the consented delegated `user_impersonation` scope.

`build-manifest` always takes the Dataverse environment URL as the environment context for connector metadata:

```bash
powerpack build-manifest --environment-url https://example.crm.dynamics.com --package solution.zip
powerpack build-manifest --environment-url https://example.crm.dynamics.com --solution SolutionUniqueName
```

`--package` and `--solution` are mutually exclusive. The `--solution` path exports the unmanaged solution from the environment, then runs the same package manifest builder used by the package path.

`publish` also takes `--environment-url` and resolves the Power Platform environment id internally before uploading the managed zip to the API.

`resolve-deployment-graph` parses a source `missingdependencies.yml`, resolves the non-built-in package set through the API, and emits a generic deployment graph that downstream consumers can adapt to their own infrastructure tooling.

`install-package` resolves a root package through the PowerPack API, downloads the root package and all package-managed dependencies, and imports them into a target Dataverse environment in dependency-first order through Dataverse Web API solution actions. PAC CLI import behavior is a reference point for option semantics only; the command does not require or execute `pac`.

If direct imports need connection-reference or environment-variable component parameters, use `PowerPack.Core` and pass Dataverse Web API `ComponentParameters` through `DataverseSolutionImportOptions`.
