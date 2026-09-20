# Architecture

## Overview

Raindrop.io ↔ Floccus Sync is a .NET 10 worker hosted in a Docker container. It synchronizes a Raindrop account with the root-level `bookmarks.xbel` file on the `main` branch of a Gitea repository.

```text
Raindrop API ↔ .NET worker ↔ Git over SSH ↔ bookmarks.xbel ↔ Floccus
```

The worker owns the scheduling loop; no cron process is used. Git is accessed through the installed command-line client over SSH, and Raindrop is accessed with a personal API token.

The repository contains two synchronization files:

- `/bookmarks.xbel` — the Floccus bookmark tree;
- `/.raindrop-sync/state.json` — the last fully reconciled state and identity mappings.

A local write-ahead journal is stored at `.git/raindrop-sync-pending.json` in the persistent working volume. It is never committed.

## Components

- `Program.cs` configures dependency injection, the worker, and the health endpoint.
- `Clients/` contains the Raindrop HTTP client and Gitea SSH/Git adapter.
- `Helpers/` contains configuration, logging, command execution, XBEL/state serialization, repository storage, SSH setup, and journal persistence.
- `Models/` contains immutable synchronization, API, XBEL, and recovery contracts.
- `Services/` contains initialization, comparison, planning, application, scheduling, and health state.
- `Interfaces/` defines the boundaries between these components.

## Synchronization scope

The complete XBEL tree is synchronized. Supported data is limited to information represented by the Floccus XBEL format:

- folder hierarchy and titles;
- bookmark titles and URLs.

Item order is not significant. Raindrop tags, notes, descriptions, favourites, covers, and other Raindrop-only metadata are not synchronized. Updates send only synchronized fields so unrelated metadata is preserved.

Root-level XBEL bookmarks map to the Raindrop `Unsorted` collection. Raindrop Trash is excluded from reads and writes except when a synchronized deletion moves a bookmark to Trash. A bookmark manually restored from Trash appears as a new active bookmark.

## Identity and duplicates

The service never deduplicates by title or URL. Duplicate URLs, duplicate folder names, and identical sibling folders remain independent entities.

Each logical folder and bookmark has a stable UUID. The state file maps that UUID to an XBEL numeric ID and a Raindrop numeric ID. Folder and bookmark UUIDs are distinct types. New Raindrop items receive unused XBEL IDs above both the current Floccus `highestId` value and the IDs recorded in state.

An item known to the previous state but missing from a current source is a deletion. An unknown source-specific ID is a new item. Titles and URLs are never used as identity keys.

## Initial synchronization

Only the actual absence of `.raindrop-sync/state.json` starts initialization. An unreadable or invalid state file is an error and is never treated as a first run.

Git is the sole source of truth during initialization:

1. Fetch `main` and strictly validate `bookmarks.xbel`.
2. Assign stable IDs and validate Raindrop field limits before making changes.
3. Move every active Raindrop bookmark to Trash.
4. Delete all user-created collections from children to parents.
5. Confirm that the active Raindrop area is empty. Existing Trash is not read or cleared.
6. Create collections from parents to children and create bookmarks individually.
7. Read Raindrop again and verify the complete imported tree.
8. Fetch Git again and confirm that the source revision did not change.
9. Atomically write generation `1` state, commit it, and push it to `main`.

Bookmarks are created individually because the Raindrop batch response does not provide a reliable positional identity mapping and duplicates are valid.

If initialization stops before valid state is written, the next run repeats the complete replacement. If state was written but not committed or pushed, the next run validates it and finishes the Git operation without clearing Raindrop again.

## Reconciliation

Every regular cycle compares three views:

- the last reconciled XBEL and Raindrop snapshots from state;
- the current XBEL tree;
- the current active Raindrop tree.

Each side is compared only with its own prior snapshot. This prevents values normalized by Raindrop from being mistaken for later user edits.

The comparer classifies every entity as unchanged, added, modified, or deleted. The planner then selects the target version:

- a change on one side is propagated to the other side;
- compatible independent changes are combined;
- when both sides changed the same entity differently, Git wins;
- when one side changed an entity and Git deleted it, the Git deletion wins;
- when Raindrop deleted an entity that Git changed, the Git version is restored.

The rule applies to the whole entity. Field-level merges are not attempted.

The planner also resolves structural dependencies. Required Git ancestor folders are restored when Raindrop deleted them. New Raindrop items inside a subtree deleted by Git are removed with that subtree. Moves that would create a cycle or exceed the maximum folder depth of 128 are resolved in favour of the Git structure; a new Raindrop subtree that is otherwise valid may be promoted to the root to stay within the limit.

Planning and validation finish before the first write. If the logical XBEL contents do not change, the original bytes, formatting, and order are retained. When contents change, unchanged siblings retain their source order and the regenerated document uses Floccus-compatible formatting so Git diffs remain focused on the affected items.

## Applying a plan

Raindrop changes are ordered to preserve a valid tree:

1. detach retained collections whose parent will change;
2. detach obsolete nested collections when needed;
3. create and update collections from parents to children;
4. create and update bookmarks;
5. move deleted bookmarks to Trash in batches of at most 100 IDs;
6. delete empty obsolete collections from children to parents;
7. reread Raindrop and verify the target tree;
8. atomically replace XBEL and state, commit both files, and push.

The state generation advances only after both sides have been verified. A cycle with no changes does not rewrite XBEL or state. A generation may still advance without API writes when new mappings or equal changes from both sides must be recorded.

## State contract

`.raindrop-sync/state.json` is a strict, versioned JSON document. Schema version `1` contains:

```json
{
  "schemaVersion": 1,
  "generation": 1,
  "initializationCompleted": true,
  "xbelSnapshot": {
    "folders": [],
    "bookmarks": []
  },
  "raindropSnapshot": {
    "folders": [],
    "bookmarks": []
  },
  "folderMappings": [],
  "bookmarkMappings": []
}
```

`generation` is positive and increases monotonically. Version `1` state always has `initializationCompleted: true`; incomplete initialization has no state document.

The two snapshots describe the same logical entities but may contain different titles or URLs when Raindrop normalized a value. Mapping arrays are one-to-one across stable, XBEL, and Raindrop IDs. The XBEL root has no stable folder ID and maps implicitly to `Unsorted`.

The serializer rejects unknown or duplicate JSON properties, invalid types, empty UUIDs, duplicate mappings, invalid source IDs, missing parents, cycles, unknown schema versions, and trees deeper than 128 folders. Output arrays are sorted by stable ID for deterministic serialization.

## Journal and crash recovery

The local journal is a strict versioned document protected by a SHA-256 checksum and written with a temporary file, disk flush, and atomic rename. It records the base Git revision and files, the complete plan, the expected Raindrop snapshot, confirmed IDs, any in-flight operation, and the prepared final state.

Before every Raindrop write, the service records its intent. After a confirmed response, it records the returned ID and actual stored value. A restart can continue a confirmed plan without creating the same item again when the source Git revision and Raindrop snapshot are unchanged.

If the process stops after sending a request but before recording the response, the outcome is unknown. The request is not replayed automatically because Raindrop creation has no idempotency key and matching by title or URL is unsafe. The service raises a recovery-required error and preserves the journal for manual investigation.

The final XBEL and state contents are added to the journal before either repository file is replaced. A restart between file replacement, commit, and push can therefore complete the pair without repeating API writes. The journal is removed only after a successful push.

## Git concurrency

Each cycle begins with a fetch and fast-forward of `main`. Git is refreshed again before Raindrop writes and before finalization. If Floccus commits concurrently, the worker rereads the branch and builds a new plan against the already confirmed Raindrop state.

The final push uses an exact lease for the base revision. A rejected lease cannot overwrite a concurrent commit. The rejected local synchronization commit is preserved under `refs/raindrop-sync/rejected/`, the working branch returns to `origin/main`, and reconciliation starts again.

Only `bookmarks.xbel` and `.raindrop-sync/state.json` may be included in synchronization commits. Unknown local changes, an unexpected commit, a damaged journal, or an unresolved API write stops automatic application rather than discarding evidence.

## Raindrop client behaviour

Collections and nested collections are read separately. Active bookmarks, including `Unsorted`, are read in pages of 50; Trash is excluded. Bookmark creation batches are limited to 100 by the API contract, although initialization and reconciliation create bookmarks individually to keep identity mapping unambiguous.

HTTP redirects are disabled and response bodies are capped at 16 MiB. Authentication comes only from configuration. Errors do not include credentials, response bodies, or raw transport exceptions.

Rate-limit responses are delayed until the advertised reset. A rejected `429` write can be retried because the server confirms it was not applied. Other writes are not automatically replayed. Reads observe cancellation immediately; an in-flight write is allowed to reach a response or timeout before cancellation prevents the next operation.

## Scheduling, failures, and shutdown

The first cycle starts immediately. Later cycles start `SYNC_INTERVAL` after the preceding cycle finishes and therefore never overlap.

Retryable Raindrop, Git, network, timeout, server, and unexpected failures are retried up to `MAX_RETRY_ATTEMPTS` after `RETRY_INTERVAL`. A longer server-provided rate-limit delay takes precedence. After retries are exhausted, the worker remains alive and waits for the next scheduled cycle.

Non-transient Raindrop failures and recovery-required conditions wait for the next scheduled cycle without immediate retries. Missing or invalid XBEL, invalid state, and Raindrop authentication failures are fatal. Invalid configuration prevents startup. A fatal synchronization failure is logged, marks health degraded, and stops the worker.

Shutdown cancels pending waits and prevents new operations. An already started bounded write and its journal update are allowed to reach a safe boundary.

## Health, logging, and secrets

`GET /health` always returns HTTP `200` with `starting`, `healthy`, or `degraded`. A new cycle retains the result of the previous completed cycle. Cancellation is not reported as a failure.

Logs are single-line human-readable messages with UTC timestamps and `Information`, `Warning`, or `Error` levels. They include cycle results, change counts, retries, and fatal reasons. Tokens, private keys, raw Git standard error, and other credentials are never logged.
