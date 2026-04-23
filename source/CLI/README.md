# PowerPack CLI

`PowerPack.Cli` is the .NET tool for publishing and resolving PowerPack solution packages.

It shares the same manifest and package-processing code as the PowerPack API so local validation and server-side validation stay aligned.

Core commands:

- `powerpack build-manifest`
- `powerpack publish`
- `powerpack resolve-set`
- `powerpack resolve-deployment-graph`
- `powerpack install-package`

The CLI uses the signed-in Azure CLI user and requests the delegated PowerPack API scope:

- `<application-id-uri>/user_impersonation`

`resolve-deployment-graph` parses a source `missingdependencies.yml`, resolves the non-built-in package set through the API, and emits a generic deployment graph that downstream consumers can adapt to their own infrastructure tooling.

`install-package` resolves a root package through the PowerPack API, downloads the root package and all package-managed dependencies, and imports them into a target Dataverse environment in dependency-first order using `pac solution import`. If imports need PAC deployment settings for connection references or environment variables, provide `--settings-directory` with per-package JSON files named by package name, transport package name, or solution unique name.
