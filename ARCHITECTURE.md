# PowerPack Architecture

## Purpose

PowerPack is the solution registry and dependency resolver for packaged Power Platform solutions.

Its job is narrow:

- store normalized manifests for published solutions
- answer deterministic dependency queries
- provide a stable deployment contract that is independent of any one client tool

## Core Model

PowerPack reduces the domain to the only stable primitives that matter:

- solution `name`
- solution `version`
- direct dependencies as `solution -> minimum version`

Everything else is metadata attached to a solution version:

- `publisher`
- `connections`
- `variables`
- `metadata`

Packages, artifact feeds, anchors, marker solutions, and deployment scripts are transport concerns. They are not the dependency model.

## Manifest Contract

Each indexed solution version is represented by a manifest with this shape:

```json
{
  "name": "WorkspaceForms",
  "version": "1.44.0.0",
    "publisher": "ExamplePublisher",
  "dependencies": {
    "SharedFoundation": "1.3.0.0",
    "TableToolkit": "1.1.0.0"
  },
  "connections": {},
  "variables": {},
  "metadata": {}
}
```

### Invariants

- `name` must be non-empty
- `publisher` must be non-empty
- `version` is normalized to four numeric parts
- dependency names are case-invariant for comparison
- case-only dependency collisions are rejected
- dependency versions are normalized to four numeric parts

Examples:

- `1` becomes `1.0.0.0`
- `1.2` becomes `1.2.0.0`
- `1.2.3` becomes `1.2.3.0`
- `1.2.3.4` stays `1.2.3.4`

## Resolution Semantics

PowerPack uses minimum-version semantics only.

That means:

- a dependency declares the minimum acceptable version
- a candidate satisfies the dependency when `installed >= required`
- when multiple indexed versions satisfy a requirement, the highest version wins
- when multiple roots require the same dependency, the merged requirement is the highest minimum version

This makes composition deterministic.

There is no special conflict model beyond:

- missing dependency: nothing indexed satisfies the minimum
- invalid dependency graph: cycles or non-converging constraint expansion

## Architectural Layers

PowerPack is split into three layers.

### 1. Manifest

The manifest is the source of truth for a single solution version.

It declares:

- solution identity
- dependency intent
- deployment-facing metadata such as connection and variable requirements

### 2. Index

The index is the persistence layer.

Today it is implemented on Azure Table Storage with two tables:

- `solutionindex`
- `dependencyindex`

Those table names are part of the API contract, not Terraform-managed resources. The Function App creates them on demand.

The index exists to make the important queries cheap:

- list all versions of a solution
- get one exact solution version
- resolve forward dependencies
- find reverse dependents

### 3. Resolver

The resolver is a stateless API layer.

Today it is implemented as a .NET 10 Azure Function App. Its responsibilities are:

- normalize incoming requests
- read manifests from the index
- expand transitive dependencies
- merge version constraints
- select the highest satisfiable version per solution
- return resolved, missing, and invalid outcomes

The resolver owns no durable state beyond the index it reads and writes.

## Storage Shape

### `solutionindex`

Logical key:

- partition key: case-invariant solution name
- row key: normalized version

Stored values include:

- exact-cased solution name
- normalized version
- publisher
- full manifest JSON
- serialized dependency JSON
- serialized connections JSON
- serialized variables JSON
- serialized metadata JSON

### `dependencyindex`

Logical key:

- partition key: case-invariant dependency name
- row key: `<dependent>|<dependentVersion>`

Stored values include:

- dependency name
- dependent solution name
- dependent version
- required version

This table is purely an index for reverse lookups. It is not a second source of truth.

## API Contract

PowerPack exposes a small API surface:

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

### `POST /api/packages`

Creates or upserts a package-backed manifest from a managed solution zip upload.

Use this when a publisher wants PowerPack to inspect a managed solution package, generate the normalized manifest server-side, store the zip, and index the result.

### `PUT /api/packages/{name}/{version}`

