# Sleezer for Lidarr 🎶

![License](https://img.shields.io/github/license/chodeus/sleezer) ![GitHub release (latest by date)](https://img.shields.io/github/v/release/chodeus/sleezer) ![GitHub last commit](https://img.shields.io/github/last-commit/chodeus/sleezer) ![GitHub stars](https://img.shields.io/github/stars/chodeus/sleezer)

Sleezer is a Lidarr plugin that adds **Deezer**, **Tidal**, **Qobuz**, **Slskd (Soulseek)**, and a handful of other music sources behind a single install. It also ships post-processing: corrupt-file scanning and pre-import tagging for Deezer/Tidal/Qobuz/Slskd downloads, plus an FFmpeg-based format converter that runs on every imported track regardless of source. 🛠️

Credit where it's due: Sleezer is built on [Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer) by [TrevTV](https://github.com/TrevTV) and [Tubifarry](https://github.com/TypNull/Tubifarry) by [TypNull](https://github.com/TypNull). See [Credits](#credits-).

---

## Table of Contents 📑

1. [Installation 🚀](#installation-)
2. [Deezer Setup 🎧](#deezer-setup-)
3. [Tidal Setup 🌊](#tidal-setup-)
4. [Qobuz Setup 🎼](#qobuz-setup-)
5. [Soulseek (Slskd) Setup 🐟](#soulseek-slskd-setup-)
6. [Web Clients 📻](#web-clients-)
7. [FFmpeg 🎛️](#ffmpeg-️)
8. [Corrupt File Scan & Pre-Import Tagging 🧼](#corrupt-file-scan--pre-import-tagging-)
9. [Queue Cleaner 🧹](#queue-cleaner-)
10. [Search Sniper 🏹](#search-sniper-)
11. [Custom Metadata Sources 🧩](#custom-metadata-sources-)
12. [Similar Artists 🧷](#similar-artists-)
13. [Troubleshooting 🛠️](#troubleshooting-)
14. [Credits 🙌](#credits-)
15. [Contributing 🤝](#contributing-)
16. [License 📄](#license-)

---

## Installation 🚀

1. In Lidarr, go to `System -> Plugins`.
2. Paste `https://github.com/chodeus/sleezer` into the GitHub URL box and click **Install**.
3. Restart Lidarr when prompted.

---

### Deezer Setup 🎧

Sleezer talks to Deezer directly (no Deemix middleman) using the `DeezNET` library.

> ⚠️ Deezer actively moves against downloading tools. Sleezer does its best, but there is no guarantee you won't be rate-limited or have an ARL banned.

#### Setting Up the Deezer Indexer

1. Go to `Settings -> Indexers` and click **Add**.
2. In the modal, select `Deezer` (under **Other** at the bottom).
3. Paste your personal ARL into the box. If you leave it blank the plugin will pick a public ARL automatically — this works but is less reliable.
4. Press **Save**. The first save performs a handful of auth calls and can take a few seconds.

#### Setting Up the Deezer Download Client

1. Go to `Settings -> Download Clients` and click **Add**.
2. Select `Deezer` from the list.
3. Set the download path and the audio quality you want.
4. **Profiles → Delay Profiles**: click the wrench on the default profile and tick **Deezer** so Lidarr is allowed to grab releases from it.

#### ARL tips

* If your downloads suddenly start failing, rotate the ARL before anything else. Most "Deezer broke" reports are single-ARL bans.
* Leaving the ARL field blank uses Sleezer's public-ARL rotation — works but slower and occasionally stale.

---

### Tidal Setup 🌊

Sleezer talks to Tidal directly using a vendored fork of TrevTV's `TidalSharp` library. Auth is one-click thanks to Tidal's device-code OAuth flow.

> ⚠️ A Tidal HiFi or HiFi Plus subscription is required to download lossless / hi-res content. Sleezer will not bypass entitlement checks.

#### Setting Up the Tidal Indexer

1. In Lidarr, go to `Settings -> Indexers` and click `+` to add a new indexer.
2. In the modal, select `Tidal` (under **Other** at the bottom).
3. Click **Authenticate with Tidal**. A small popup opens with a *"Open Tidal →"* button.
4. Click *"Open Tidal →"* — your Tidal verification page opens in a separate tab. Log in / grant access until Tidal says **"Device linked"**.
5. Come back to the popup window and click *"I've Authorized"*. The popup closes automatically; the settings page populates the hidden token fields.
6. Click **Save**.

<details>
<summary>Why the extra "I've Authorized" click?</summary>

Tidal's device-code OAuth flow doesn't redirect back to Lidarr after you authorize — its "Device linked" page is the end of the road on Tidal's side. The intermediate popup acts as the bridge so Lidarr knows when you're done.
</details>

#### Setting Up the Tidal Download Client

1. `Settings -> Download Clients`, click `+` to add.
2. Select `Tidal` from the list.
3. Set the **Download Path** Lidarr should monitor.
4. Optional: enable **Extract FLAC From M4A** (Tidal ships FLAC inside an M4A container; this unwraps it) or **Re-encode AAC into MP3**. Both require FFmpeg on PATH.
5. **Profiles → Delay Profiles**: tick **Tidal** on the default profile so Lidarr will grab from it.

#### Notes & Troubleshooting

* The post-processing pipeline (corrupt-file scan + pre-import tagging) runs on Tidal downloads, just like Deezer and Slskd.
* If searches start returning errors that mention `countryCode parameter missing`, that's Tidal's confusing way of saying your session expired. Sleezer detects this and forces a refresh; if that fails, re-authenticate via the indexer settings.
* A Tidal download failing with `Tidal returned codec 'MP4A' ... despite a LOSSLESS request` is expected — the grab is failed deliberately so Lidarr re-picks another source instead of importing AAC into a Lossless bucket.
  <details>
  <summary>Why this happens / what to do</summary>

  Tidal silently downgrades to AAC for tracks not licensed lossless in your region — common in AU/NZ/SE-Asia for older or remix-heavy catalogue. Sleezer fails the grab so Lidarr can re-pick another source (e.g. a slskd FLAC peer). Re-authenticating via a US VPN sometimes unlocks more lossless catalogue if the album genuinely is licensed lossless somewhere.
  </details>
* Various Artists, Soundtracks, and Cast Recordings are recognised explicitly so they actually return search hits.
* Tidal music videos and Dolby Atmos tracks are not supported in this release.
* Tidal does not expose a public RSS / new-release feed, so RSS sync is disabled at the indexer level.

---

### Qobuz Setup 🎼

Sleezer talks to Qobuz directly using a vendored fork of `QobuzApiSharp`. Qobuz serves **native `.flac`** — there is no DASH/M4A container to unwrap and no FFmpeg step, which makes it the cleanest-provenance source in this plugin.

> ⚠️ A paid Qobuz subscription is required. Qobuz refuses even *search* without a valid user token, and Studio/Sublime tiers are what unlock 24-bit hi-res. Sleezer will not bypass entitlement checks.

#### Setting Up the Qobuz Indexer

1. **Settings → Indexers → Add**.
2. In the modal, select `Qobuz` (under **Other** at the bottom).
3. Fill in **User ID** and **User Auth Token**:
   * Open [play.qobuz.com](https://play.qobuz.com) and log in.
   * DevTools → **Network** tab → click any request → copy the `X-User-Auth-Token` request header.
   * DevTools → **Application** → Local Storage → the numeric `id` field is your User ID.
4. Leave **App ID** and **App Secret** blank. Sleezer reads the current pair from Qobuz's web player at runtime, so it survives Qobuz rotating them.
5. Save. The indexer test logs the storefront your account resolves to — see the region note below.

> Email + MD5 password is accepted as an alternative, but **it can only search**. Qobuz refuses `getFileUrl` on email/password sessions, so downloads fail. Use the token.

#### Setting Up the Qobuz Download Client

1. **Settings → Download Clients → Add**.
2. Select `Qobuz` from the list.
3. Set **Download Path** to a directory Lidarr can read.
4. **Profiles → Delay Profiles**: tick **QobuzDirect** on the default profile so Lidarr will grab from it.

#### Notes

* Each album is offered at up to four qualities — `MP3 320kbps`, `FLAC Lossless`, `FLAC 24bit 96kHz`, `FLAC 24bit 192kHz`. Your Lidarr quality profile picks.
* **Region.** Qobuz licenses per territory. An album missing from search is usually not licensed in your account's storefront rather than a bug — the indexer test logs which storefront that is. A failed grab says so explicitly.
* **Require Complete Album** (on by default) fails the whole album when any track can't be downloaded, so Lidarr retries or picks another release instead of importing a gap-toothed album.
* The post-processing pipeline (corrupt-file scan + pre-import tagging) runs on Qobuz downloads — enable **Qobuz** in the FFmpeg provider's client pickers.
* Qobuz supplies no lyrics; enable **Use LRCLIB as Lyric Provider** if you want them.
* Qobuz is *not* the same as the **DABMusic** web client, which speaks the Qobuz protocol against a third-party proxy. DABMusic keeps its own `Qobuz` delay-profile protocol row; this first-party client uses `QobuzDirect`.

---

### Soulseek (Slskd) Setup 🐟

Sleezer includes both the Slskd indexer and download client, so Lidarr can search Soulseek and grab results through your existing Slskd instance.

#### Setting Up the Slskd Indexer

1. Navigate to `Settings -> Indexers` and click **Add**.
2. Select `Slskd` from the list.
3. Configure:
   * **URL**: the URL of your Slskd instance (e.g. `http://localhost:5030`).
   * **API Key**: from Slskd's Options panel.
   * **Include Only Audio Files**: enable to filter search results.

#### Setting Up the Slskd Download Client

1. Go to `Settings -> Download Clients` and click **Add**.
2. Select `Slskd` from the list.
3. The download path is fetched from Slskd automatically; if it doesn't match the host view, use **Remote Path** mappings.

#### Matching & Retry Behaviour

A few Slskd behaviours worth knowing, all born from live-log audits of real-world failure modes:

* **Remix/variant qualifiers are hard boundaries.** A folder named `Song (Colyn Remix)` will never match a search for the plain `Song`, and vice versa — same for `rework`, `bootleg`, `VIP`, `edit`, `instrumental`, `acapella`, and `karaoke`. Two releases that each *name* a remixer only match when those names agree; a generic qualifier (`Remixes` with nobody named) matches any remix release. Deluxe/remastered editions are unaffected.
* **Recently-failed sources sit out automatic searches on an escalating clock.** When a download fails ("File not shared.", remote cancel), that release is excluded from automatic grabs — one hour after a first failure, six after a second, a full day from the third — so a transiently busy peer retries quickly while a dead share stops being hammered. Interactive search still shows everything — a manual re-grab is deliberate.
* **Failed grabs retry cleanly.** A re-grab after a failure tracks under a fresh download id, so a completed retry imports instead of being silently ignored by Lidarr's tracked-download cache (which permanently remembers the failed id until a restart).
* **Downloads survive Lidarr restarts.** In-flight and completed Slskd transfers re-attach to their grabs after a restart — including multi-disc and retried grabs — so nothing sits finished in Slskd, invisible to Lidarr.
* **Empty download folders are pruned automatically.** Once an import moves the files out (or a download is abandoned), the leftover empty folder is swept away — the gap that neither Slskd's file-retention (which deletes files, never their empty parent directories) nor a missed per-item cleanup (a Lidarr restart, a failed import) covers. It only ever removes folders that are *provably* empty, so nothing holding data is touched. For Slskd this is gated on the **Clean Stale Directories** client option; **Deezer, Tidal and Qobuz do the same sweep** on their own download paths.

---

### Web Clients 📻

Sleezer also ships a family of "web-client" indexers inherited from Tubifarry. These are third-party music services that vary in uptime and quality — Sleezer isn't responsible for any of them.

**Supported:**
* **Lucida** — a multi-source music-downloading service.
* **DABMusic** — a high-resolution audio streaming platform.
* **T2Tunes** — a music-downloading service backed by Amazon Music.
* **SubSonic** — a music-streaming API standard with broad compatibility.

The SubSonic indexer/client is generic: any service that implements the [Subsonic API](https://www.subsonic.org/pages/api.jsp) should plug in without modification.

---

### FFmpeg 🎛️

**FFmpeg** (the component formerly known as "Codec Tinker" in Tubifarry) converts imported audio files between formats. You can set default rules (e.g. "convert all WAV to FLAC", "convert AAC ≥ 256k to MP3 320k") or per-artist overrides. It also backs the corrupt-file scan and pre-import tagging described in the next section, so even users who never touch conversion still benefit from having it configured.

> ⚠️ **Scope note — FFmpeg conversion applies to every track Lidarr imports, not just Sleezer's downloads.** FFmpeg is registered as a Lidarr *Metadata Consumer*, which Lidarr invokes for every imported track regardless of source. Enable it and your torrent, Usenet, and manual imports will also be converted according to the rules you configure. If you only want Sleezer's Deezer/Tidal/Qobuz/Slskd downloads affected, leave the provider disabled — the corrupt-scan and pre-import tagger do **not** require it to be enabled for downloads to work.

#### How to Enable FFmpeg

1. Go to `Settings -> Metadata` in Lidarr.
2. Open **FFmpeg** (the MetadataConsumer).
3. Toggle the switch to enable.

#### Conversion targets and bitrates

The **Default Conversion Settings** dropdown offers four targets: **AAC, MP3, Opus, FLAC**. Custom rules (below) can additionally target **ALAC, WAV, Vorbis/OGG, AIFF, AMR, WMA**.

For lossy targets you may specify a bitrate in kbps. Out-of-range values are clamped to the format's min/max, then rounded to the nearest standard step (`32, 64, 96, 128, 160, 192, 256, 320, 384, 448, 510`):

| Target | Default | Min | Max |
|--------|--------:|----:|----:|
| Opus   | 256 | 32 | 510 |
| MP3    | 320 | 64 | 320 |
| AAC    | 256 | 64 | 320 |
| Vorbis | 224 | 64 | 500 |
| WMA    | 192 | 48 | 320 |
| AMR    | 12  | 5  | 12  |

Encoding is VBR by default; append `:cbr` to force constant bitrate. Lossless targets (FLAC, ALAC, WAV, AIFF) take an optional **bit-depth** instead of a bitrate — `16`, `24`, or `32`.

#### Custom Conversion Rules

A two-column list. The **Key** is the file you want to match; the **Value** is what to turn it into. One row = one rule. (There's no arrow to type — Lidarr's `source -> target` hint just shows which column is which.)

| Key — match these files | Value — convert them to | Result |
|-------------------------|-------------------------|--------|
| `wav` | `flac` | every WAV → FLAC |
| `all` | `alac` | everything → ALAC |
| `aac>=256` | `mp3:320` | AAC at 256k or higher → 320k MP3 |
| `flac:24` | `flac:16` | 24-bit FLAC → 16-bit FLAC |
| `lossy` | `opus:192:cbr` | any lossy file → 192k CBR Opus |

**What you can put in the Key:**
* A format — `flac`, `mp3`, `aac`, `wav`, … — or a group: `all`, `lossy`, `lossless`.
* Optionally, a bitrate filter on a *single lossy format* — `mp3>=256`, `aac<128`, `opus=192`. Operators: `=  !=  <  <=  >  >=`. Groups (`all`/`lossy`/`lossless`) can't take a filter.

**What you can put in the Value:**
* A target format, optionally followed by a quality:
  * lossy target → bitrate in kbps: `mp3:320`, `opus:192`
  * lossless target → bit-depth `16`/`24`/`32`: `flac:24`
* Add `:cbr` to a lossy target to force constant bitrate: `opus:192:cbr`.

**No-upscaling rules.** Sleezer skips any rule that would fake quality the source doesn't have. There are three separate checks:

| Check | Blocked example | Allowed example |
|-------|-----------------|-----------------|
| **Lossy → lossless** — a lossy file already threw data away, so re-wrapping it as FLAC/WAV/ALAC just wastes space | `mp3` → `flac` | `mp3` → `aac:256` (lossy → lossy) |
| **Bitrate upscaling** — *lossy → lossy only*; the target bitrate can't be higher than the source's | `aac<128` → `mp3:256` | `aac>=256` → `mp3:192` (same or lower) |
| **Bit-depth upscaling** — *lossless → lossless only*; the target bit-depth can't be higher than the source's | `flac:16` → `flac:24` | `flac:24` → `flac:16` (same or lower) |

The bitrate check **only** compares a lossy source against a lossy target. A lossless source (FLAC, WAV, ALAC) has no bitrate to exceed, so converting it to a lossy format is allowed at **any** bitrate the target supports — e.g. **FLAC → Opus** accepts the full 32–510k range.

#### Per-artist tags

Add a Lidarr **Tag** to an artist to override the rules above for everything by that artist:

* `opus-192`, `flac`, `mp3-320` — format with an optional `-bitrate`.
* `no-conversion` — disable conversion for that artist entirely.

Artist tags take precedence over Custom Conversion Rules.

#### Per-format toggles

If you'd rather not write rules, the **convert-MP3 / convert-AAC / convert-FLAC / convert-WAV / convert-Opus / convert-Other** checkboxes simply convert any incoming file of that format to the **Default Conversion Settings** target. They only apply when no custom rule and no artist tag already matched the track.

#### FFmpeg binary

Sleezer auto-downloads FFmpeg on first use if it can't find one, pulling the current static build from [`chodeus/ffmpeg-static`](https://github.com/chodeus/ffmpeg-static) (compiled fully-static from pinned upstream source, so it runs on both musl/Alpine and glibc Lidarr containers) into the configured FFmpeg directory. It then checks daily for a newer release and updates itself, so you stay on a current FFmpeg without manual steps. A newer FFmpeg already on the host PATH is always preferred over the downloaded copy, and you can still set the FFmpeg path explicitly in the settings panel. Downloads are SHA-256 verified before use. (No macOS build is published — on macOS, install FFmpeg via Homebrew and it'll be picked up from PATH.)

---

### Corrupt File Scan & Pre-Import Tagging 🧼

These two features live under FFmpeg's settings because they depend on the bundled FFmpeg binary. Both are scoped to **Sleezer's own downloaders only** — Deezer, Tidal, Qobuz, and Slskd. The web clients (Lucida, SubSonic, T2Tunes, DABMusic) currently share a lighter download path that doesn't invoke them, and Lidarr's native torrent/Usenet clients are untouched. Only the FFmpeg *conversion* provider (previous section) runs on imports from every source.

Each feature is opt-in via a chip-style picker: pick which Sleezer downloaders should get the treatment. An empty picker means the feature is off entirely. **Both pickers default empty** — nothing runs until you opt in.

#### Run Corrupt Scan On

When a download finishes, Sleezer runs each audio file through FFmpeg to detect truncated/corrupt streams. If something's broken, the download is deleted and marked failed so Lidarr grabs a different release instead of importing a silent half-track.

Add the clients you want scanned — for example, just **Slskd** (where corrupt files from random peers are the whole reason this exists), or all three if you want belt-and-braces.

#### Run Pre-Import Tagging On

Before Lidarr sees the finished folder, Sleezer reads each file's embedded tags, matches them to the intended Lidarr release via MusicBrainz metadata, and rewrites the file's tags to match. The goal is to make Lidarr's importer see exactly the album/track Lidarr asked for, not whatever the download source happened to name things.

Same picker pattern — add the clients you want tagged.

For **Single/EP** targets there's a title-driven fallback: Soulseek search results contain only the files that matched the query, so a single grabbed out of someone's album rip arrives wearing that album's tags and can never pass album-level identification. When that happens, Sleezer matches the files to the release by *track title* instead (best score first) and tags the matches. Remix/variant qualifiers still refuse to cross-match — a `(KETTAMA remix)` file never gets tagged as the original — and a file whose artist tag names someone else entirely is left untouched for Lidarr to judge.

#### Strip Featured Artists

This is the one that fixes the classic "75% match" import failure on Deezer. Deezer's track titles often read `"Song Name (feat. Other Artist)"`. Lidarr compares that against MusicBrainz which just lists `"Song Name"`, and the fuzzy match falls just under Lidarr's 80% default threshold — so the import silently fails.

With **Strip Featured Artists** enabled, Sleezer:

1. Reads the Title/Artist/AlbumArtist tags from the file.
2. Strips bracketed featured-artist suffixes: `(feat. X)`, `[featuring Y]`, `{ft. Z}` — case-insensitive, bracket-style agnostic.
3. Writes the cleaned tags back to the file.
4. Renames the file from the cleaned tag so the filename Lidarr parses also matches.

Bare-text suffixes without brackets (`Foo feat. Bar`) are left alone to avoid false positives on track titles that legitimately contain the word "feat".

---

### Queue Cleaner 🧹

**Queue Cleaner** handles downloads that fail to import. When Lidarr can't import a grab (missing tracks, bad metadata, etc.), Queue Cleaner can rename files from their embedded tags, retry the import, blocklist the release, or just remove the files.

**Key options:**
* *Blocklist* — remove, blocklist, or both, for failed imports.
* *Rename* — auto-rename folders and tracks from embedded metadata.
* *Clean Imports* — rule-based: clean when tracks are missing, metadata is incomplete, or always.
* *Retry Finding Release* — auto-retry search if the import failed.

**Enable:** `Settings -> Connect`, add a new **Queue Cleaner** connection, configure.

---

### Search Sniper 🏹

**Search Sniper** staggers searches for missing albums so you don't hammer every indexer at once. Instead of running the wanted-list in one pass, it picks a few random albums at an interval and searches just those, tracking what's been tried recently.

You can also trigger it manually from the **Tasks** tab.

**Enable:** `Settings -> Metadata`, open **Search Sniper**, and configure:
* **Picks Per Interval** — how many items to search each cycle.
* **Min Refresh Interval** — how often to run.
* **Cache Type** — Memory or Permanent.
* **Cache Retention Time** — days to keep the cache.
* **Pause When Queued** — stop when the queue hits this size.
* **Search Options** — at least one of Missing albums / Missing tracks / Cutoff not met.

---

### Custom Metadata Sources 🧩

Sleezer can fetch artist and album metadata from **Discogs**, **Deezer**, and **Last.fm** in addition to MusicBrainz. These fill gaps when MusicBrainz is incomplete — cover art, additional artist bios, etc. The **MetaMix** layer combines them intelligently.

**Enable a single source:**

1. `Settings -> Metadata`, open the source you want (Discogs, Deezer, Last.fm).
2. Toggle on.
3. Configure **User Agent**, **API Key**, caching mode, cache directory.

**Enable MetaMix:**

1. `Settings -> Metadata`, open **MetaMix**.
2. **Priority Rules** — hierarchy among sources (lower number = higher priority).
3. **Dynamic Threshold** — how willing MetaMix is to use lower-priority sources.
4. **Multi-Source Population** — missing fields from the primary get filled in from secondary sources.

Best results come with artists that are linked across multiple metadata systems, which is typically the case on MusicBrainz.

---

### Similar Artists 🧷

**Similar Artists** lets you discover related artists via Last.fm's recommendation data, right inside Lidarr's search. Prefix an artist search with `~` and you get back a list of recommendations ready to add.

**Enable:** `Settings -> Metadata`, enable these three:
* **Similar Artists** — enter your Last.fm API key.
* **Lidarr Default** — required for normal searches.
* **MetaMix** — coordinates the search flow.

**Examples:**
* `similar:Pink Floyd`
* `~20244d07-534f-4eff-b4d4-930878889970`

---

## Troubleshooting 🛠️

* **Deezer downloads fail / 403s** — rotate the ARL. Single-ARL bans are the most common cause.
* **Slskd download path permissions** — Lidarr needs read/write on the Slskd download folder. For Docker, check volume mounts and PUID/PGID.
* **FFmpeg issues** — make sure FFmpeg is on PATH, or set its location explicitly in FFmpeg settings. If it's still failing, enable Lidarr's Trace logging and look for the full ffmpeg command line in the log.
* **Metadata not being added** — confirm your files are in a supported format. If you're using FFmpeg conversion, check the output format is one Lidarr accepts (AAC in MP4, FLAC, MP3, Opus, ALAC).
* **"X% match" import failure on Deezer** — enable **Strip Featured Artists** (see above). This is the single biggest fix for Deezer's `(feat. X)` titles being rejected by Lidarr's 80% matcher.
* **No release found** — confirm the indexer is enabled in Delay Profiles (the wrench icon on each profile).

Enable **Debug** log level in `Settings -> General` if you're filing an issue — Sleezer logs the request/response lifecycle at Debug and ARL/API-key values are redacted.

---

## Credits 🙌

Sleezer exists because of these people:

* **[TrevTV](https://github.com/TrevTV)** — author of [Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer) and the [DeezNET](https://github.com/TrevTV/DeezNET) client library that powers Sleezer's Deezer integration. Nothing Deezer-related in this plugin would exist without his work.
* **[TypNull](https://github.com/TypNull)** — author of [Tubifarry](https://github.com/TypNull/Tubifarry), which contributed the Slskd integration, web-client framework, FFmpeg pipeline, Queue Cleaner, Search Sniper, custom metadata sources, and Similar Artists. Sleezer is basically Tubifarry with YouTube/Spotify/Lyrics/telemetry stripped out and Deezer bolted in.
* **[DaveBinM](https://github.com/DaveBinM)** — maintainer of the living fork of [Lidarr.Plugin.Qobuz](https://github.com/DaveBinM/Lidarr.Plugin.Qobuz) (originally TrevTV's) and of [QobuzApiSharp](https://github.com/DaveBinM/QobuzApiSharp) (originally [DJDoubleD](https://github.com/DJDoubleD)'s). Sleezer's Qobuz indexer and download client are ported from that fork.

Also thanks to the maintainers of Lidarr's plugin system, and the authors of every bundled library listed in [NOTICE](NOTICE).

If you're reporting an issue with something that originated upstream (DeezNET, the Slskd protocol, etc.), the bug tracker on the upstream repo is usually the right place. For issues with Sleezer's integration of them — or anything added in the merge — the [Sleezer issue tracker](https://github.com/chodeus/sleezer/issues) is the right place.

---

## Contributing 🤝

Open an issue or PR on the [GitHub repo](https://github.com/chodeus/sleezer). Contributions follow the guidelines in [CONTRIBUTION.md](CONTRIBUTION.md).

---

## License 📄

Sleezer is licensed under **GPL-3.0**. See [LICENSE](LICENSE) for the full text and [NOTICE](NOTICE) for attributions to the upstream projects and bundled libraries.

The GPL-3.0 license is required because Sleezer bundles [DeezNET](https://github.com/TrevTV/DeezNET), which is GPL-3.0 itself.

---

Enjoy seamless music downloads with Sleezer! 🎧
