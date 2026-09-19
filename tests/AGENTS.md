# Test guidance

- Tests must be deterministic and isolated. Prefer offline tests with controlled HTTP handlers for protocol and failure behaviour.
- Never require network access or credentials for the default test command.
- Do not enable live tests merely to verify refactoring or documentation changes.
- Test names should describe observable behaviour and expected safety properties.
- Preserve warnings-as-errors and avoid timing-dependent assertions; use explicit synchronization with bounded failure deadlines.
- Keep fixtures and generated reports free of credentials, authorization headers, and raw response bodies.
