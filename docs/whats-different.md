# What Sleezer changes

Sleezer started as a merge of the plugins listed under [Credits](../README.md#credits-). This page lists what it does differently from them. Fixes Sleezer ported back from Tubifarry aren't listed.

## Sources

| Source | Started from | What Sleezer adds |
|---|---|---|
| Deezer | Lidarr.Plugin.Deezer | Own downloader: DeezNET's decoder left junk bytes on nearly every track. Queue survives restarts. Tracks blocked in your country are skipped at search. |
| Tidal | Lidarr.Plugin.Tidal | One-click device login. Catches Tidal quietly sending AAC instead of lossless. Failures name your account's country. |
| Qobuz | Lidarr.Plugin.Qobuz | A rotated app secret is picked up without a restart. Credentials kept out of logs. Preview-only tracks skipped. Import lists fixed. |
| Bandcamp | lidarr-plugin-bandcamp | Largely rewritten. Fixes a security hole and multi-GB archives loaded into memory. |
| Soulseek | Tubifarry | See [Soulseek search](#soulseek-search) and [Downloads and imports](#downloads-and-imports). |
| Web clients | Tubifarry | Lucida, DABmusic and T2Tunes removed. SubSonic gets scanning and tagging. |

## Soulseek search

| Before | Sleezer |
|---|---|
| 44% of searches found nothing | Queries fixed: Unicode punctuation in names, plain `Artist Album` first, no chopped words |
| Multi-disc shares split per disc | One release |
| Share ranking never reached Lidarr | Ranking applied, and users who just failed rank lower |
| Singles stitched from several users | One source per single |
| A Soulseek user whose download failed was tried again on the next search | That release skips automatic searches for 1 h, then 6 h, then 24 h |
| A single matched on its one track title alone, even in another artist's folder | One title counts only when the folder names the artist or the album, the file names the artist, or the search has no artist |

## Store search and titles

Store search covers Deezer, Qobuz, Tidal and Bandcamp.

| Before | Sleezer |
|---|---|
| Whatever the store ranked first was grabbed | Checked against MusicBrainz: artist, title, track count, length |
| Remix, live or extended versions could stand in for the album | Never cross-matched |
| Reissues and same-titled albums confused | Year checked against every official release and the artist's other albums |
| `Album (Remixes)` read by Lidarr as the wrong album | Titles built so Lidarr maps them to the searched album |
| Rejections hidden | The reason shows in interactive search, and you can still grab it |
| Upgrades grabbed different recordings (radio edits, piano versions) | Rejected before download |

## Downloads and imports

| Before | Sleezer |
|---|---|
| Corrupt downloads imported as-is | Files decoded first, and bad downloads re-searched (opt-in per client) |
| Tags left as the source named them | Retagged to the release Lidarr asked for (opt-in per client) |
| Store downloads matched to a CD release | Matched to the Digital Media release of the same length |
| Singles taken from full-album shares failed import | Matched by track title |
| Store titles with `(feat. X)` failed Lidarr's 80% match | Featured credits ignored when matching |
| Soulseek files trusted by name alone | Audio fingerprint (AcoustID) checked against the wanted recording: always for singles taken from full-album shares, optionally for every file |
| "Found multiple artists" stuck forever | Resolved from grab history |
| Soulseek: restarts, retries and shared folders lost downloads | Downloads resume, retries import, no cross-deletes |
| Empty download folders pile up | Swept automatically |
| No provenance | `SOURCE` tag with the store URL; `.cue` and `.log` files kept |
| Removing a Soulseek grab after a Lidarr restart left the files the tagger had renamed | Deleted: the renamed files are remembered across restarts |
| Queue Cleaner's Remove deleted everything in the download folder, past the recycle bin | Removes through the download client, which deletes only the download's own files |
| Queue Cleaner's Blocklist Only failed the download, so it was removed and searched again | Blocklists only; the item stays queued for a manual import |
| Queue Cleaner's Rename renamed only the first file | Renames every audio file, under the album's title |
| FFmpeg conversion ran on every library file at each artist scan, probing each one even with no rule set | A file is converted only when Lidarr imports it into the library, not when a scan finds it there; with no rule set, no file is probed |

## Blocklist and failures

| Before | Sleezer |
|---|---|
| Plugin blocklist entries never matched | They block |
| Removing a wrong store release blocked one quality | Blocks every quality of that album |
| A Qobuz album the account can't fully stream was retried at each quality | Blocked once, with the reason |
| A Soulseek release you removed with blocklist was back an hour later | Blocked until you delete the blocklist entry, as in Lidarr |

## FFmpeg and more

| Before | Sleezer |
|---|---|
| FFmpeg downloaded once, unchecked, never updated | Own static builds, SHA-256 checked, updated daily |
| Some corrupt files passed the check | Caught, and bad cover art no longer deletes good audio |
| Search Sniper ignored saved settings and needed a restart | Reads saved settings, applies without a restart |
| Credentials could end up in logs | Deezer ARL and Tidal/Qobuz tokens masked, so logs are safe to share |
