# Infrastructure helper guidance

- Keep each helper focused on one infrastructure concern and expose behaviour through an interface when it is injected.
- `EnvironmentConfigurationHelper` is the only production type allowed to read environment variables. Validate required values, formats, ranges, and relationships during construction.
- `CommandRunner` is the only production type allowed to start external processes.
- Serializers are strict and deterministic. Reject unknown or duplicate properties, invalid IDs, broken parent references, cycles, excessive depth, and unsupported versions.
- Preserve atomic file replacement and durable journal writes. Temporary-file, flush, and rename sequencing is part of the recovery contract.
- Do not silently repair malformed XBEL, state, or journal data.
- Keep logging single-line and human-readable, and sanitize all external error details.
- SSH key material and temporary key paths must be scoped, permission-restricted, and cleaned up without exposing their contents.
