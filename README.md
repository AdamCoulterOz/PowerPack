# PowerPack

PowerPack is a standalone solution package registry and dependency resolver for Power Platform solutions.

The source of truth for API and manifest contracts is the C# code under `source/Core/Models/` and the validation logic under `source/Core/Services/`.

For the fuller architecture and usage model, see [architecture](./ARCHITECTURE.md).

It strips the dependency model back to the only stable primitives that matter:

- solution `name`
- solution `version`
- minimum-version dependency comparison

Packages, storage, and download transport are treated as install-time plumbing. They are not the dependency model.

Documentation examples use these neutral sample solution names consistently:

- `WorkspaceForms`
- `ExperienceHub`
- `SharedFoundation`
- `TableToolkit`
- `FieldOperations`

## Architecture

PowerPack is split into three layers:

1. `manifest`
   - a solution manifest declares the solution name, version, publisher, and direct dependencies
2. `index`
   - Azure Table Storage holds:
     - `solutionindex`
     - `dependencyindex`
   - Azure Blob Storage holds the managed solution zip packages
3. `resolver`
   - a stateless .NET 10 Azure Function App on the Flex Consumption plan reads the index and returns deterministic dependency plans

## Terraform Module

`infra/` is a reusable Terraform module for deploying PowerPack into an Azure subscription.

- the module does not own backend configuration
- the module does not assume Azure DevOps
- the calling root config is responsible for:
  - provider configuration
  - backend/state configuration
  - selecting variable values that fit the target tenant and subscription

Example:

```hcl
module "powerpack" {
  source = "https://github.com/org/repo/releases/download/v1.2.0/module-1.2.0.zip"

  name_prefix = "powerpack-prod"
  location    = "australiaeast"
}
```

Important:

- the module derives its internal resource names from `name_prefix`
- the storage account and function app names are generated to remain globally unique
- released module artifacts bake in the matching API release asset URL so the module is self-contained
- source-tree module files are not the deployment artifact; the packaged release module is
- the module does not configure Flex Consumption always-ready instances, so the app remains eligible to scale to zero when idle
- the module outputs the Function App hostname, API base URL, Entra app identifiers, and storage resource names

## Dependency Rules

- versions are normalized to four numeric segments
  - `1` -> `1.0.0.0`
  - `1.2` -> `1.2.0.0`
  - `1.2.3` -> `1.2.3.0`
  - `1.2.3.4` -> `1.2.3.4`
- dependency requirements are minimum-version constraints
- resolution rule is blunt:
  - highest available version that satisfies the minimum wins
- composition rule is blunt:
  - merge all incoming constraints by taking the maximum required version per solution
- if a dependency cannot be satisfied, resolution returns it as missing
- if the index contains case-only solution-name collisions, the service fails loudly

## Built-In Registry

- [`built-in-solutions.json`](./source/Core/built-in-solutions.json) is the checked-in registry of platform-owned solution unique names
- this initial registry was generated from:
  - an internal build environment snapshot
- the current classification rule is blunt:
  - `ismanaged = true`
  - publisher unique name is one of:
    - `MicrosoftCorporation`
    - `adxstudio`
    - `microsoftdynamics`
    - `microsoftdynamicslabs`
    - `microsoftfirstparty`
- this registry exists so dependency analysis can distinguish:
  - platform solutions that must already exist in the target environment
  - package-managed solutions that must exist in the PowerPack registry
- the shared dependency Terraform generator uses this registry as an explicit exclusion set
  - built-in solution dependencies are not resolved through the PowerPack index
  - built-in solution dependencies do not produce package nodes or Terraform inter-package edges

## Tables

### `solutionindex`

- `PartitionKey`
  - case-invariant solution lookup key
- `RowKey`
  - normalized four-part version
- stores:
  - exact-cased solution name
  - publisher
  - serialized manifest JSON
  - dependency JSON
  - connections JSON
  - variables JSON
  - package blob metadata:
    - blob name
    - file name
    - content type
    - content length
    - quality

### `dependencyindex`

- `PartitionKey`
  - case-invariant dependency lookup key
- `RowKey`
  - `<dependent-key>|<dependent-version>`
- stores:
  - exact-cased dependency and dependent names
  - normalized dependent version
  - normalized required version

## API

The Function App exposes:

- `POST /api/packages`
- `PUT /api/packages/{name}/{version}`
- `GET /api/packages/{name}`
- `GET /api/packages/{name}/{version}`
- `DELETE /api/packages/{name}/{version}`
- `POST /api/resolve`
- `POST /api/validate`
- `POST /api/resolve-set`
- `GET /api/dependents/{name}`
- `GET /api/packages/{name}/{version}/download`

Requests and responses are modeled and validated in C#.

API authorization is split deliberately:

- management and resolver endpoints require an Entra bearer token with the configured PowerPack app role
- `GET /api/packages/{name}/{version}/download` stays anonymous at the platform edge and is protected by the signed PowerPack download token instead
- request authorization is enforced in the API code rather than Function App Easy Auth because the Flex Consumption `excludedPaths` model does not cleanly support this dynamic download route

