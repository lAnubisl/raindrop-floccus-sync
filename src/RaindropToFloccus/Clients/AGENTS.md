# Client adapter guidance

- Keep HTTP and Git protocol details inside this directory; expose them through the interfaces in `Interfaces/`.
- Never log credentials, authorization headers, private keys, raw response bodies, or raw Git stderr.
- Raindrop redirects remain disabled. Validate status, envelope shape, required fields, IDs, and operation-specific result counts.
- Do not retry writes with an uncertain outcome. A rejected `429` may be retried after the advertised reset because the server confirmed non-application.
- Preserve pagination and API batch limits. Do not infer entity identity from response order, title, or URL.
- Reads may cancel promptly. Once a write is sent, let it reach a response or configured timeout before observing cancellation.
- Git operations must protect `main` with an exact lease and must not overwrite concurrent Floccus commits.
- Route every external command through `ICommandRunner` and every SSH environment through `IGitSshEnvironmentProvider`.
