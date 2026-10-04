# Conformance scenarios

Each `*.json` file is a script that both trackers run in their test suites,
against an in-process fake of the server described in `../PROTOCOL.md`. The
fake speaks real HTTP, so the trackers' API clients are tested too.

The test harness creates a temporary root directory and a fake clock. Time only
moves when a step says so, so flush intervals and back-off are deterministic.

```json
{
  "name": "what the scenario proves",
  "config": { "flush_bytes": 300, "max_read_bytes": 300 },
  "steps": [ ... ],
  "expect": { ... }
}
```

`config` overrides the config the fake server hands out (keys as in
`GET /api/tracker/config`). Defaults: poll 2 s, flush 10 s, flush_bytes 256 KiB,
max_read_bytes 1 MiB, max_chunk_bytes 4 MiB.

## Steps

| Step | Effect |
|---|---|
| `{"write": {"file": "a/b.txt", "text": "..."}}` | Append `text` (UTF-8) to the file, creating it and its directories. |
| `{"truncate": {"file": "b.txt", "size": 0}}` | Truncate the file to `size` bytes. |
| `{"delete": {"file": "b.txt"}}` | Delete the file. |
| `{"tick": 3}` | Run three polls. The clock advances by `poll_interval_seconds` after each one. |
| `{"advance": 10}` | Move the clock forward 10 seconds. |
| `{"restart": true}` | Discard the follower and build a new one from the state file, as after a reboot. |
| `{"lose_state": true}` | Delete the state file, then restart, as after a reinstall. |
| `{"server": {"fail": 2}}` | The next two requests get a 503. |
| `{"server": {"status": 426, "times": 1}}` | The next request gets this status. |
| `{"server": {"rewind": {"file": "b.txt", "acked_offset": 0}}}` | The server forgets everything past the offset for the file's current stream, so the tracker's next upload gets a 409. |

Paths are relative to the root and use `/`.

## Expectations

After the script, for every file under the root that has at least one complete
line, the fake server must hold exactly one stream whose fingerprint matches
the file's current first line, and the bytes of that stream's chunks,
concatenated, must equal the file's bytes up to and including its last `\n`.
Files with no newline must have no stream. This is checked for every scenario.

Optional extra checks:

| Key | Meaning |
|---|---|
| `"chunks": {"b.txt": 2}` | The file's current stream was uploaded in exactly this many chunks. |
| `"streams": {"b.txt": 2}` | This many streams were registered with the file's path. |
| `"requests_at_most": 5` | The fake server saw at most this many requests in total. |
