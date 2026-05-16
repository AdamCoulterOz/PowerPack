# PowerPack Interface

## Purpose

PowerPack is the package registry, manifest normalizer, and dependency resolver for packaged Power Platform solutions.

It owns the semantic contract for:

- solution package manifests
- package publish and download API routes
- dependency resolution responses
- deployment graph projection
- stable `PowerPack.Core` library APIs for package and Dataverse solution operations
- the reusable Terraform module under `infra/`
- the `powerpack` .NET tool command surface

## Responsibilities

PowerPack currently owns:

- extracting normalized manifest data from managed Power Platform solution zips
- storing package-backed solution versions
- resolving package-managed solution dependencies by minimum version
- producing signed package download URLs
- projecting resolved packages into a deployment graph for downstream tooling
- exposing built-in Power Platform solution knowledge to downstream code
- exposing package publish, resolve, download, Dataverse export, and Dataverse import primitives as library APIs
- deploying its own registry API and storage through a reusable Terraform module

PowerPack may later own:

- additional package artifact distribution targets
- richer deployment graph metadata when it is derivable from the package contract
- administrative repair flows for index hygiene

PowerPack should not own:

- Dataverse environment provisioning
- connector instance creation
- tenant-specific deployment orchestration
- source solution authoring
- organisation-specific pipeline behavior

## Domain Model

- A solution package version is identified by solution unique name and normalized four-part version.
- A manifest describes one solution package version.
- Dependencies are minimum-version constraints on other solution package names.
- A package is the managed solution zip stored behind a manifest.
- A resolution result selects package versions that satisfy one or more roots.
- A deployment graph projects resolved manifests into dependency-first package nodes with deployment metadata.

## Public Interfaces

The API exposes:

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

The CLI exposes:

- `powerpack build-manifest`
- `powerpack publish`
- `powerpack resolve-set`
- `powerpack resolve-deployment-graph`
- `powerpack install-package`

The Terraform module exposes its variable and output surface from `infra/`.

The C# model classes under `source/Core/Models/` are the source of truth for serialized request, response, manifest, and deployment graph shapes.

The `PowerPack.Core` package exposes these stable consumer APIs:

- `PowerPack.Services.BuiltInSolutionRegistry`
- `PowerPack.Services.PowerPackApiClient`
- `PowerPack.Services.DataverseSolutionClient`
- `PowerPack.Services.DependencyDeploymentPlanner`

## Invariants

- Version comparison uses normalized four-part numeric versions.
- Solution names compare case-insensitively but preserve original casing.
- Case-only dependency and index collisions fail loudly.
- Package publish inspects the uploaded zip and generates the manifest server-side.
- Blob package storage is not exposed as the public download contract.
- Download URLs are PowerPack API URLs protected by signed query tokens.
- Protected API routes accept Entra app-role tokens for applications and delegated scope tokens for signed-in users.
- The anonymous package download route is authorized by the signed PowerPack token, not Easy Auth.
- Source-active modern flows are represented as manifest and deployment graph metadata when they are present in the package.
- Draft or inactive modern flows are not included in the activation contract.

## Side Effects

- The API writes manifest rows to Azure Table Storage.
- The API writes package zips to Azure Blob Storage.
- The API creates its table schema on demand through storage clients.
- The CLI can read local package zips and write manifest or graph JSON files.
- `powerpack publish` uploads package zips to the configured PowerPack API.
- `powerpack build-manifest --solution` exports an unmanaged solution from Dataverse before manifest inspection.
- `powerpack build-manifest` and `powerpack publish` resolve the Power Platform environment id from a Dataverse environment URL.
- `powerpack install-package` downloads packages and imports them through Dataverse Web API solution actions.
- The Terraform module provisions Azure infrastructure, grants storage and Key Vault RBAC roles, and applies the Function App package through OneDeploy.

## Dependency Boundaries

Trusted upstream contracts:

- Dataverse managed solution zip structure
- Microsoft connector metadata endpoints
- Entra token validation metadata
- Azure Storage data-plane APIs
- Terraform provider contracts for `azurerm`, `azuread`, `random`, and the AzAPI OneDeploy wrapper

Downstream consumers should depend on:

- documented API routes and model shapes
- the stable `PowerPack.Core` library APIs listed in Public Interfaces
- CLI command behavior
- Terraform module variables and outputs
- deployment graph semantics

Downstream consumers should not depend on:

- Azure Table Storage entity shapes
- internal C# service types outside the public model contract
- PAC CLI as a runtime dependency or durable integration boundary
- release workflow implementation details
- organisation-specific pipelines

## Lifecycle / Execution Model

- The API is a stateless .NET isolated Azure Functions app on Flex Consumption.
- Persistent state lives in Azure Table Storage and Blob Storage.
- Dependency resolution is request-scoped and deterministic.
- Package publish is an authenticated mutation that writes both manifest state and package blob state.
- Package download is anonymous at the Function route level and requires a valid signed PowerPack download token.
- Dataverse solution export/import library operations call Dataverse Web API actions directly.
- Terraform deploys durable infrastructure and applies the API package through the OneDeploy extension.

## Anti-Goals

- PowerPack is not a Dataverse deployment engine.
- PowerPack is not a connector provisioning system.
- PowerPack is not a Power Platform source-control format.
- PowerPack is not a PAC CLI wrapper.
- PowerPack does not hide failed validation behind fallback behavior.
- PowerPack does not treat organisation-specific pipeline conventions as generic product contract.

## Agent Guidance

- Preserve API, CLI, Terraform, and manifest contracts when making internal refactors.
- Update this file when changing public routes, model shapes, deployment graph semantics, Terraform variables or outputs, side effects, or lifecycle boundaries.
- Keep organisation-specific pipeline behavior out of this primary repo.
- Prefer additive CLI changes over breaking option changes unless the user explicitly chooses a contract break.
- Add focused tests for resolver, manifest, graph, and tokenized download behavior when the contract changes.
