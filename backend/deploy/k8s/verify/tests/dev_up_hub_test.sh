#!/usr/bin/env bash
# Offline regression cases for the Studio hub block of k8s/dev-up.sh.
#
# dev-up.sh imports images and applies namespaces, so it is never executed whole. Only
# the span between "# BEGIN studio hub rbac apply" and "# END studio hub rbac apply"
# (each required to occur once) is extracted and run against a fake kubectl on PATH,
# on the nonexistent context "does-not-exist": the fake answers only the reads and
# applies the block makes and logs (exit 99) anything else, so no cluster is reachable.
#
# What it shows:
#   - the block applies EXACTLY k8s/hub/{00-namespace,10-serviceaccount,20-rbac}.yaml,
#     by name, in that order, and nothing else: never optional/, never a glob, no
#     ClusterRole, no token;
#   - it sits after the app and data tiers are applied and before the final report;
#   - a game namespace without its Role warns (the per-game pair arrives with the
#     rendered game manifests) and does not stop the script;
#   - a failing apply stops the script before the next file.
# Run: bash backend/deploy/k8s/verify/tests/dev_up_hub_test.sh
set -uo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
DEVUP="$HERE/dev-up.sh"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
PASS=0 FAIL=0
ok() { echo "ok - $*"; PASS=$((PASS+1)); }
bad() { echo "FAIL - $*"; FAIL=$((FAIL+1)); }
check() { # name command...: ok when the command succeeds
  local name=$1; shift
  if "$@"; then ok "$name"; else bad "$name"; fi
}

NAME='studio hub rbac apply'
begin="# BEGIN $NAME" end="# END $NAME"
if [ "$(grep -Fxc "$begin" "$DEVUP")" != 1 ] || [ "$(grep -Fxc "$end" "$DEVUP")" != 1 ]; then
  bad "$NAME markers must occur exactly once"; exit 1
fi
awk -v b="$begin" -v e="$end" '$0 == b {p=1} p {print} $0 == e {exit}' "$DEVUP" > "$WORK/block"
grep -Fxq "$end" "$WORK/block" || { bad "block extraction"; exit 1; }
ok "block extraction"
# The block calls this helper, which dev-up.sh defines inside the game TLS repair block.
sed -n '/^rendered_game_namespaces() {/,/^}/p' "$DEVUP" > "$WORK/rendered_game_namespaces"
grep -q '^rendered_game_namespaces() {' "$WORK/rendered_game_namespaces" || { bad "rendered_game_namespaces extraction"; exit 1; }
# The code of the block: comments and echo lines stripped, because those name what it must not do.
grep -v -e '^[[:space:]]*#' -e '^[[:space:]]*echo ' "$WORK/block" > "$WORK/block.code"

# ------------------------------------------------------------------ placement
line_of() { grep -nFx -- "$1" "$DEVUP" | head -n 1 | cut -d: -f1; }
b=$(line_of "$begin"); e=$(line_of "$end")
app_apply=$(grep -nF 'say "apply the app tier (rpg-k8s-realtime)"' "$DEVUP" | head -n 1 | cut -d: -f1)
data_apply=$(grep -nF 'say "apply the data tier (rpg-k8s-data)"' "$DEVUP" | head -n 1 | cut -d: -f1)
state_line=$(line_of 'say "state"')
check 'block sits after the data tier and the app tier are applied' \
  test -n "$b" -a -n "$app_apply" -a -n "$data_apply" -a "$b" -gt "$app_apply" -a "$b" -gt "$data_apply"
check 'block ends before the final state report' test -n "$e" -a -n "$state_line" -a "$e" -lt "$state_line"

# ------------------------------------------------------------------ static rules
check 'the block names exactly the three files, in order' \
  grep -Fxq 'for f in 00-namespace.yaml 10-serviceaccount.yaml 20-rbac.yaml; do' "$WORK/block.code"
