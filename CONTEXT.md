# Context

_Last updated: 2026-04-23_

## Purpose

PowerPack is the standalone solution registry and dependency resolver for packaged Power Platform solutions.

It owns:

- the PowerPack source projects under `source/`
- the reusable Terraform module under `infra/`
- the GitHub Actions workflows under `.github/workflows/`

## Current State

- The API stores manifests in Azure Table Storage and package zips in Azure Blob Storage.
- Package publish uploads the managed solution zip directly to the API.
- The API generates the manifest server-side from the uploaded zip.
- Mixed-case manifest-name contamination is now blocked during index upsert so one solution partition cannot contain both `Core` and `core` style entries.
- The resolver now suppresses built-in platform solutions using the checked-in built-in registry for both direct roots and transitive dependencies, so indexed package manifests can safely reference Microsoft-managed solutions without requiring package records for them.
- The shared manifest builder accepts both the newer `Other/Customizations.xml` layout and the older flat `customizations.xml` layout used by legacy managed solution exports.
- The shared manifest builder now correctly parses connection parameter keys that themselves contain colons, such as `token:clientId`, `token:clientSecret`, and `token:TenantId`.
- The shared manifest builder now also infers Dataverse attachment-policy requirements from packaged solution contents.
  - explicit packaged files with blocked extensions, such as `*.js`, are detected directly from the archive
  - classic Dataverse web resources are also inspected through `customizations.xml` so a `WebResourceType` of script still produces an attachment-policy requirement even when the packaged payload path has no filename extension
- Resolution returns signed PowerPack download URLs for packages.
- The source layout is split into `source/Core/`, `source/API/`, `source/CLI/`, and `source/Tests/`.
- The CLI is a .NET tool with package id `PowerPack.Cli` and command `powerpack`.
- The CLI uses the same shared C# manifest-building code as the API through the `Core` project.
- The CLI now also owns `missingdependencies.yml` parsing and emits a generic deployment graph with:
  - `roots`
  - `topological_order`
  - `nodes`
  - package identities, connection references, environment variables, and download URLs per node
  - graph-level environment requirements, including the reconciled Dataverse blocked attachment extension list
- The CLI can install a PowerPack package and its package-managed dependencies directly into a Dataverse environment with `powerpack install-package`.
  - it resolves the requested root package through the PowerPack API
  - it downloads every resolved package
  - it imports packages with `pac solution import` in dependency-first order
  - optional per-package PAC deployment settings can be supplied from a settings directory
- `infra/` is now a generic Terraform module rather than an environment-specific Terraform root.
- Release packaging now produces two paired artifacts: `released-package.zip` for the API and `module-<version>.zip` for the Terraform module with a baked reference to that API asset URL.
- `test/` now owns reusable test fixtures, including the release-consumer Terraform fixture and .NET-generated Power Platform solution package fixtures.
- Tests now cover resolver-generated tokenized download URLs and the anonymous package download flow.
- API authorization now runs in the Function code so protected endpoints require Entra app-role tokens while package download remains publicly reachable with a signed query token.
- GitHub Actions now owns CI and tagged release packaging.
- Contract definitions live in C# models and validators; there is no parallel JSON schema source of truth.
- The default blocked Dataverse attachment extension list is checked into `source/Core/default-blocked-attachment-extensions.txt` and loaded by shared Core code.
- Documentation examples use a shared neutral solution set:
  - `WorkspaceForms`
  - `ExperienceHub`
  - `SharedFoundation`
  - `TableToolkit`
  - `FieldOperations`

## Architecture

- `source/Core/`
  - shared domain and contract code
  - manifest building
  - dependency resolution
  - storage abstractions
- `source/API/`
  - .NET 10 isolated Azure Functions app
  - Azure-backed storage and download implementations
- `source/CLI/`
  - Spectre.Console command-line tool
  - local manifest build
  - API publish
  - API resolve-set
  - deployment-graph generation from source `missingdependencies.yml`
  - direct operator package install through `pac solution import`
- `source/Tests/`
  - xUnit coverage for shared/core behavior, API options, and tokenized package download flow
- `test/`
  - Terraform consumer fixture for released module validation
  - .NET fixture generator for deterministic Power Platform solution zip packages
- `infra/`
  - reusable Terraform module for PowerPack infrastructure
- `.github/workflows/`
  - CI and release automation

## Key Decisions

- No silent fallbacks.
- PowerPack is both the dependency index and the package registry.
- Blob access is never exposed directly to consumers; downloads go through signed PowerPack API URLs.
- Manifest generation logic lives in shared C# code that is consumed by both the API and CLI.
- Dependency-root inference and deployment-graph construction also live in shared C# code so downstream tooling does not re-implement PowerPack policy in another language.
- Deployment-graph generation is also responsible for reconciling package-driven environment requirements, including Dataverse attachment-extension policy, so downstream consumers only apply resolved target state.
- Direct CLI installation is an operator path for installing package zips and dependencies; it deliberately does not provision Dataverse environments, service identities, or connector instances.
- The CLI and API may authenticate differently, but they must use the same domain logic.
- Delivery automation is GitHub-native through GitHub Actions workflows in `.github/workflows/`.
- The Terraform code in `infra/` is consumed as a module by caller-owned root configurations.
- The released Terraform module artifact, not the source tree, is the self-contained unit that carries the paired API package URI.
- Contract and validation rules should not be duplicated in hand-maintained JSON schema files.
- Flex Consumption deployment stays on `azurerm`; only the `onedeploy` extension is applied through an `azapi_resource` ARM deployment wrapper.
- Function host storage uses managed identity with `AzureWebJobsStorage__accountName`, `AzureWebJobsStorage__credential=managedidentity`, and an explicit blank `AzureWebJobsStorage`.
- PowerPack download token signing uses a Key Vault secret reference rather than a plain app setting.
- Application Insights ingestion uses Entra authentication with `APPLICATIONINSIGHTS_AUTHENTICATION_STRING=Authorization=AAD`.
- Function App Easy Auth is intentionally not used because its path-exclusion model does not fit the dynamic anonymous package download route.

## Invariants

- Versions are normalized to four numeric segments for comparison.
- Solution names are compared case-insensitively and preserved case-sensitively.
- Managed solution package ingestion must accept both modern and legacy customization entry locations.
- Description-block connection parameter keys may contain embedded colons and must be parsed without truncating the key name.
- Package blob tags are:
  - `Package`
  - `Version`
  - `Quality`
- Supported package quality values are:
  - `local`
  - `prerelease`
  - `release`
- Dataverse attachment policy is modeled as:
  - package-level required allowed attachment extensions
  - graph-level default blocked attachment extensions
  - graph-level effective blocked attachment extensions after reconciliation

## Follow-up

- Decide whether release assets alone are sufficient or whether the Function App zip should also be published to a package registry.
- Review whether the built-in solution registry should remain checked in here or move to a separate source of truth.
- Add an example root configuration that consumes the Terraform module from GitHub.
- Decide whether GitHub Releases should remain the long-term host for the baked API package URI or whether Azure Blob Storage should own release distribution.
- Clean up older scratch resource groups created while iterating on Flex Consumption OneDeploy behavior.
- Consider adding a small admin/repair flow for enumerating and cleaning legacy mixed-case manifest partitions without touching table storage manually.

## Technical Debt

- The Terraform module still creates its own Entra application and service principal, which is convenient but may be too opinionated for some tenants.
- The release workflow currently publishes the CLI to GitHub Packages; consumers outside GitHub Packages may still want an alternate feed strategy.
- The Key Vault currently uses access policies for the Function identity; revisit whether RBAC-only Key Vault access is viable for this deployment shape.
