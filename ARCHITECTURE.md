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
  "name": "MetaForm",
  "version": "1.44.0.0",
    "publisher": "ExamplePublisher",
  "dependencies": {
    "Core": "1.3.0.0",
    "EditableTable": "1.1.0.0"
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

- `POST /api/manifests`
- `PUT /api/manifests/{name}/{version}`
- `GET /api/manifests/{name}`
- `GET /api/manifests/{name}/{version}`
- `DELETE /api/manifests/{name}/{version}`
- `POST /api/resolve`
- `POST /api/validate`
- `POST /api/resolve-set`
- `GET /api/dependents/{name}`

### `POST /api/manifests`

Creates or upserts a manifest from a request body.

Use this when a publisher has produced a manifest for a solution version and wants it indexed.

### `PUT /api/manifests/{name}/{version}`

Upserts a manifest at an explicit identity.

The route and body must agree. If they do not, the request fails.

### `GET /api/manifests/{name}`

Lists all indexed versions of a solution, newest first.

### `GET /api/manifests/{name}/{version}`

Returns a single normalized manifest version.

### `DELETE /api/manifests/{name}/{version}`

Deletes a single indexed manifest version and its reverse dependency rows.

### `POST /api/resolve`

Resolves one root solution reference.

Request:

```json
{
  "name": "MetaForm",
  "version": "1.44"
}
```

### `POST /api/validate`

Validates an arbitrary dependency set without naming a root solution.

Request:

```json
{
  "dependencies": {
    "Core": "1.3.0.0",
    "EditableTable": "1.1.0.0"
  }
}
```

### `POST /api/resolve-set`

Resolves multiple root solutions as one composed requirement set.

Request:

```json
{
  "solutions": [
    { "name": "MetaForm", "version": "1.44" },
    { "name": "Portal", "version": "2.0" }
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

Each `resolved` item also includes the normalized manifest payload, so a caller can immediately project deployment requirements without follow-up lookups.

### `GET /api/dependents/{name}`

Returns the indexed reverse dependency list for one solution.

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

1. Build or extract a solution manifest.
2. Publish that manifest to the PowerPack registry.
3. Ask PowerPack to resolve one solution or a set of solutions.
4. Use the returned normalized manifests to build a deployment plan.
5. Install or validate the selected solution versions using whatever transport tooling you prefer.

### What PowerPack Does

- stores manifests
- normalizes versions
- enforces case-invariant identity rules
- resolves direct and transitive dependencies
- merges multi-root constraints deterministically
- exposes reverse dependency lookups

### What PowerPack Does Not Do

- fetch package ZIPs
- own artifact-feed transport
- install solutions into environments
- mutate environments directly
- decide how a caller obtains solution packages
- decide how a caller applies resolved manifests

That split is intentional. PowerPack owns dependency truth, not package transport.

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
