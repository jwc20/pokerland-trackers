# pokerland-trackers

Desktop trackers that upload PokerStars hand histories to
[pokerland-api](https://github.com/jwc20/pokerland-api). They copy bytes; the
server parses hands. Both trackers implement
[`protocol/PROTOCOL.md`](protocol/PROTOCOL.md) and pass the same conformance
scenarios in [`protocol/scenarios/`](protocol/scenarios/).

| | Windows | macOS |
|---|---|---|
| Language | C# (.NET 10, WinForms) | Go |
| Install | `PokerlandTracker-win-Setup.exe` from the [latest release](https://github.com/jwc20/pokerland-trackers/releases/latest) | `brew install jwc20/tap/pokerland-tracker` |
| Runs as | tray icon, starts with Windows | `brew services` (launchd), no window |
| Updates | itself, via Velopack | `brew upgrade pokerland-tracker` |
| Token | DPAPI-encrypted `%LocalAppData%\Pokerland\settings.json` | `~/Library/Application Support/Pokerland/config.json` (mode 600) |
| Logs | `%LocalAppData%\Pokerland\logs\` | `$(brew --prefix)/var/log/pokerland-tracker.log` |

## Using the macOS tracker

```bash
brew install jwc20/tap/pokerland-tracker
pokerland-tracker login             # paste the client token from the web app's Settings page
brew services start pokerland-tracker
pokerland-tracker status            # what is tracked, last upload
pokerland-tracker doctor            # when something looks wrong
```

Other commands: `once` (upload and exit), `logout`, `version`. `login --api URL`
points the tracker at another server, e.g. `http://localhost:8000` for a local
pokerland-api.

## Using the Windows tracker

Run `PokerlandTracker-win-Setup.exe`. It installs per user (no admin prompt),
opens its settings window and asks for the client token. Afterwards it lives in
the system tray: double-click for settings and a live log, right-click to
pause, check for updates or quit.

## Development

```bash
# macOS tracker
make -C mac test
make -C mac build                    # -> mac/pokerland-tracker, talks to the local API

# Windows tracker (the Core library and its tests build and run on macOS/Linux too)
cd windows && dotnet test Pokerland.Tracker.Tests

# Try it against a local pokerland-api
POKERLAND_TRACKER_HOME=/tmp/pl mac/pokerland-tracker login
POKERLAND_TRACKER_HOME=/tmp/pl mac/pokerland-tracker once --root /path/to/HandHistory
```

`POKERLAND_TRACKER_HOME` relocates the config, state and status files, so a
test setup never touches the real one.

### Local and production builds

The server a tracker talks to by default is set at build time by its
environment:

| Environment | URL | Used by |
|---|---|---|
| `local` | `http://127.0.0.1:8000` | `make -C mac build`, Debug builds of the Windows tracker |
| `production` | `https://api.pokerland.app` | `make -C mac build ENV=production`, the Homebrew formula, the release workflow, Release builds of the Windows tracker (`build.ps1`) |

The production URL lives in code (`mac/internal/config/config.go`,
`windows/Pokerland.Tracker.App/AppSettings.cs`), so a plain `go build` gets it
too. Switch environment per build with `make -C mac build ENV=production` or
`dotnet build -c Release -p:PokerlandEnv=local`.

To point an environment at another server, set `POKERLAND_API_BASE_URL` in an
untracked `.env.local` or `.env.production` at the repo root (shared by both
trackers), or pass `POKERLAND_API_BASE_URL=...` to `make` or
`-p:PokerlandApiBaseUrl=...` to `dotnet`. A URL saved by
`pokerland-tracker login --api` (or in the Windows settings window) takes
precedence over the built-in default.

`make build` stamps the version from the nearest git tag (e.g.
`0.1.1-3-gabc1234`). A build without a version reports `0.0.0-dev`, which
servers refuse with 426 unless their `TRACKER_MIN_CLIENT_VERSION` is `0.0.0`.

### Layout

```
protocol/            PROTOCOL.md and the shared scenarios both trackers must pass
mac/                 Go module: cmd/pokerland-tracker, internal/{api,follower,state,config,platform}
windows/             .NET solution: Core (loop, no UI), App (tray), Tests (xunit, runs the scenarios)
homebrew/            formula template the release workflow pushes to jwc20/homebrew-tap
.github/workflows/   ci.yml (tests on push), release.yml (installer, binaries, formula on a v* tag)
```

### Releasing

1. Push a tag: `git tag v0.1.0 && git push --tags`.
2. `release.yml` builds the Windows installer with Velopack and attaches it and
   the macOS binaries to the GitHub release. Installed Windows trackers pick up
   the update within six hours.
3. With a `TAP_GITHUB_TOKEN` secret (push access to `jwc20/homebrew-tap`), the
   workflow also updates the Homebrew formula. Create that repo once, empty;
   the workflow adds `Formula/pokerland-tracker.rb`.
4. To require the new version, raise `TRACKER_MIN_CLIENT_VERSION` in the API's
   environment: older trackers then get 426, show "update required", and the
   Windows tracker updates itself at once.

Code signing for Windows is optional but removes the SmartScreen warning: put
the `signtool` arguments in the `VPK_SIGN_PARAMS` secret (see `windows/build.ps1`).
