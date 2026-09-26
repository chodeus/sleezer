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
| Web clients | Tubifarry | Lucida and DABmusic removed, because they no longer deliver downloads. The rest get scanning and tagging. |

## Soulseek search

| Before | Sleezer |
|---|---|
| 44% of searches found nothing | Queries fixed: Unicode punctuation in names, plain `Artist Album` first, no chopped words |
| Multi-disc shares split per disc | One release |
| Share ranking never reached Lidarr | Ranking applied, and users who just failed rank lower |
| Singles stitched from several users | One source per single |
| A Soulseek user whose download failed was tried again on the next search | That release skips automatic searches for 1 h, then 6 h, then 24 h |

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

## Blocklist and failures

| Before | Sleezer |
|---|---|
| Plugin blocklist entries never matched | They block |
| Removing a wrong store release blocked one quality | Blocks every quality of that album |
| A Qobuz album the account can't fully stream was retried at each quality | Blocked once, with the reason |

## FFmpeg and more

| Before | Sleezer |
|---|---|
| FFmpeg downloaded once, unchecked, never updated | Own static builds, SHA-256 checked, updated daily |
| Some corrupt files passed the check | Caught, and bad cover art no longer deletes good audio |
| Search Sniper ignored saved settings and needed a restart | Reads saved settings, applies without a restart |
| Credentials could end up in logs | Deezer ARL and Tidal/Qobuz tokens masked, so logs are safe to share |
