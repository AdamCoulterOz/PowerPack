# Context

_Last updated: 2026-04-18_

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
- Resolution returns signed PowerPack download URLs for packages.
- The source layout is split into `source/Core/`, `source/API/`, `source/CLI/`, and `source/Tests/`.
- The CLI is a .NET tool with package id `PowerPack.Cli` and command `powerpack`.
- The CLI uses the same shared C# manifest-building code as the API through the `Core` project.
- `infra/` is now a generic Terraform module rather than an environment-specific Terraform root.
- Release packaging now produces two paired artifacts: `released-package.zip` for the API and `module-<version>.zip` for the Terraform module with a baked reference to that API asset URL.
- GitHub Actions now owns CI and tagged release packaging.
- Contract definitions live in C# models and validators; there is no parallel JSON schema source of truth.
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
- `source/Tests/`
  - xUnit coverage for shared/core behavior and API options
- `infra/`
  - reusable Terraform module for PowerPack infrastructure
- `.github/workflows/`
  - CI and release automation

## Key Decisions

- No silent fallbacks.
- PowerPack is both the dependency index and the package registry.
- Blob access is never exposed directly to consumers; downloads go through signed PowerPack API URLs.
- Manifest generation logic lives in shared C# code that is consumed by both the API and CLI.
- The CLI and API may authenticate differently, but they must use the same domain logic.
- Delivery automation is GitHub-native through GitHub Actions workflows in `.github/workflows/`.
- The Terraform code in `infra/` is consumed as a module by caller-owned root configurations.
- The released Terraform module artifact, not the source tree, is the self-contained unit that carries the paired API package URI.
- Contract and validation rules should not be duplicated in hand-maintained JSON schema files.

## Invariants

- Versions are normalized to four numeric segments for comparison.
- Solution names are compared case-insensitively and preserved case-sensitively.
- Package blob tags are:
  - `Package`
  - `Version`
  - `Quality`
- Supported package quality values are:
  - `local`
  - `prerelease`
  - `release`

## Follow-up

- Decide whether release assets alone are sufficient or whether the Function App zip should also be published to a package registry.
- Review whether the built-in solution registry should remain checked in here or move to a separate source of truth.
- Add an example root configuration that consumes the Terraform module from GitHub.
- Decide whether GitHub Releases should remain the long-term host for the baked API package URI or whether Azure Blob Storage should own release distribution.

## Technical Debt

- The Terraform module still creates its own Entra application and service principal, which is convenient but may be too opinionated for some tenants.
- The release workflow currently publishes the CLI to GitHub Packages; consumers outside GitHub Packages may still want an alternate feed strategy.
