# Vendored upstream code

Some upstream code lives in this repository as source rather than as a package,
because the upstream is not published to NuGet at a usable version, or because
we need to modify it. Vendoring is a decision to take on maintenance, so this
file records what came from where and what we changed.

**Every vendored tree records the exact upstream commit it was taken from.**
That is the whole point of this file: without a baseline, "pull upstream fixes
by hand" is not a plan, because nobody can tell what upstream has changed since.
`TidalSharp` predates this file and shows the cost — its baseline is lost, so
its drift is unmeasurable.

To see what a vendored tree has missed:

```sh
git clone <upstream> /tmp/upstream && cd /tmp/upstream
git log --oneline <vendored_commit>..HEAD
```

`.github/workflows/vendor-drift.yml` does this weekly and opens an issue when an
upstream has moved. It reads the JSON block below, so keep that block correct —
it is the source of truth, not the prose.

<!-- vendor-drift: machine-readable source of truth. Keep in sync when re-vendoring. -->
```json
[
  {
    "name": "QobuzApiSharp",
    "path": "src/Sleezer/QobuzApiSharp/",
    "upstream": "https://github.com/DaveBinM/QobuzApiSharp",
    "commit": "e9589d7fce247d13e0d8e1f7a6b53aeca85d7adb",
    "vendored": "2026-08-22",
    "track": true
  },
  {
    "name": "Lidarr.Plugin.Qobuz",
    "path": "src/Sleezer/Indexers/Qobuz/, src/Sleezer/Download/Clients/Qobuz/, src/Sleezer/ImportLists/Qobuz/",
    "upstream": "https://github.com/DaveBinM/Lidarr.Plugin.Qobuz",
    "commit": "a3bd3139aa4306d59451dd0c474df92b06b5ab2e",
    "vendored": "2026-08-22",
    "track": true
  },
  {
    "name": "lidarr-plugin-bandcamp",
    "path": "src/Sleezer/Indexers/Bandcamp/, src/Sleezer/Download/Clients/Bandcamp/, src/Sleezer/Http/Bandcamp/",
    "upstream": "https://github.com/jtstothard/lidarr-plugin-bandcamp",
    "commit": "e146da55de4375e94a0c0fc9b73c5f0d4a0132ab",
    "vendored": "2026-08-04",
    "adopted": "2026-08-23",
    "track": false
  },
  {
    "name": "TidalSharp",
    "path": "src/Sleezer/TidalSharp/",
    "upstream": "https://github.com/TrevTV/Lidarr.Plugin.Tidal",
    "commit": null,
    "vendored": null,
    "track": false
  }
]
```

## What is modified, and how much upstream is worth pulling

How useful an upstream fix is depends entirely on how far we diverged. These are
not equally trackable and should not be treated as if they were.

### QobuzApiSharp — tracked, with a patch series

**Still worth pulling from.** Its highest-value part is the `bundle.js` scrape,
which breaks when *Qobuz* changes rather than when upstream does — and upstream
is the likelier place a fix appears first. That sync path earns its keep, so
this tree stays diffable.

Local changes, which GPL-3.0 §5(a) requires be stated:

- A two-line `#nullable disable` banner on every file, plus `#pragma warning
  disable CS0672, SYSLIB0051` on the three exception types.
- `QobuzApiService.User.cs`: the login failure messages no longer quote the
  user's auth token or password hash back into the exception, which was putting
  the credential into any log that recorded it. The three login responses are
  also disposed.
- `QobuzApiHelper.cs`: `DescribeRequest` replaces `HttpRequestMessage.ToString()`
  on every error path — the full render includes the `X-User-Auth-Token` header,
  so the token was stored on every API exception. The `app_secret` derivation
  also reports a changed bundle format instead of throwing
  `ArgumentOutOfRangeException` from a blind substring.
- `QobuzApiService.Artist.cs`, `.Favorite.cs`, `.User.cs`: eight parameter keys
  had a trailing space (`"type "`, `"user_id "`, `"order "`, …). `ToQueryString`
  escapes the key, so they were sent as `type%20=` and silently ignored — which
  meant the favourites import lists never applied their type filter.
