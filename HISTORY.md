# PowerPack History

## 2026-05-08: Repository Contract Baseline

The primary repository now carries `INTERFACE.md` as its compressed public boundary contract and keeps `CONTEXT.md` focused on current operational state.

This baseline records that PowerPack is the generic upstream product repository. Organisation-specific pipelines and deployment overrides belong in downstream forks or consumers rather than in the primary contract.

## 2026-05-08: Source-Active Flow Metadata

Manifests and deployment graphs now include source-active modern flow metadata derived from packaged Power Platform `Workflow` components.

Only modern flows with `Category: 5`, `StateCode: 1`, and `StatusCode: 2` are included. Draft and inactive flows are deliberately excluded so downstream deployment tooling receives an activation contract rather than a raw package inventory.

## 2026-05-08: Dataverse URL Environment Context

The CLI moved to `--environment-url` as the public environment context for manifest build and publish operations. The CLI resolves the Power Platform environment id internally from Dataverse when connector metadata enrichment needs it.

The older `--power-platform-environment-id` option was removed from the CLI surface. This is an intentional breaking change so callers use the stable Dataverse environment URL rather than passing an internal Power Platform environment id. The new `--solution` manifest source exports the unmanaged solution directly from Dataverse before inspection.

## 2026-05-16: Stable Core Library Boundary For CrmProxy

`PowerPack.Core` now publishes the reusable library boundary for CrmProxy-style consumers. Built-in Power Platform solution knowledge, PowerPack API package publish/resolve/download primitives, Dataverse solution export/import primitives, and dependency-first deployment planning are exposed as Core APIs.

The CLI now consumes those Core APIs instead of carrying private Dataverse and PowerPack HTTP helpers. Package import uses Dataverse Web API solution actions directly, so PAC CLI behavior remains a reference for compatible semantics rather than a runtime dependency.

## 2026-05-16: Key Vault RBAC And Host Storage Settings

The Terraform module moved completely from Key Vault access policies to Azure RBAC for secret access. The deployment principal receives `Key Vault Secrets Officer` for Terraform-managed secret lifecycle, and the Function host identity receives `Key Vault Secrets User` for runtime Key Vault references.

The Flex Consumption Function App now omits the legacy `AzureWebJobsStorage` app setting and keeps only the identity-based host storage settings. This removes the previous explicit blank setting that could drift through provider/API normalization.
