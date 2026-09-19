# Raindrop.io ↔ Floccus Sync

A long-running Docker service that synchronizes Raindrop.io bookmarks with a Floccus XBEL file stored in a Gitea repository.

```text
Raindrop API ↔ synchronization service ↔ Git/Gitea ↔ bookmarks.xbel ↔ Floccus
```

After initialization, synchronization is bidirectional. Git wins when the same item is changed on both sides. The service synchronizes folders, bookmark titles, and URLs. Ordering and Raindrop-specific metadata such as tags, notes, descriptions, favourites, and covers are outside the synchronization scope.

## First-run warning

The first run is a **Git-to-Raindrop replacement**, not a merge. If `.raindrop-sync/state.json` is absent from the repository, the service:

1. validates `bookmarks.xbel`;
2. moves every active Raindrop bookmark to Trash;
3. deletes every user-created Raindrop collection;
4. imports the complete XBEL tree into Raindrop;
5. commits `.raindrop-sync/state.json` to Git.

Raindrop Trash is not emptied, but deleted collections are not a reliable backup. Export Raindrop and back up the Git repository before the first run. Run only one service instance, and pause Floccus until initialization has completed.

Never delete `.raindrop-sync/state.json` to reset the service. Its absence starts the destructive first-run procedure again.

## Requirements

- Docker Engine with Docker Compose v2
- A Gitea repository with a `main` branch and SSH read/write access
- A passphrase-free SSH key authorized for that repository
- A personal Raindrop API token
- A complete, valid `bookmarks.xbel` at the root of `main`
- Floccus configured to use the same repository and branch

The service supports only the `main` branch, the root-level `bookmarks.xbel`, and SSH Git URLs. It does not create a missing XBEL file. The Gitea host key is recorded automatically on the first connection.

## Prepare the repository

1. Make sure `main` contains the complete `bookmarks.xbel` that should replace the active Raindrop contents.
2. Confirm that `.raindrop-sync/state.json` is absent only for a genuine first run. If it exists, do not replace or edit it manually.
3. Grant the service SSH key permission to clone and push to the repository.
4. Pause Floccus so it cannot update `main` during initialization.

## Configure Docker Compose

Use a protected deployment checkout and replace the placeholder values in `docker-compose.yml`. In particular, set:

- `GIT_REPOSITORY_URL`
- `GIT_SSH_PRIVATE_KEY`
- `RAINDROP_API_TOKEN`
- `GIT_AUTHOR_NAME`
- `GIT_AUTHOR_EMAIL`

Keep the private key in normal multiline OpenSSH PEM form; Base64 input and passphrase prompts are not supported. Never commit the populated Compose file or expose its secrets in logs, commands, or screenshots.

The supplied Compose file connects the container to an external Docker network named `gitea-network`. Keep that setting when Gitea is reachable there, or adapt the network section to your deployment.

### Environment variables

| Variable | Required | Value or default |
| --- | --- | --- |
| `GIT_REPOSITORY_URL` | Yes | SSH URL such as `git@gitea.example:owner/bookmarks.git` or `ssh://git@gitea.example/owner/bookmarks.git`. |
| `GIT_SSH_PRIVATE_KEY` | Yes | Complete multiline private key without a passphrase. |
| `RAINDROP_API_TOKEN` | Yes | Personal Raindrop token. |
| `GIT_AUTHOR_NAME` | Yes | Author name for synchronization commits. |
| `GIT_AUTHOR_EMAIL` | Yes | Valid author email for synchronization commits. |
| `RAINDROP_REQUEST_TIMEOUT` | No | Positive `TimeSpan`; default `00:00:30`, maximum `00:05:00`. |
| `GIT_WORKING_DIRECTORY` | No | Default `/var/lib/raindrop-to-floccus/repository`. The Compose volume must cover its parent directory. |
| `SYNC_INTERVAL` | No | Positive `TimeSpan`; default `01:00:00`, maximum 24 days. |
| `RETRY_INTERVAL` | No | Positive `TimeSpan`; default `00:05:00`, maximum 24 days. |
| `MAX_RETRY_ATTEMPTS` | No | Integer from `0` to `20`; default `5`. This is the number of retries after the first attempt. |
| `LOG_LEVEL` | No | `Information`, `Warning`, or `Error`; default `Information`. |
| `HEALTH_PORT` | No | Integer from `1` to `65535`; default `8080`. |

## Start and verify

From the protected deployment checkout, run:

```powershell
docker compose up --detach --build
docker compose logs --follow
```

The first synchronization attempt starts immediately. During initialization, do not stop the container. If it is interrupted before the state file is created, the next start repeats the account replacement from the beginning.

Check the health response body:

```powershell
(Invoke-WebRequest http://localhost:8080/health).Content
```

The endpoint always returns HTTP `200` with one of these values:

- `starting` — no synchronization attempt has completed yet;
- `healthy` — the latest completed cycle succeeded;
- `degraded` — the latest attempt failed; inspect the logs.

When the service becomes `healthy`, confirm that `.raindrop-sync/state.json` was committed and that `bookmarks.xbel` still contains the expected tree. Re-enable Floccus only after those checks.

## Normal operation

The next cycle starts `SYNC_INTERVAL` after the previous cycle finishes, so cycles never overlap. Temporary API, network, and Git failures are retried after `RETRY_INTERVAL`. After the retry limit is exhausted, the service stays running and waits for the next scheduled cycle.

Changes made only in Raindrop are written to XBEL and Git. Changes made only in XBEL are applied to Raindrop. Git wins conflicting changes and change-versus-delete conflicts. Root-level XBEL bookmarks map to Raindrop `Unsorted`; Trash is excluded from synchronization.

Before applying changes, the service fetches the latest `main`. A concurrent Floccus commit causes the service to reload the branch and recalculate instead of overwriting remote history.

## Stop, update, and recover

Stop the service cleanly:

```powershell
docker compose down
```

The `repository-work` volume contains the Git clone and the journal used to recover interrupted writes. Preserve this volume during normal stops and upgrades. Rebuild and restart with the same configuration and volume:

```powershell
docker compose up --detach --build
```

If health is `degraded`, preserve the logs and volume before investigating. Do not delete the working volume, `.raindrop-sync/state.json`, or `.git/raindrop-sync-pending.json` as a shortcut. A damaged state or journal requires manual inspection; deleting recovery data can trigger destructive initialization or duplicate an operation whose result is unknown.

See [architecture.md](architecture.md) for synchronization, conflict, state, and recovery contracts.