- `QobuzApiService.cs`: `IsAppSecretValid` treated any exception as an invalid
  secret, so a network blip forced a needless re-scrape and re-authentication.
  Only a rejected request counts now.
- `MostPopularContentConverter.cs`: rejects a missing or unsupported `type`
  rather than dereferencing null or returning null.

Re-vendoring means re-applying that list, not discarding it.

The file that matters is `Service/QobuzApiHelper.cs`: it scrapes the Qobuz web
player's `bundle.js` for the `app_id` and derives the `app_secret` from an
embedded seed. Qobuz can change that page at any time and the scrape is the
first thing to break. `.github/workflows/qobuz-bundle-canary.yml` checks it
weekly against the live player, because that failure has no upstream signal —
nothing changes in this repo or theirs when Qobuz ships a new bundle.

Only `Models/` is excluded from CodeRabbit review in `.coderabbit.yaml` — 47 files
of JSON-mapped properties with no logic. `Service/`, `Exceptions/`, `Converters/`
and `Utilities/` are reviewed, because that is where the logic lives and where
the patch series above applies. Excluding a file we patch would mean the change
least likely to be safe is the one nobody looks at.

### Lidarr.Plugin.Qobuz — heavily rewritten

**Read upstream for ideas, do not diff it.** The port keeps the donor's shape
(indexer / parser / request generator / download client / queue) but the
internals diverged enough that a patch will rarely apply:

- Queue rebuilt on this plugin's Tidal queue; the donor's mutated its item and
  cancellation collections from several threads with no lock.
- Post-processing routed through `PostProcessRunner` (corrupt scan, pre-import
  tagging) — the donor has no equivalent.
- `IsFullPage` counts distinct albums, not releases, because the parser expands
  one album into up to four quality variants.
- Release type read from the search payload where present; the donor slept
  300–800 ms per album on the search thread.
- `CompletedDownloadHandler` dropped (Lidarr owns completed-download handling);
  `SixLabors.ImageSharp` dropped in favour of the SkiaSharp helper already here.
- Import lists fixed: the favourite-albums list only set `Artist`, and all three
  could loop forever on an empty page.
- Sleezer logging conventions and nullable annotations throughout.

### lidarr-plugin-bandcamp — adopted, no longer tracked

**This code is ours now.** Fix it in place; do not defer a defect on the grounds
that it came from upstream.

Originally imported from `e146da5` and initially treated as near-verbatim. That
stopped being true: review surfaced enough real defects — an SSRF on the
credentialed download path, a whole-archive buffer, substring purchase matching,
process-randomised release GUIDs, swallowed collection failures, a dead JSON
parse path, an `IHttpDispatcher` that would have competed for every HTTP call
Lidarr makes — that roughly 450 lines across nine files have changed, three of
them by 15-37%, with five files deleted and one added. A patch of that size is
not a vendored copy, and calling it one only served as a reason not to fix
things properly.

MIT permits this without restriction; the licence text is reproduced in NOTICE
and jtstothard keeps authorship credit for the original work. Upstream remains
worth reading, but nothing here is written to stay diffable against it.

### TidalSharp — baseline lost

**Not tracked.** Vendored before this file existed, with no record of the commit
it came from, so upstream drift cannot be measured. It also carries substantial
local work — device-code OAuth, token storage in Lidarr's settings DB,
`LosslessGuard`, `ExpiredTokenDetector`, the tier-locked quality fallback — so a
re-baseline would be a deliberate project rather than a lookup. Left honest
rather than guessed at.

## Re-vendoring

1. Clone upstream at the commit you want, copy the tree in.
2. Re-apply the local modifications listed above for that tree.
3. Update `commit` and `vendored` in the JSON block.
4. Build and run the tests; for QobuzApiSharp, also run the bundle canary
   workflow manually before trusting it.
