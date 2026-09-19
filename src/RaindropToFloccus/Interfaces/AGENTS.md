# Interface guidance

- Interfaces define injectable behaviour and external boundaries; do not create interfaces for records, enums, exceptions, or passive data containers.
- Keep contracts minimal and implementation-independent.
- Add members only when a caller needs the capability; do not expose helper internals for convenience.
- Async I/O and long-running operations accept an optional `CancellationToken` and document any safe-boundary behaviour through naming and implementation.
- Coordinate interface changes with every implementation, registration, and test double in the same change.
