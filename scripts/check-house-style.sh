#!/usr/bin/env bash
#
# House style checks. See the "House style" section of CONTRIBUTING.md.
#
#   scripts/check-house-style.sh [base-ref]
#
# Checks only lines the change *adds*, and the commit messages the change
# introduces, so pre-existing text is never anyone's problem to fix. Defaults to
# comparing against origin/master.
#
# Install as a local pre-commit hook if you like:
#   ln -s ../../scripts/check-house-style.sh .git/hooks/pre-commit

set -uo pipefail

BASE="${1:-}"
if [ -z "$BASE" ]; then
  BASE="$(git merge-base HEAD origin/master 2>/dev/null || git rev-parse HEAD~1)"
fi

# These files define or implement the rules, so they have to contain the very
# strings the rules ban. Everything else is fair game.
EXEMPT='^(CONTRIBUTING\.md|CLAUDE\.md|\.cursorrules|scripts/check-house-style\.sh|\.github/workflows/house-style\.yml)$'

EM_DASH=$'—'
# Split so this file does not trip the pattern it is searching for.
ATTRIB="Generated (with|by) .{0,20}Claude|Co-Authored-By: *(Claude|Copilot)|Claude-Session:|Generated (with|by) .{0,20}Copilot|robot: *Generated"

fail=0
note() { printf '  %s\n' "$1"; }

echo "House style: comparing against ${BASE}"

# ---- added lines ----------------------------------------------------------
while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || continue
  # Skip anything that is not text.
  if ! grep -Iq . "$f" 2>/dev/null; then continue; fi

  added=$(git diff -U0 "$BASE"...HEAD -- "$f" | awk '
    /^\+\+\+/ { next }
    /^@@/     { split($3, a, ","); ln = substr(a[1], 2) + 0; next }
    /^\+/     { print ln ": " substr($0, 2); ln++ }
  ')
  [ -n "$added" ] || continue

  if hits=$(printf '%s\n' "$added" | grep -F "$EM_DASH"); then
    echo "FAIL $f: em dash in added lines"
    printf '%s\n' "$hits" | while IFS= read -r l; do note "$l"; done
    fail=1
  fi

  if ! printf '%s' "$f" | grep -Eq "$EXEMPT"; then
    if hits=$(printf '%s\n' "$added" | grep -Ei "$ATTRIB"); then
      echo "FAIL $f: AI attribution in added lines"
      printf '%s\n' "$hits" | while IFS= read -r l; do note "$l"; done
      fail=1
    fi
  fi
done < <(git diff --name-only --diff-filter=ACMR "$BASE"...HEAD)

# ---- commit messages ------------------------------------------------------
while IFS= read -r sha; do
  [ -n "$sha" ] || continue
  msg=$(git log -1 --format='%B' "$sha")
  subject=$(git log -1 --format='%s' "$sha")

  if printf '%s' "$msg" | grep -Fq "$EM_DASH"; then
    echo "FAIL commit ${sha:0:8}: em dash in message (${subject})"
    fail=1
  fi
  if printf '%s' "$msg" | grep -Eiq "$ATTRIB"; then
    echo "FAIL commit ${sha:0:8}: AI attribution trailer in message (${subject})"
    note "strip it with: git rebase -i ${BASE}"
    fail=1
  fi
done < <(git log --format='%H' "$BASE"..HEAD)

if [ "$fail" -eq 0 ]; then
  echo "House style: clean."
else
  echo
  echo "See the House style section of CONTRIBUTING.md. Use a comma, a colon, a"
  echo "semicolon, parentheses or two sentences instead of an em dash, and sign"
  echo "your work as yourself."
fi
exit "$fail"
