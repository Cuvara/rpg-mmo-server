#!/usr/bin/env bash
# Check framework for the deployment verification suite.
#
# Every check declares what it PROVES and what it CANNOT prove, and must end in
# exactly one verdict: pass / fail / warn / skip. A check that falls off its own
# end without a verdict is recorded as FAIL, not PASS -- this project has a
# documented history of a soft `return` reading as a green line, which makes
# absence of coverage look identical to coverage.

set -uo pipefail

declare -a CHECK_ORDER=()
declare -A CHECK_TITLE=() CHECK_PROVES=() CHECK_LIMITS=() CHECK_LAYER=() CHECK_FN=()

declare -a R_ID=() R_STATUS=() R_MSG=()
_verdict=""; _msg=""

# register <id> <layer> <title> <proves> <cannot-prove> <function>
register() {
  local id="$1"
  CHECK_ORDER+=("$id")
  CHECK_LAYER[$id]="$2"
  CHECK_TITLE[$id]="$3"
  CHECK_PROVES[$id]="$4"
  CHECK_LIMITS[$id]="$5"
  CHECK_FN[$id]="$6"
}

pass() { _verdict=PASS; _msg="$*"; }
warn() { _verdict=WARN; _msg="$*"; }
skip() { _verdict=SKIP; _msg="$*"; }
# fail <summary> [expected] [observed] [where-to-look]
fail() {
  _verdict=FAIL
  _msg="$1"
  [ $# -ge 2 ] && [ -n "$2" ] && _msg+=$'\n      expected: '"$2"
  [ $# -ge 3 ] && [ -n "$3" ] && _msg+=$'\n      observed: '"$3"
  [ $# -ge 4 ] && [ -n "$4" ] && _msg+=$'\n      look at:  '"$4"
  return 0
}

C_RESET=""; C_PASS=""; C_FAIL=""; C_SKIP=""; C_WARN=""; C_DIM=""
if [ -t 1 ] && [ "${NO_COLOR:-}" = "" ]; then
  C_RESET=$'\033[0m'; C_PASS=$'\033[32m'; C_FAIL=$'\033[31m'
  C_SKIP=$'\033[33m'; C_WARN=$'\033[35m'; C_DIM=$'\033[2m'
fi

status_color() {
  case "$1" in
    PASS) printf '%s' "$C_PASS" ;;
    FAIL) printf '%s' "$C_FAIL" ;;
    SKIP) printf '%s' "$C_SKIP" ;;
    WARN) printf '%s' "$C_WARN" ;;
  esac
}

run_check() {
  local id="$1"
  local fn="${CHECK_FN[$id]}"
  _verdict=""; _msg=""
  "$fn" || true
  if [ -z "$_verdict" ]; then
    _verdict=FAIL
    _msg="check ended without a verdict (no pass/fail/warn/skip call) -- treated as FAIL so missing coverage cannot read as green"$'\n      look at:  '"the body of ${fn}()"
  fi
  R_ID+=("$id"); R_STATUS+=("$_verdict"); R_MSG+=("$_msg")
  printf '  %s%-4s%s %-28s %s\n' "$(status_color "$_verdict")" "$_verdict" "$C_RESET" "$id" "${CHECK_TITLE[$id]}"
  if [ -n "$_msg" ]; then
    printf '      %s\n' "${_msg//$'\n'/$'\n'}" | sed 's/^      $//'
  fi
  if [ "$_verdict" = FAIL ]; then
    printf '      %sproves: %s%s\n' "$C_DIM" "${CHECK_PROVES[$id]}" "$C_RESET"
  fi
}

# require <cmd...> -- skip the current check when a tool is missing, loudly.
have() { command -v "$1" >/dev/null 2>&1; }

# summary -> exit code. FAIL always fails the run; SKIP fails it under --strict.
summary() {
  local strict="$1"
  local p=0 f=0 s=0 w=0 i
  for i in "${!R_ID[@]}"; do
    case "${R_STATUS[$i]}" in
      PASS) p=$((p+1)) ;; FAIL) f=$((f+1)) ;; SKIP) s=$((s+1)) ;; WARN) w=$((w+1)) ;;
    esac
  done
  echo
  echo "================================================================"
  printf 'checks: %d  %sPASS %d%s  %sFAIL %d%s  %sSKIP %d%s  %sWARN %d%s\n' \
    "${#R_ID[@]}" "$C_PASS" "$p" "$C_RESET" "$C_FAIL" "$f" "$C_RESET" \
    "$C_SKIP" "$s" "$C_RESET" "$C_WARN" "$w" "$C_RESET"
  if [ "$f" -gt 0 ]; then
    echo "failed:"
    for i in "${!R_ID[@]}"; do
      [ "${R_STATUS[$i]}" = FAIL ] && printf '  - %s: %s\n' "${R_ID[$i]}" "${CHECK_TITLE[${R_ID[$i]}]}"
    done
  fi
  if [ "$s" -gt 0 ]; then
    # Skips are printed again here on purpose: a skipped check is absent
    # coverage, and absent coverage must never be quiet.
    echo "skipped (NOT verified -- absence of coverage, not evidence of health):"
    for i in "${!R_ID[@]}"; do
      [ "${R_STATUS[$i]}" = SKIP ] && printf '  - %s: %s\n' "${R_ID[$i]}" "${CHECK_TITLE[${R_ID[$i]}]}"
    done
  fi
  if [ "$w" -gt 0 ]; then
    echo "warnings (checked, non-fatal for this target):"
    for i in "${!R_ID[@]}"; do
      [ "${R_STATUS[$i]}" = WARN ] && printf '  - %s: %s\n' "${R_ID[$i]}" "${CHECK_TITLE[${R_ID[$i]}]}"
    done
  fi
  echo "================================================================"
  if [ "$f" -gt 0 ]; then echo "VERIFY=FAIL"; return 1; fi
  if [ "$strict" = "1" ] && [ "$s" -gt 0 ]; then
    echo "VERIFY=FAIL (--strict: $s skipped check(s) count as failures)"; return 1
  fi
  echo "VERIFY=PASS"; return 0
}