check 'the block never names optional/ or node-access (code, not comments)' \
  bash -c '! grep -Eiq "optional|node-access" "$1"' _ "$WORK/block.code"
check 'the block applies no ClusterRole / ClusterRoleBinding (code)' \
  bash -c '! grep -Eiq "clusterrole" "$1"' _ "$WORK/block.code"
check 'the block never mints or reads a token (code)' \
  bash -c '! grep -Eiq "create token|token" "$1"' _ "$WORK/block.code"
apply_lines=$(grep -F 'apply' "$WORK/block.code" | grep -Fv 'say ')
check 'the block applies by explicit file name: one apply, of "$HUB_DIR/$f", no glob, no -k, no -R'   test "$apply_lines" = '  $K apply -f "$HUB_DIR/$f"'
# Outside the block dev-up.sh must not touch k8s/hub at all (comments aside).
outside=$(awk -v b="$begin" -v e="$end" '$0 == b {skip=1} !skip {print} $0 == e {skip=0}' "$DEVUP" | grep -v -e '^[[:space:]]*#' -e '^unset ')
check 'nothing outside the block applies or names k8s/hub' \
  bash -c '! grep -Eq "/hub[/\" ]|HUB_DIR|studio-hub" <<<"$1"' _ "$outside"
for f in 00-namespace.yaml 10-serviceaccount.yaml 20-rbac.yaml; do
  check "k8s/hub/$f exists" test -f "$HERE/hub/$f"
done

# ------------------------------------------------------------------ fakes
mkdir -p "$WORK/bin"
for tool in docker k3d curl wget; do
  cat > "$WORK/bin/$tool" <<'FAKE'
#!/usr/bin/env bash
printf 'unexpected %s %s\n' "$(basename "$0")" "$*" >> "$GUARD"
exit 99
FAKE
  chmod +x "$WORK/bin/$tool"
done
# Answers only what the hub block asks:
#   apply -f <path>                      logged; exit APPLY_RC for the file named APPLY_FAILS
#   get role studio-hub -n <ns>          exit 1 for a namespace in ROLES_ABSENT, else 0
cat > "$WORK/bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
unexpected() { printf 'unexpected kubectl %s\n' "$*" >> "$GUARD"; exit 99; }
[ "${1-}" = --context ] && [ "${2-}" = does-not-exist ] || unexpected "$@"
shift 2
cmd="$*"
case "$cmd" in
"apply -f "*)
  path=${cmd#apply -f }
  echo "apply $path" >> "$TEST_LOG"
  [ "$(basename "$path")" = "${APPLY_FAILS:-}" ] && exit 1
  exit 0 ;;
"get role studio-hub -n "*)
  ns=${cmd##* }
  echo "get-role $ns" >> "$TEST_LOG"
  case " ${ROLES_ABSENT:-} " in *" $ns "*) exit 1 ;; esac ;;
*) unexpected "$@" ;;
esac
FAKE
chmod +x "$WORK/bin/kubectl"

export WORK PATH="$WORK/bin:$PATH" TEST_LOG="$WORK/log" GUARD="$WORK/guard"
cat > "$WORK/run_block.sh" <<'EOF'
set -euo pipefail
HERE="$REAL_HERE"
CTX=does-not-exist
K="kubectl --context $CTX"
GAMES_LIST=(${GAMES:-cuvara})
nk_cert_path="${NK_CERT:-}"
say() { printf '\n== %s\n' "$*"; }
source "$WORK/rendered_game_namespaces"
source "$WORK/block"
echo "REACHED-END"
EOF
export REAL_HERE="$HERE"
reset() { : > "$TEST_LOG"; : > "$GUARD"; unset GAMES NK_CERT ROLES_ABSENT APPLY_FAILS; }
run_block() { bash "$WORK/run_block.sh" > "$WORK/out" 2>&1; }
guard_clean() { if [ -s "$GUARD" ]; then bad "$1: fake saw an unexpected command"; cat "$GUARD"; else return 0; fi; }
seq_of() { sed "s|^apply $HERE/hub/|apply |" "$TEST_LOG" | tr '\n' ' '; }

