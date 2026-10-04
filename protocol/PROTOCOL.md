# Tracker upload protocol

The trackers copy PokerStars hand-history files to pokerland-api, byte for byte.
They never parse hands: the server does that, once, in Python. This document is
the contract both trackers (C# on Windows, Go on macOS) implement and that
`scenarios/` tests.

## Terms

- **Root**: a directory the tracker watches recursively, e.g.
  `~/Library/Application Support/PokerStars/HandHistory`. Every `*.txt` under a
  root is a hand-history file.
- **Stream**: one hand-history file, as the server sees it. A stream is a
  growing sequence of bytes; the tracker uploads it in order, in chunks.
- **Fingerprint**: `sha256` (lowercase hex) of the file's first line, including
  its `\n` and any UTF-8 BOM. PokerStars starts every file with a unique
  `PokerStars Hand #<id>: ...` line, so the fingerprint identifies the file even
  if it is renamed or moved. A file with no `\n` in its first 4096 bytes has no
  fingerprint yet and is skipped until it does.
- **Stream id**: UUID v5 of the fingerprint in the namespace
  `6f1e7c1e-5a0b-4d3e-9b1a-2f3c4d5e6f70`. It is deterministic, so a tracker
  that lost its local state asks the server where it left off instead of
  re-uploading. The server scopes stream ids per user.
- **Offset**: a byte position in the file. `acked_offset` is the number of
  bytes the server has stored for a stream.

## Requests

Every request carries:

```
Authorization: Token <client token from the Settings page>
User-Agent: pokerland-tracker/<semver> (<windows|macos|linux>; <arch>)
```

Responses the tracker must handle on any request:

| Status | Meaning | Tracker does |
|---|---|---|
| 401 | token invalid | show "token invalid", pause uploads for 5 minutes, then try again (the user may have fixed the token) |
| 426 | tracker version below `min_version` | show "update required", pause uploads for an hour |
| 429, 5xx, network error | server busy or unreachable | back off: 1 s doubling to 5 min, with jitter, forever |

### `GET /api/tracker/me`

Checks the token. Response: `{"username": "alice"}`.

### `GET /api/tracker/config`

Tunables, fetched at startup and every 6 hours. Defaults in parentheses are
what a tracker uses before its first successful fetch.

```json
{
  "min_version": "0.1.0",
  "poll_interval_seconds": 2,
  "flush_interval_seconds": 10,
  "flush_bytes": 262144,
  "max_read_bytes": 1048576,
  "max_chunk_bytes": 4194304
}
```

- `poll_interval_seconds` (2): how often to look at the files.
- `flush_interval_seconds` (10): a file is uploaded at most once per interval…
- `flush_bytes` (256 KiB): …unless at least this many new bytes are waiting.
- `max_read_bytes` (1 MiB): the most uncompressed bytes in one chunk.
- `max_chunk_bytes` (4 MiB): the most *compressed* bytes in one request; the
  server answers 413 above this.

### `PUT /api/tracker/streams/{stream_id}`

Registers a stream, or asks where an existing one left off.

```json
{
  "source": "pokerstars",
  "platform": "macos",
  "client_version": "0.1.0",
  "path_hint": "BungusChungus42/HH20260521 Gertrud VIII - 100-200 - Play Money No Limit Hold'em.txt",
  "fingerprint": "<64 hex>"
}
```

`path_hint` is the path relative to the root. Response, 201 when created and
200 otherwise: `{"stream_id": "...", "acked_offset": 0}`. A 409 means the id
is already registered with a different fingerprint, which is a tracker bug.

### `PUT /api/tracker/streams/{stream_id}/chunks/{start}`

Uploads bytes `[start, end)` of the file. `start` must equal the stream's
`acked_offset`.

```
Content-Type: application/gzip
X-Chunk-End: <end offset>
X-Chunk-Sha256: <sha256 hex of the uncompressed bytes>
```

The body is the gzipped bytes. The uncompressed bytes must end with `\n`: the
tracker cuts every chunk at the last newline and sends the rest later, so the
server never receives half a line.

| Status | Body | Tracker does |
|---|---|---|
| 202 | `{"acked_offset": end}` | save `end` as the file's acked offset |
| 200 | `{"acked_offset": ...}` | a retry of a chunk already stored (same start and hash): same as 202 |
| 409 | `{"acked_offset": N, "detail": ...}` | set the acked offset to `N` and continue from there |
| 400 | `{"detail": ...}` | hash, length or newline check failed: re-read the file and retry |
| 413 | `{"detail": ...}` | halve the chunk size and retry |
| 404 | | stream unknown: register it again, then retry |

## The tracker loop

Every `poll_interval_seconds`, for each `*.txt` file under each root:

1. **Identify it.** If the file has no state yet, or its size is smaller than
   its acked offset, or its first line no longer matches its fingerprint,
   fingerprint it, derive the stream id and (re)register it with
   `PUT /streams/{id}`. The response's `acked_offset` is where to start, which
   may be past zero when the tracker was reinstalled. If it is past the end of
   the file (the file was truncated and rewritten with the same first line),
   nothing is uploaded until the file grows past it again.
2. **Decide whether an upload is due.** It is when the file has never been
   uploaded, when `size - acked_offset >= flush_bytes`, or when there are new
   bytes and `flush_interval_seconds` have passed since the file's last upload.
3. **Read** from `acked_offset`, at most `max_read_bytes`, and cut at the last
   `\n`. If no complete line was read, wait for the next poll.
4. **Upload** the chunk and act on the response as in the table above. After a
   2xx, persist the new offset before anything else, then go back to step 3
   until the file has no complete unsent bytes: once an upload is due, the
   whole backlog goes up in that poll.
5. **Stop on fatal replies.** 401 and 426 pause all uploads for every file; the
   tracker keeps polling the files but sends nothing until the pause ends.

Rules:

- Open files for reading only, sharing write and delete access with the game.
  Never hold a file open between polls.
- Never decode the bytes. Encoding and line endings are the server's problem.
- Nothing is buffered in memory across polls: the file is the queue. After a
  crash the tracker re-reads from the last persisted acked offset.
- State is written atomically (write a temp file, then rename) after every
  change to an acked offset.
- Deleted files are forgotten after 30 days without being seen, so state does
  not grow forever.

## Local state

`state.json`:

```json
{
  "version": 1,
  "files": {
    "/abs/path/to/file.txt": {
      "stream_id": "uuid",
      "fingerprint": "hex",
      "acked_offset": 12345,
      "registered": true,
      "last_upload_at": "2026-10-03T12:00:00Z",
      "last_seen_at": "2026-10-03T12:00:00Z"
    }
  }
}
```

## Default roots

| OS | Roots (globbed, so regional builds like `PokerStars.EU` are included) |
|---|---|
| Windows | `%LOCALAPPDATA%\PokerStars*\HandHistory`, `%APPDATA%\PokerStars*\HandHistory` |
| macOS | `~/Library/Application Support/PokerStars*/HandHistory` |

Users can add roots in the tracker's settings.

## Scenarios

`scenarios/*.json` are shared conformance tests. Each is a script of file
operations and server behaviours, run by both trackers against an in-process
fake server. After the script, for every file, the fake server's reassembled
bytes for the file's current stream must equal the file's bytes up to and
including its last `\n`, and chunks must be contiguous with no gaps or
duplicates. See `scenarios/README.md` for the step format.