Creates or upserts a package-backed manifest at an explicit identity.

The route and uploaded package must agree on solution name and version. If they do not, the request fails.

### `GET /api/packages/{name}`

Lists all indexed versions of a solution, newest first.

### `GET /api/packages/{name}/{version}`

Returns a single normalized manifest version.

### `DELETE /api/packages/{name}/{version}`

Deletes a single indexed manifest version and its reverse dependency rows.

### `POST /api/resolve`

Resolves one root solution reference.

Request:

```json
{
  "name": "WorkspaceForms",
  "version": "1.44"
}
```

### `POST /api/validate`

Validates an arbitrary dependency set without naming a root solution.

Request:

```json
{
  "dependencies": {
    "SharedFoundation": "1.3.0.0",
    "TableToolkit": "1.1.0.0"
  }
}
```

### `POST /api/resolve-set`

Resolves multiple root solutions as one composed requirement set.

Request:

```json
{
  "solutions": [
    { "name": "WorkspaceForms", "version": "1.44" },
    { "name": "ExperienceHub", "version": "2.0" }
  ]
}
```

Response includes:

- `status`
- `roots`
- `constraints`
- `resolved`
- `missing`
- `invalid`

Each `resolved` item also includes:

- the normalized manifest payload
- stored package metadata
- a time-limited PowerPack download URL

That lets a caller immediately project deployment requirements and download the selected packages without follow-up manifest lookups or a separate artifact registry hop.

### `GET /api/dependents/{name}`

Returns the indexed reverse dependency list for one solution.

### `GET /api/packages/{name}/{version}/download`

Streams the stored managed solution package for one indexed solution version.

The request must include a valid signed PowerPack download token.

## Auth Model

PowerPack is an API, not a public bucket.

The current deployment uses Microsoft Entra authentication in front of the Function App. Callers present tokens for the PowerPack API audience and are admitted based on the configured allowed client applications.

That auth model is an implementation choice around the API boundary. It does not change the PowerPack domain contract.

## Intended Usage

PowerPack is intended to be consumed in the same way regardless of whether the caller is:

- a CI pipeline
- a deployment orchestrator
- a packaging tool
- a custom CLI
- a UI

The generic usage flow is:

1. Build the managed solution package.
2. Upload that package to PowerPack.
3. Let PowerPack generate and index the normalized manifest from the uploaded zip.
4. Ask PowerPack to resolve one solution or a set of solutions.
5. Use the returned manifests and signed package URLs to build and execute the deployment plan.

### What PowerPack Does

- stores manifests
- stores managed solution packages
- normalizes versions
- enforces case-invariant identity rules
- resolves direct and transitive dependencies
- merges multi-root constraints deterministically
- exposes reverse dependency lookups
- issues time-limited package download URLs

### What PowerPack Does Not Do

- install solutions into environments
- mutate environments directly
- decide how a caller applies resolved manifests after package retrieval

That split is intentional. PowerPack owns dependency truth and package distribution, not environment mutation.

## Built-In Solutions

Some solution dependencies are platform-owned and are expected to already exist in the target environment.

The checked-in registry for that classification lives at:

- [built-in-solutions.json](source/Core/built-in-solutions.json)

This registry is not part of the core PowerPack resolver contract. It is a policy input used by surrounding tooling to decide which dependencies must be resolved through package management and which are treated as environment prerequisites.

## Current Azure Deployment

The current hosted implementation is:

- Azure Function App
- .NET 10 isolated worker
- Flex Consumption plan
- system-assigned managed identity
- Azure Table Storage for index persistence
- Microsoft Entra auth in front of the API

The Function App uses managed identity for:

- host storage access
- table access

This keeps storage credentials out of application configuration.

## Design Principles

PowerPack is deliberately opinionated:

- versions are numeric and normalized
- names compare case-invariantly
- highest satisfiable version wins
- missing or invalid conditions fail loudly
- the resolver is stateless
- persistence is optimized for query shape, not relational purity
