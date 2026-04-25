# Sleezer for Lidarr 🎶

![License](https://img.shields.io/github/license/chodeus/sleezer) ![GitHub release (latest by date)](https://img.shields.io/github/v/release/chodeus/sleezer) ![GitHub last commit](https://img.shields.io/github/last-commit/chodeus/sleezer) ![GitHub stars](https://img.shields.io/github/stars/chodeus/sleezer)

Sleezer is a Lidarr plugin that adds **Deezer**, **Tidal**, **Slskd (Soulseek)**, and a handful of other music sources behind a single install. It also ships post-processing: corrupt-file scanning and pre-import tagging for Deezer/Tidal/Slskd downloads, plus an FFmpeg-based format converter that runs on every imported track regardless of source. 🛠️

Credit where it's due: Sleezer is built on [Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer) by [TrevTV](https://github.com/TrevTV) and [Tubifarry](https://github.com/TypNull/Tubifarry) by [TypNull](https://github.com/TypNull). See [Credits](#credits-).

---

## Table of Contents 📑

1. [Installation 🚀](#installation-)
2. [Deezer Setup 🎧](#deezer-setup-)
3. [Tidal Setup 🌊](#tidal-setup-)
4. [Soulseek (Slskd) Setup 🐟](#soulseek-slskd-setup-)
5. [Web Clients 📻](#web-clients-)
5. [FFmpeg 🎛️](#ffmpeg-️)
6. [Corrupt File Scan & Pre-Import Tagging 🧼](#corrupt-file-scan--pre-import-tagging-)
7. [Queue Cleaner 🧹](#queue-cleaner-)
8. [Search Sniper 🏹](#search-sniper-)
9. [Custom Metadata Sources 🧩](#custom-metadata-sources-)
10. [Similar Artists 🧷](#similar-artists-)
11. [Troubleshooting 🛠️](#troubleshooting-)
12. [Credits 🙌](#credits-)
13. [License 📄](#license-)

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

> Why the extra "I've Authorized" click? Tidal's device-code OAuth flow doesn't redirect back to Lidarr after you authorize — its "Device linked" page is the end of the road on Tidal's side. The intermediate popup acts as the bridge so Lidarr knows when you're done.

#### Setting Up the Tidal Download Client

1. `Settings -> Download Clients`, click `+` to add.
2. Select `Tidal` from the list.
3. Set the **Download Path** Lidarr should monitor.
4. Optional: enable **Extract FLAC From M4A** (Tidal ships FLAC inside an M4A container; this unwraps it) or **Re-encode AAC into MP3**. Both require FFmpeg on PATH.
5. **Profiles → Delay Profiles**: tick **Tidal** on the default profile so Lidarr will grab from it.

#### Notes & Troubleshooting

* The post-processing pipeline (corrupt-file scan + pre-import tagging) runs on Tidal downloads, just like Deezer and Slskd.
* If searches start returning errors that mention `countryCode parameter missing`, that's Tidal's confusing way of saying your session expired. Sleezer detects this and forces a refresh; if that fails, re-authenticate via the indexer settings.
* Various Artists, Soundtracks, and Cast Recordings are recognised explicitly so they actually return search hits.
* Tidal music videos and Dolby Atmos tracks are not supported in this release.
* Tidal does not expose a public RSS / new-release feed, so RSS sync is disabled at the indexer level.

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

---

### Web Clients 📻

Sleezer also ships a family of "web-client" indexers inherited from Tubifarry. These are third-party music services that vary in uptime and quality — Sleezer isn't responsible for any of them.

**Supported:**
* **Lucida** — a multi-source music-downloading service.
* **DABmusic** — a high-resolution audio streaming platform.
* **T2Tunes** — a music-downloading service backed by Amazon Music.
* **Subsonic** — a music-streaming API standard with broad compatibility.

The Subsonic indexer/client is generic: any service that implements the [Subsonic API](https://www.subsonic.org/pages/api.jsp) should plug in without modification.

---

### FFmpeg 🎛️

**FFmpeg** (the component formerly known as "Codec Tinker" in Tubifarry) converts imported audio files between formats. You can set default rules (e.g. "convert all WAV to FLAC", "convert AAC ≥ 256k to MP3 300k") or per-artist overrides. It also backs the corrupt-file scan and pre-import tagging described in the next section, so even users who never touch conversion still benefit from having it configured.

> ⚠️ **Scope note — FFmpeg conversion applies to every track Lidarr imports, not just Sleezer's downloads.** FFmpeg is registered as a Lidarr *Metadata Consumer*, which Lidarr invokes for every imported track regardless of source. Enable it and your torrent, Usenet, and manual imports will also be converted according to the rules you configure. If you only want Sleezer's Deezer/Tidal/Slskd downloads affected, leave the provider disabled — the corrupt-scan and pre-import tagger do **not** require it to be enabled for downloads to work.

> Lossy formats (MP3, AAC) can't be converted up into lossless formats (FLAC, WAV). Quality that wasn't there can't be restored.

#### How to Enable FFmpeg

1. Go to `Settings -> Metadata` in Lidarr.
2. Open **FFmpeg** (the MetadataConsumer).
3. Toggle the switch to enable.

#### How to Use FFmpeg

1. **Default Conversion Settings** — pick your target format (FLAC, Opus, MP3, ALAC …).
2. **Custom Conversion Rules** — strings like `wav -> flac`, `AAC>=256k -> MP3:300k`, or `all -> alac`.
3. **Custom Conversion Rules On Artists** — tags like `opus-192` applied to every album of a specific artist.
4. **Format toggles** — convert-MP3, convert-FLAC, etc., if you want the simple per-format toggles instead of rules.

#### FFmpeg binary

Sleezer ships with a downloader (`Xabe.FFmpeg.Downloader`) and will fetch FFmpeg on first use if it can't find one on PATH. You can also set the FFmpeg binary path explicitly in the settings panel.

---

### Corrupt File Scan & Pre-Import Tagging 🧼

These two features live under FFmpeg's settings because they depend on the bundled FFmpeg binary. Both are scoped to **Sleezer's own downloaders only** — Deezer, Tidal, and Slskd. The web clients (Lucida, SubSonic, TripleTriple, DABMusic) currently share a lighter download path that doesn't invoke them, and Lidarr's native torrent/Usenet clients are untouched. Only the FFmpeg *conversion* provider (previous section) runs on imports from every source.

Each feature has a master toggle plus per-client toggles. A feature only runs against a given client when the master switch **and** that client's toggle are both on. **All toggles default off** — nothing runs until you opt in.

#### Enable Corrupt File Scan

When a download finishes, Sleezer runs each audio file through FFmpeg to detect truncated/corrupt streams. If something's broken, the download is deleted and marked failed so Lidarr grabs a different release instead of importing a silent half-track.

The per-client toggles — **Corrupt Scan: Deezer**, **Corrupt Scan: Tidal**, **Corrupt Scan: Slskd** — let you turn the scan off for a specific client without disabling it everywhere. For example, leave it on for Slskd (where corrupt files from random peers are the whole reason this exists) and turn it off for Deezer if you trust the source.

#### Enable Pre-Import Tagging

Before Lidarr sees the finished folder, Sleezer reads each file's embedded tags, matches them to the intended Lidarr release via MusicBrainz metadata, and rewrites the file's tags to match. The goal is to make Lidarr's importer see exactly the album/track Lidarr asked for, not whatever the download source happened to name things.

Same pattern: per-client toggles — **Pre-Import Tag: Deezer**, **Pre-Import Tag: Tidal**, **Pre-Import Tag: Slskd** — let you decide which clients get the tagging treatment.

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

Sleezer exists because of two people:

* **[TrevTV](https://github.com/TrevTV)** — author of [Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer) and the [DeezNET](https://github.com/TrevTV/DeezNET) client library that powers Sleezer's Deezer integration. Nothing Deezer-related in this plugin would exist without his work.
* **[TypNull](https://github.com/TypNull)** — author of [Tubifarry](https://github.com/TypNull/Tubifarry), which contributed the Slskd integration, web-client framework, FFmpeg pipeline, Queue Cleaner, Search Sniper, custom metadata sources, and Similar Artists. Sleezer is basically Tubifarry with YouTube/Spotify/Lyrics/telemetry stripped out and Deezer bolted in.

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
