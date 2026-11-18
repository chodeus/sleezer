#!/usr/bin/env bash
# Usage: check-nightly.sh <plugin.dll> [framework]. Runs PluginCheck against Lidarr's nightly; with a framework, also fails unless the nightly runs it.
set -euo pipefail

plugin=$1
framework=${2:-}
host=$(mktemp -d)
trap 'rm -rf "$host"' EXIT

if ! curl -fsSL --retry 3 -o "$host/lidarr.tar.gz" \
  "https://lidarr.servarr.com/v1/update/nightly/updatefile?os=linux&runtime=netcore&arch=x64"; then
  echo "::error::Could not download Lidarr's nightly build"
  exit 1
fi
tar -xzf "$host/lidarr.tar.gz" -C "$host"
nightly=$(jq -r .runtimeOptions.tfm "$host/Lidarr/Lidarr.runtimeconfig.json")
if [ -n "$framework" ] && [ "$nightly" != "$framework" ]; then
  echo "::error::Lidarr's nightly runs $nightly, not $framework, so the $framework zip cannot be checked"
  exit 1
fi

project="$(dirname "$0")/PluginCheck.csproj"
dotnet build "$project" -c Release --nologo
if ! dotnet run --project "$project" -c Release --no-build -- "$plugin" "$host/Lidarr"; then
  echo "::error::PluginCheck failed against Lidarr's $nightly nightly (see above)"
  exit 1
fi