Manifest publish now uploads the managed solution zip directly to PowerPack.

- the API validates and inspects the uploaded zip in memory
- the API generates the full manifest server-side
- the API stores the package in blob storage with blob tags:
  - `Package`
  - `Version`
  - `Quality`
- publish fails loudly if the zip cannot be processed into a clean manifest

`POST /api/resolve-set` now returns each resolved node with:

- its normalized manifest payload
- package metadata
- a time-limited PowerPack download URL

That lets callers project the full deployment contract without issuing follow-up manifest lookups or any extra package-download sidecar.

The CLI also provides `powerpack resolve-deployment-graph`, which parses a source `missingdependencies.yml`, calls `resolve-set`, and emits a generic deployment graph with roots, topological order, per-package deployment metadata, identities, connection references, and environment variables.

`GET /api/packages/{name}/{version}/download` is the direct package download endpoint.

- it requires a signed PowerPack download token
- it streams the stored zip from blob storage
- it sets:
  - `Content-Type`
  - `Content-Disposition`
  - `Content-Length`

## GitHub Actions

This repo uses GitHub Actions instead of Azure DevOps.

- [`.github/workflows/ci.yml`](./.github/workflows/ci.yml)
  - restores
  - tests
  - validates the Terraform module
  - packs the CLI
  - publishes a zipped Function App build artifact
- [`.github/workflows/release.yml`](./.github/workflows/release.yml)
  - runs on version tags like `v1.2.3`
  - packs the CLI with the tag version
  - builds `released-package.zip` for the Function App
  - builds `module-<version>.zip` with a baked reference to that release's API package asset URL
  - publishes the CLI package to GitHub Packages
  - creates a GitHub release with the packaged artifacts

## CLI

- `source/CLI/` contains the .NET tool package
- package id:
  - `PowerPack.Cli`
- command name:
  - `powerpack`
- implementation details:
  - C#
  - `Spectre.Console`
  - `Azure.Identity` with Azure CLI token acquisition

The CLI shares PowerPack contracts with the API and is intended to be installed by the shared Dataverse pipeline as a NuGet tool.

GitHub Packages publish is handled by the release workflow.

## Contracts

- request and response contracts are defined by the C# models in `source/Core/Models/`
- manifest validation and normalization rules are enforced in C# service code
- there is no separate hand-maintained JSON schema layer in this repo

## Layout

- `source/`
  - `Core/`
    - shared domain models, validation, resolver logic, manifest builder, and storage abstractions
  - `API/`
    - .NET 10 isolated Azure Function App host and Azure-backed implementations
  - `CLI/`
    - .NET tool host for local build/publish/resolve commands
  - `Tests/`
    - test coverage for core logic and API-adjacent behavior
- `infra/`
  - reusable Terraform module that provisions the PowerPack runtime infrastructure
  - provisions:
    - Flex Consumption plan (`FC1`)
    - system-assigned managed identity on the Function App
    - private blob container for deployment packages
    - Azure Table Storage access via managed identity

## Storage Auth

- the Function App uses its system-assigned managed identity for both:
  - `AzureWebJobsStorage`
  - PowerPack storage access
- `AzureWebJobsStorage` is configured with:
  - `AzureWebJobsStorage__accountName`
  - `AzureWebJobsStorage__credential=managedidentity`
  - `AzureWebJobsStorage=""`
- PowerPack table access is configured with:
  - `PowerPack__Storage__AccountUrl`
- PowerPack blob access is configured with:
  - `PowerPack__Storage__BlobAccountUrl`
  - `PowerPack__Storage__PackageContainerName`
- PowerPack download token signing is configured with:
  - `PowerPack__Downloads__TokenSigningKey` as a Key Vault reference
- API bearer-token validation is configured with:
  - `PowerPack__Auth__ApplicationClientId`
  - `PowerPack__Auth__ApplicationIdUri`
  - `PowerPack__Auth__TenantId`
  - `PowerPack__Auth__RequiredRole`
- Application Insights ingestion is configured with:
  - `APPLICATIONINSIGHTS_AUTHENTICATION_STRING=Authorization=AAD`
- the Function App identity is granted:
  - `Storage Blob Data Owner`
  - `Storage Table Data Contributor`
  - `Monitoring Metrics Publisher`

## Deployment Graph

- `azurerm` owns the durable infrastructure:
  - resource group
  - storage
  - Flex plan
  - Function App
  - role assignments
  - Key Vault and secret reference wiring
- the Function App owns table creation and schema usage:
  - `solutionindex`
  - `dependencyindex`
- released Terraform module artifacts deploy Function App code through an AzAPI-managed `Microsoft.Resources/deployments` wrapper that applies `Microsoft.Web/sites/extensions/onedeploy` using their baked `released-package.zip` asset URL
- there is no shell polling or `local-exec` in the Terraform path
