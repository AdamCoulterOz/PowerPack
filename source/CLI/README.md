# PowerPack CLI

`PowerPack.Cli` is the .NET tool for publishing and resolving PowerPack solution packages.

It shares the same manifest and package-processing code as the PowerPack API so local validation and server-side validation stay aligned.

Core commands:

- `powerpack build-manifest`
- `powerpack publish`
- `powerpack resolve-set`
- `powerpack resolve-deployment-graph`

`resolve-deployment-graph` parses a source `missingdependencies.yml`, resolves the non-built-in package set through the API, and emits a generic deployment graph that downstream consumers can adapt to their own infrastructure tooling.
