#!/usr/bin/env bash
# Prints the package ids found in a pack output directory, one per line, sorted.
#
# The version-stripping rule lived in three copies across ci.yml and publish.yml,
# which is how the publish list drifted from the CI assertion in the first place
# (#33). One copy here, called by both workflows.
#
# Usage: scripts/packed-package-ids.sh <directory>
set -euo pipefail

dir="${1:?usage: packed-package-ids.sh <directory>}"

[ -d "$dir" ] || { echo "packed-package-ids.sh: no such directory: $dir" >&2; exit 1; }

find "$dir" -maxdepth 1 -name '*.nupkg' ! -name '*.symbols.nupkg' -exec basename {} \; \
  | sed -E 's/\.[0-9]+(\.[0-9]+)*(-[0-9A-Za-z.-]+)?\.nupkg$//' \
  | sort -u
