# Repository guidance

## Scope

This repository contains a .NET 10 Docker worker that synchronizes Raindrop.io with a Floccus XBEL file in Gitea. Treat data-loss prevention, stable identity, and recoverability as primary requirements.

## Repository map

- `src/RaindropToFloccus/` — production worker.
- `tests/RaindropToFloccus.IntegrationTests/` — offline HTTP contract tests and explicitly enabled live Raindrop tests.
- `README.md` — operator documentation.
- `architecture.md` — durable product and recovery contracts.
- `Dockerfile` and `docker-compose.yml` — container deployment.

## Global rules

- Write documentation in English. Keep it focused on the current product; do not add implementation diaries, completed-stage lists, dated verification notes, or development progress reports.
- Preserve the destructive first-run guard: only a genuinely absent `.raindrop-sync/state.json` may trigger Git-to-Raindrop initialization. Invalid state must fail closed.
- Preserve stable IDs and one-to-one mappings. Never deduplicate folders or bookmarks by title or URL.
- Git wins conflicting changes. Keep planning separate from side effects and retain the write-ahead journal around every Raindrop mutation.
- Never log or commit tokens, SSH keys, authorization headers, raw sensitive responses, or deployment files containing secrets.
- Run only offline tests by default. Live Raindrop tests mutate a real account and require explicit user authorization plus both opt-in environment variables.
- Keep unrelated user changes intact. Do not delete recovery files or reset repository state to make a test pass.

## Code conventions

- Nullable reference types and warnings-as-errors are enabled.
- Keep one top-level type per file and match the filename to the type.
- Use interfaces for injected behaviour, not for records, enums, exceptions, or other data-only types.
- Access environment variables only through `EnvironmentConfigurationHelper`.
- Start external processes only through `ICommandRunner`/`CommandRunner`.
- Log through the project `ILogger`; direct console output is limited to unavoidable bootstrap failures.
- Pass dependencies and operation state explicitly. Do not move per-cycle state into service fields to shorten method signatures.
- Extract cohesive workflow phases and complex domain decisions into named methods. Simple selectors and predicates may remain lambdas.
- Preserve validation order, ID allocation order, batch boundaries, cancellation boundaries, deferred enumeration, atomic writes, and disposal behaviour when refactoring.

## Verification

Use the smallest relevant checks first:

```powershell
dotnet build RaindropToFloccus.slnx --configuration Release
dotnet test RaindropToFloccus.slnx --configuration Release --filter Category=RaindropContract
```

Do not run `Category=RaindropIntegration` unless the user explicitly authorizes changes to the configured live account.
