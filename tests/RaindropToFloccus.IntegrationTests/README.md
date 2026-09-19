# Raindrop client tests

This project contains offline contract tests and opt-in live integration tests for `RaindropApiClient`. It does not start the worker, use Git or Floccus, or run the destructive first-time account replacement.

## Offline contract tests

Offline tests use an in-memory HTTP handler and require neither a token nor network access:

```powershell
dotnet test RaindropToFloccus.slnx --configuration Release --filter Category=RaindropContract
```

They cover pagination and batching, HTTP and transport failures, rate-limit headers, malformed responses, validation, cancellation boundaries, duplicate IDs, partial results, and Unicode/URL transport fidelity.

## Live integration tests

Live tests use a real Raindrop account; Raindrop does not provide a sandbox. They run only when both variables are present:

```text
RAINDROP_RUN_INTEGRATION_TESTS=1
RAINDROP_API_TOKEN=<personal token>
```

Inject the token through a secret store or the process environment, then run:

```powershell
dotnet test RaindropToFloccus.slnx --configuration Release --filter Category=RaindropIntegration
```

Without explicit opt-in, live tests are reported as skipped. Opting in without a token fails before any account mutation. Never put the token in source files, test parameters, committed configuration, logs, or reports.

Live coverage includes authenticated reads; collection creation, nesting, moves, and deletion; bookmark creation and updates; duplicate names and URLs; `Unsorted`; pagination and batch boundaries; cancellation; server title normalization; and preservation of fields outside the synchronization scope.

## Isolation and cleanup

Live resources use a random `r2f-it-<run-id>-` collection prefix and a unique `https://example.com/raindrop-integration/<run-id>/` URL prefix. Tests are serialized for the assembly.

Cleanup uses independent HTTP requests so a client parsing failure cannot prevent recovery. It removes test collections and moves active test bookmarks to Trash. It does not empty Trash or delete pre-existing account data. A normal complete run leaves its test bookmarks recoverable in Trash.

Forced process termination cannot guarantee cleanup. Use the run ID in the test output to identify leftovers; never clear the whole account to recover a failed run.

Test result files and sanitized cleanup reports are written below `TestResults/` and the test output directory. They exclude tokens, request headers, and raw response bodies.

These tests validate the Raindrop client contract, not end-to-end synchronization or crash recovery.
