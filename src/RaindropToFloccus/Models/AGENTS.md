# Model guidance

- Prefer immutable records and readonly record structs for domain values.
- Keep stable folder and bookmark identities strongly typed and independent from XBEL IDs, Raindrop IDs, titles, and URLs.
- Do not add order as domain state; synchronization intentionally treats item order as insignificant.
- Model XBEL and Raindrop snapshots separately so server normalization does not appear as a user edit.
- Exceptions are concrete classes in individual files and must not carry secrets or raw response bodies.
- Changes to state, journal, or plan models require corresponding strict serializer validation and compatibility consideration.
- Avoid behaviour and I/O in models; validation that depends on whole trees belongs in serializers, comparers, or planners.
