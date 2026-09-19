# Raindrop client test guidance

- `Category=RaindropContract` is offline and must use `ScriptedHttpClientFactory` or another in-memory handler.
- `Category=RaindropIntegration` targets a real account and must use `IntegrationFactAttribute` so it remains skipped without `RAINDROP_RUN_INTEGRATION_TESTS=1` and `RAINDROP_API_TOKEN`.
- Keep assembly-level parallelization disabled; live account mutations are serialized.
- Live resources must use the per-run collection and URL prefixes. Cleanup may touch only resources registered for that run.
- Cleanup moves test bookmarks to Trash and removes test collections; it must never empty Trash or delete pre-existing resources.
- Use independent HTTP calls for cleanup and verification so a defect in `RaindropApiClient` cannot hide leftovers.
- A failed or interrupted live run must report its run ID and preserve enough sanitized evidence for targeted cleanup.
- Keep live tests focused on the client contract; do not imply that they validate the worker, Git integration, or crash recovery.