# 1. cuvara alone: the three applies, in order, and no Role read
reset
if run_block; then ok 'cuvara alone: the block succeeds'; else bad 'cuvara alone: the block succeeds'; cat "$WORK/out"; fi
guard_clean 'cuvara alone'
check 'cuvara alone: applies 00, 10, 20 from k8s/hub, in that order, and nothing else' \
  test "$(seq_of)" = 'apply 00-namespace.yaml apply 10-serviceaccount.yaml apply 20-rbac.yaml '
check 'cuvara alone: reaches the end and says no ClusterRole, no token' \
  bash -c 'grep -Fxq REACHED-END "$1" && grep -Fq "no ClusterRole, no token" "$1"' _ "$WORK/out"
check 'cuvara alone: no warning' bash -c '! grep -Fq "::warning" "$1"' _ "$WORK/out"

# 2. games whose Role is present: the Role is only READ, never applied here
reset; GAMES='cuvara ember frost ember'; export GAMES
if run_block; then ok 'games with their Role: the block succeeds'; else bad 'games with their Role: the block succeeds'; cat "$WORK/out"; fi
guard_clean 'games with their Role'
check 'games: still only the three applies; one Role read per game namespace, duplicates folded' \
  test "$(seq_of)" = 'apply 00-namespace.yaml apply 10-serviceaccount.yaml apply 20-rbac.yaml get-role rpg-k8s-realtime-ember get-role rpg-k8s-realtime-frost '
check 'games: no warning when every Role is there' bash -c '! grep -Fq "::warning" "$1"' _ "$WORK/out"

# 3. a game namespace without the Role warns, names the render command, and continues
reset; GAMES='cuvara ember frost' ROLES_ABSENT='rpg-k8s-realtime-ember'; export GAMES ROLES_ABSENT
if run_block; then ok 'a game without the Role does not stop the deploy'; else bad 'a game without the Role does not stop the deploy'; cat "$WORK/out"; fi
check 'missing Role: annotation names the namespace' \
  grep -Fq '::warning title=Studio hub has no access to a game::rpg-k8s-realtime-ember has no Role studio-hub' "$WORK/out"
check 'missing Role: only that namespace is warned about' bash -c '! grep -Fq "rpg-k8s-realtime-frost has no Role" "$1"' _ "$WORK/out"
check 'missing Role: says verify FAILs on it' grep -Fq "verify's cluster.hub_rbac FAILs on it" "$WORK/out"
check 'missing Role: hint renders the game with TLS off and applies it' \
  grep -Fq 'python backend/deploy/k8s/games/render.py --game ember --nakama-tls off | kubectl --context does-not-exist apply -f -' "$WORK/out"
check 'missing Role: the block still reaches its end' grep -Fxq REACHED-END "$WORK/out"
reset; GAMES='cuvara ember' ROLES_ABSENT='rpg-k8s-realtime-ember' NK_CERT=/tls/tls.crt; export GAMES ROLES_ABSENT NK_CERT
run_block
check 'missing Role: hint renders with TLS on when Nakama has a certificate' grep -Fq -- '--game ember --nakama-tls on |' "$WORK/out"

# 4. a failing apply stops before the next file (set -e), and is not swallowed
reset; APPLY_FAILS=10-serviceaccount.yaml; export APPLY_FAILS
if ! run_block; then ok 'a failing apply stops the script'; else bad 'a failing apply stops the script'; fi
check 'failing apply: 20-rbac.yaml is not applied and the end is not reached' \
  bash -c '! grep -Fq 20-rbac.yaml "$1" && ! grep -Fxq REACHED-END "$2"' _ "$TEST_LOG" "$WORK/out"
guard_clean 'failing apply'

guard_clean 'last case'
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
