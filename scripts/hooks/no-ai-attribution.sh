#!/usr/bin/env bash
#
# PreToolUse hook. Blocks the two House style violations (see CONTRIBUTING.md)
# before they reach anything permanent: git commit messages, and text posted to
# GitHub issues and pull requests.
#
# This is the backstop for the failure mode CI cannot catch, which is an agent
# posting a comment, and the one it catches too late, which is a commit message
# that then needs history rewriting to fix.
#
# Reads the hook payload on stdin, prints a PreToolUse deny decision on a hit,
# and stays silent otherwise.

set -uo pipefail

input=$(cat)
tool=$(printf '%s' "$input" | jq -r '.tool_name // ""')

EM_DASH=$'—'
ATTRIB='Generated (with|by) .{0,25}(Claude|Copilot)|Co-Authored-By: *(Claude|Copilot)|Claude-Session:'

text=""
case "$tool" in
  Bash)
    cmd=$(printf '%s' "$input" | jq -r '.tool_input.command // ""')
    # Only inspect commands that write permanent text. Scanning every command
    # would block honest things like grepping the repository for an em dash,
    # which is exactly what scripts/check-house-style.sh does.
    if printf '%s' "$cmd" | grep -Eq '(^|[;&|(]|[[:space:]])git[[:space:]]+(commit|tag|notes)([[:space:]]|$)'; then
      text="$cmd"
    fi
    ;;
  mcp__github__*)
    # Comment bodies, PR titles and descriptions, review bodies.
    text=$(printf '%s' "$input" | jq -r '
      [.tool_input.body?, .tool_input.title?, .tool_input.summary?]
      | map(select(. != null and . != "")) | join("\n")')
    ;;
esac

[ -n "$text" ] || exit 0

reason=""
if printf '%s' "$text" | grep -Fq "$EM_DASH"; then
  reason="an em dash"
fi
if printf '%s' "$text" | grep -Eq "$ATTRIB"; then
  [ -n "$reason" ] && reason="$reason and AI attribution" || reason="AI attribution"
fi

[ -n "$reason" ] || exit 0

detail="This text contains ${reason}, which the House style section of CONTRIBUTING.md forbids."
case "$reason" in
  *"em dash"*) detail="$detail Replace the em dash with a comma, a colon, a semicolon, parentheses or two sentences." ;;
esac
case "$reason" in
  *attribution*) detail="$detail Remove the attribution line; the author is whoever submits the work." ;;
esac

jq -n --arg r "$detail" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "deny",
    permissionDecisionReason: $r
  }
}'
exit 0
