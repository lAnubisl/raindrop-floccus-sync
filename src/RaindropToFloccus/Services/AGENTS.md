# Synchronization service guidance

- Keep initialization, comparison, planning, application, scheduling, and health responsibilities separate.
- Build and validate a complete plan before side effects. The comparer and planner remain pure in-memory operations.
- Git wins entity conflicts, including change-versus-delete. Do not introduce field-level merges or title/URL deduplication.
- Apply tree changes in dependency-safe order: detach moves, create/update parents before children, move bookmarks before deleting collections, and delete children before parents.
- Record write intent before every Raindrop mutation and confirmed results immediately afterward. Never advance state generation before both sides are verified.
- Reconcile again when the Git revision or expected Raindrop snapshot changes. Do not overwrite concurrent work.
- Keep cycles serialized. Retry only failures classified as retryable; recovery-required and fatal conditions must preserve evidence.
- Health reflects the latest completed attempt. Shutdown is not a failed synchronization.
