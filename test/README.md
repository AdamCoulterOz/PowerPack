# Test

This folder holds reusable test fixtures and fixture-generation code that sit outside the production projects.

## Layout

- `fixtures/release-consumer/`
  - Terraform consumer fixture that points at a published module release artifact
- `fixtures/solution-packages/`
  - Generated Power Platform solution zip fixtures
- `src/PowerPack.TestFixtures/`
  - .NET fixture generator for deterministic Power Platform package zips

## Regenerate Solution Packages

Run this from the repo root:

```bash
dotnet run --project ./test/src/PowerPack.TestFixtures
```
