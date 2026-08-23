#!/usr/bin/env python3
"""Assert Qobuz's web-player bundle still yields an app_id and app_secret.

Mirrors src/Sleezer/QobuzApiSharp/Service/QobuzApiHelper.cs. If the regexes there
change, change them here too — this exists to fail before a user does.
"""

import base64
import re
import sys
import urllib.request

PLAYER = "https://play.qobuz.com"
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"

# Kept identical to QobuzApiHelper.cs.
BUNDLE_RE = re.compile(r'<script src="(?P<bundle>/resources/\d+\.\d+\.\d+-[a-z]\d{3}/bundle\.js)')
APP_ID_RE = re.compile(r'production:\{api:\{appId:"(\d+)"')
SEED_RE = re.compile(r'initialSeed\("([^"]+)",window\.utimezone\.berlin\)')
BERLIN_RE = re.compile(r'name:"Europe/Berlin",info:"([^"]+)",extras:"([^"]+)"')


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8", "replace")


def fail(message):
    sys.exit(f"FAIL: {message}")


def main():
    login = fetch(f"{PLAYER}/login")

    bundle_match = BUNDLE_RE.search(login)
    if not bundle_match:
        fail("no bundle.js <script src> matched on the login page — the player's markup changed")

    bundle_path = bundle_match.group("bundle")
    bundle = fetch(f"{PLAYER}{bundle_path}")

    app_id_match = APP_ID_RE.search(bundle)
    if not app_id_match:
        fail(f"app_id regex did not match in {bundle_path}")

    seed_match = SEED_RE.search(bundle)
    if not seed_match:
        fail(f"initialSeed regex did not match in {bundle_path}")

    berlin_match = BERLIN_RE.search(bundle)
    if not berlin_match:
        fail(f"Europe/Berlin info/extras regex did not match in {bundle_path}")

    combined = seed_match.group(1) + berlin_match.group(1) + berlin_match.group(2)
    truncated = combined[: len(combined) - 44]
    padding = len(truncated) % 4
    if padding:
        truncated += "=" * (4 - padding)

    try:
        secret = base64.b64decode(truncated).decode("utf-8")
    except Exception as exc:  # noqa: BLE001 - any decode failure is the same verdict
        fail(f"app_secret did not base64-decode from {bundle_path}: {exc}")

    # Qobuz app secrets are 32 lowercase hex characters. Anything else means the
    # derivation drifted even though every regex happened to match.
    if not re.fullmatch(r"[0-9a-f]{32}", secret):
        fail(f"derived app_secret is not 32 hex chars (got {len(secret)} chars) from {bundle_path}")

    print(f"bundle   {bundle_path}")
    print(f"app_id   {app_id_match.group(1)}")
    print(f"secret   resolved, {len(secret)} hex chars")


if __name__ == "__main__":
    main()
