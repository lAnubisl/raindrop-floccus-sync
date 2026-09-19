# Production application guidance

These instructions apply to the worker project and are additional to the repository-level guidance.

## Boundaries

- `Program.cs` is the composition root. Keep business logic out of startup and dependency registration.
- `Clients/` owns external Raindrop and Git communication.
- `Helpers/` owns infrastructure details such as configuration, commands, SSH, logging, serialization, storage, and journaling.
- `Services/` owns initialization, comparison, planning, synchronization, scheduling, and health transitions.
- `Models/` contains data contracts and typed identities; `Interfaces/` contains injectable boundaries.

## Invariants

- The worker targets only `main`, root `bookmarks.xbel`, and `.raindrop-sync/state.json`.
- A synchronization commit may change only XBEL and state.
- Validate the complete source and plan before the first side effect.
- Keep XBEL and Raindrop snapshots separate because Raindrop may normalize values.
- Treat a sent write without a durably recorded response as an unknown outcome; never replay it automatically.
- Preserve the persistent Git working directory and local journal across restarts.
- Honour cancellation before new work while allowing an in-flight bounded write and journal update to finish safely.

## Style

- Prefer immutable records for transported state.
- Keep async methods cancellation-aware and thread a `CancellationToken` through I/O boundaries.
- Use ordinal comparisons unless a protocol explicitly requires different semantics.
- Error messages must be actionable without containing secrets or raw external response bodies.
