#!/usr/bin/env bash
# Offline regression cases for the Studio hub blocks of k8s/dev-up.sh.
#
# dev-up.sh imports images and applies namespaces, so it is never executed whole. Only
# the spans between "# BEGIN studio hub rbac apply" / "# END studio hub rbac apply" and
# "# BEGIN studio hub preflight" / "# END studio hub preflight" (each marker required to
# occur once) are extracted and run against a fake kubectl on PATH, on the nonexistent
# context "does-not-exist": the fake answers only the reads and applies the blocks make
# and logs (exit 99) anything else, so no cluster is reachable.
#
# What it shows:
#   - the apply block applies EXACTLY k8s/hub/{00-namespace,10-serviceaccount,
#     15-admission-policy,20-rbac}.yaml, by name, in that order (the policy BEFORE the
#     Role that grants `patch deployments`), and nothing else: never optional/, never a
#     glob, no ClusterRole, no token;
#   - it sits after the app and data tiers are applied and before the final report;
#   - a failing apply stops the script before the next file, and a failing POLICY apply
#     stops it without applying the Role (an error, never a warning);
#   - the preflight sits BEFORE the data and app tiers are applied, only reads, and stops
#     the script (exit 1, nothing applied) when the API does not serve
#     ValidatingAdmissionPolicy (Kubernetes older than 1.30) or when a listed non-default
#     game has no Role studio-hub (rendered before the hub existed), naming the render command;
#   - in the apply block a game namespace without its Role still only warns.
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
PRE_NAME='studio hub preflight'
pre_begin="# BEGIN $PRE_NAME" pre_end="# END $PRE_NAME"
for marker in "$begin" "$end" "$pre_begin" "$pre_end"; do
  if [ "$(grep -Fxc "$marker" "$DEVUP")" != 1 ]; then
    bad "marker '$marker' must occur exactly once"; exit 1
  fi
done
awk -v b="$begin" -v e="$end" '$0 == b {p=1} p {print} $0 == e {exit}' "$DEVUP" > "$WORK/block"
grep -Fxq "$end" "$WORK/block" || { bad "block extraction"; exit 1; }
awk -v b="$pre_begin" -v e="$pre_end" '$0 == b {p=1} p {print} $0 == e {exit}' "$DEVUP" > "$WORK/pre"
grep -Fxq "$pre_end" "$WORK/pre" || { bad "preflight extraction"; exit 1; }
ok "block extraction"
# The block calls this helper, which dev-up.sh defines inside the game TLS repair block.
sed -n '/^rendered_game_namespaces() {/,/^}/p' "$DEVUP" > "$WORK/rendered_game_namespaces"
grep -q '^rendered_game_namespaces() {' "$WORK/rendered_game_namespaces" || { bad "rendered_game_namespaces extraction"; exit 1; }
# The code of the block: comments and echo lines stripped, because those name what it must not do.
grep -v -e '^[[:space:]]*#' -e '^[[:space:]]*echo ' "$WORK/block" > "$WORK/block.code"
grep -v -e '^[[:space:]]*#' -e '^[[:space:]]*echo ' "$WORK/pre" > "$WORK/pre.code"

# ------------------------------------------------------------------ placement
line_of() { grep -nFx -- "$1" "$DEVUP" | head -n 1 | cut -d: -f1; }
b=$(line_of "$begin"); e=$(line_of "$end")
app_apply=$(grep -nF 'say "apply the app tier (rpg-k8s-realtime)"' "$DEVUP" | head -n 1 | cut -d: -f1)
data_apply=$(grep -nF 'say "apply the data tier (rpg-k8s-data)"' "$DEVUP" | head -n 1 | cut -d: -f1)
state_line=$(line_of 'say "state"')
check 'block sits after the data tier and the app tier are applied' \
  test -n "$b" -a -n "$app_apply" -a -n "$data_apply" -a "$b" -gt "$app_apply" -a "$b" -gt "$data_apply"
check 'block ends before the final state report' test -n "$e" -a -n "$state_line" -a "$e" -lt "$state_line"
pb=$(line_of "$pre_begin"); pe=$(line_of "$pre_end")
join_end=$(line_of '# END app-secret and join-keyring checks')
check 'preflight sits after the join-keyring checks (GAMES_LIST is parsed) and BEFORE the data tier is applied' \
  test -n "$pb" -a -n "$join_end" -a "$pb" -gt "$join_end" -a "$pe" -lt "$data_apply" -a "$pe" -lt "$app_apply"
check 'preflight is read-only: no apply, create, delete, patch, label or annotate in its code' \
  bash -c '! grep -Eq "apply|create|delete|patch|label|annotate|replace" "$1"' _ "$WORK/pre.code"

# ------------------------------------------------------------------ static rules
check 'dev-up.sh has no kubectl call with a leading-slash argument (get --raw /apis/... is rewritten by Git Bash)' \
  bash -c '! grep -v "^[[:space:]]*#" "$1" | grep -Eq -- "--raw[ =]/|get[[:space:]]+/"' _ "$DEVUP"
check 'the block names exactly the four files, in order, the policy before the Role' \
  grep -Fxq 'for f in 00-namespace.yaml 10-serviceaccount.yaml 15-admission-policy.yaml 20-rbac.yaml; do' "$WORK/block.code"
check 'the block never names optional/ or node-access (code, not comments)' \
  bash -c '! grep -Eiq "optional|node-access" "$1"' _ "$WORK/block.code"
check 'the block applies no ClusterRole / ClusterRoleBinding (code)' \
  bash -c '! grep -Eiq "clusterrole" "$1"' _ "$WORK/block.code"
check 'the block never mints or reads a token (code)' \
  bash -c '! grep -Eiq "create token|token" "$1"' _ "$WORK/block.code"
apply_lines=$(grep -F 'apply' "$WORK/block.code" | grep -Fv 'say ')
check 'the block applies by explicit file name: one apply, of "$HUB_DIR/$f", no glob, no -k, no -R'   test "$apply_lines" = '  if ! $K apply -f "$HUB_DIR/$f"; then'
check 'a failed apply exits 1 inside the block (an error, not a warning)' \
  bash -c 'grep -Fxq "    exit 1" "$1"' _ "$WORK/block.code"
# Outside the two blocks dev-up.sh must not touch k8s/hub at all (comments aside).
outside=$(awk -v b="$begin" -v e="$end" -v pb="$pre_begin" -v pe="$pre_end" '$0 == b || $0 == pb {skip=1} !skip {print} $0 == e || $0 == pe {skip=0}' "$DEVUP" | grep -v -e '^[[:space:]]*#' -e '^unset ')
check 'nothing outside the two blocks applies or names k8s/hub' \
  bash -c '! grep -Eq "/hub[/\" ]|HUB_DIR|studio-hub" <<<"$1"' _ "$outside"
for f in 00-namespace.yaml 10-serviceaccount.yaml 15-admission-policy.yaml 20-rbac.yaml; do
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
#   api-resources --api-group=admissionregistration.k8s.io -o name   the two resource names, or nothing when NO_VAP_API is set
#   ANY argument that starts with "/" and is not a file that exists: answered the way kubectl.exe answers
#       it under Git Bash on Windows, where MSYS rewrites such an argument into "C:/Program Files/Git/..."
#       (NotFound, exit 1) and the call is logged as "mangled". `kubectl get --raw /apis/...` is that call.
#   get configmap nakama-config ...      NK_CERT (the tls-cert-path value)
cat > "$WORK/bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
unexpected() { printf 'unexpected kubectl %s\n' "$*" >> "$GUARD"; exit 99; }
[ "${1-}" = --context ] && [ "${2-}" = does-not-exist ] || unexpected "$@"
shift 2
# MSYS path conversion (Git Bash on Windows): an argument that starts with "/" and is not an existing file is
# rewritten into a Windows path before kubectl.exe sees it, so the API answers NotFound for it. Model that, so a
# probe written as `get --raw /apis/...` is a false "the API does not serve it" here, as it was on the live QA.
for a in "$@"; do
  case "$a" in
  /*) [ -e "$a" ] || { echo "mangled $a" >> "$TEST_LOG"
        echo "Error from server (NotFound): the server could not find the requested resource (path C:/Program Files/Git$a)" >&2
        exit 1; } ;;
  esac
done
cmd="$*"
case "$cmd" in
"apply -f "*)
  path=${cmd#apply -f }
  echo "apply $path" >> "$TEST_LOG"
  [ "$(basename "$path")" = "${APPLY_FAILS:-}" ] && exit 1
  exit 0 ;;
"api-resources --api-group=admissionregistration.k8s.io -o name")
  echo "api-resources-vap" >> "$TEST_LOG"
  # what a 1.35 cluster prints for this group (the live QA): the webhook kinds, then both policy kinds
  [ -z "${NO_VAP_API:-}" ] && printf '%s\n' mutatingwebhookconfigurations.admissionregistration.k8s.io \
    validatingadmissionpolicies.admissionregistration.k8s.io validatingadmissionpolicybindings.admissionregistration.k8s.io \
    validatingwebhookconfigurations.admissionregistration.k8s.io
  # a cluster that does not serve the group prints nothing and still exits 0
  [ -n "${NO_VAP_API:-}" ] && [ -n "${ONLY_VAP_KIND:-}" ] && printf '%s\n' "$ONLY_VAP_KIND"
  exit 0 ;;
"get configmap nakama-config -n rpg-k8s-data -o jsonpath={.data.tls-cert-path}")
  echo "get-nakama-config" >> "$TEST_LOG"
  printf '%s' "${NK_CERT:-}"
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
reset() { : > "$TEST_LOG"; : > "$GUARD"; unset GAMES NK_CERT ROLES_ABSENT APPLY_FAILS NO_VAP_API ONLY_VAP_KIND; }
run_block() { bash "$WORK/run_block.sh" > "$WORK/out" 2>&1; }
sed -e 's|source "$WORK/block"|source "$WORK/pre"|' "$WORK/run_block.sh" | grep -v rendered_game_namespaces > "$WORK/run_pre.sh"
run_pre() { bash "$WORK/run_pre.sh" > "$WORK/out" 2>&1; }
guard_clean() { if [ -s "$GUARD" ]; then bad "$1: fake saw an unexpected command"; cat "$GUARD"; else return 0; fi; }
seq_of() { sed "s|^apply $HERE/hub/|apply |" "$TEST_LOG" | tr '\n' ' '; }

# 1. cuvara alone: the three applies, in order, and no Role read
reset
if run_block; then ok 'cuvara alone: the block succeeds'; else bad 'cuvara alone: the block succeeds'; cat "$WORK/out"; fi
guard_clean 'cuvara alone'
check 'cuvara alone: applies 00, 10, 15, 20 from k8s/hub, in that order (policy before the Role), and nothing else' \
  test "$(seq_of)" = 'apply 00-namespace.yaml apply 10-serviceaccount.yaml apply 15-admission-policy.yaml apply 20-rbac.yaml '
check 'cuvara alone: reaches the end and says no ClusterRole, no token' \
  bash -c 'grep -Fxq REACHED-END "$1" && grep -Fq "no ClusterRole, no token" "$1"' _ "$WORK/out"
check 'cuvara alone: no warning' bash -c '! grep -Fq "::warning" "$1"' _ "$WORK/out"

# 2. games whose Role is present: the Role is only READ, never applied here
reset; GAMES='cuvara ember frost ember'; export GAMES
if run_block; then ok 'games with their Role: the block succeeds'; else bad 'games with their Role: the block succeeds'; cat "$WORK/out"; fi
guard_clean 'games with their Role'
check 'games: still only the four applies; one Role read per game namespace, duplicates folded' \
  test "$(seq_of)" = 'apply 00-namespace.yaml apply 10-serviceaccount.yaml apply 15-admission-policy.yaml apply 20-rbac.yaml get-role rpg-k8s-realtime-ember get-role rpg-k8s-realtime-frost '
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

# 5. the policy cannot be applied: the Role is NOT applied and the script stops with an ERROR
reset; APPLY_FAILS=15-admission-policy.yaml; export APPLY_FAILS
if ! run_block; then ok 'a failing policy apply stops the script'; else bad 'a failing policy apply stops the script'; fi
check 'failing policy apply: 20-rbac.yaml is not applied, the end is not reached' \
  bash -c '! grep -Fq 20-rbac.yaml "$1" && ! grep -Fxq REACHED-END "$2"' _ "$TEST_LOG" "$WORK/out"
check 'failing policy apply: the error names the policy, the Role it holds back and Kubernetes 1.30' \
  bash -c 'grep -Fq "ERROR: could not apply k8s/hub/15-admission-policy.yaml" "$1" && grep -Fq "20-rbac.yaml is NOT applied" "$1" && grep -Fq "1.30" "$1"' _ "$WORK/out"
check 'failing policy apply: it is an error, not a ::warning' bash -c '! grep -Fq "::warning" "$1"' _ "$WORK/out"
guard_clean 'failing policy apply'

# 6. preflight: reads only, before anything is applied
reset
if run_pre; then ok 'preflight, cuvara alone: passes'; else bad 'preflight, cuvara alone: passes'; cat "$WORK/out"; fi
guard_clean 'preflight, cuvara alone'
check 'preflight, cuvara alone: reads the API resource list and Nakama TLS, applies nothing, reads no Role' \
  bash -c '[ "$(tr "\n" " " < "$1")" = "api-resources-vap get-nakama-config " ]' _ "$TEST_LOG"
check 'preflight, cuvara alone: no kubectl argument is a path that Git Bash would rewrite (the "mangled" log is empty)' \
  bash -c '! grep -q "^mangled" "$1"' _ "$TEST_LOG"
check 'preflight, cuvara alone: reaches the end' grep -Fxq REACHED-END "$WORK/out"

reset; GAMES='cuvara ember frost'; export GAMES
if run_pre; then ok 'preflight, games with their Role: passes'; else bad 'preflight, games with their Role: passes'; cat "$WORK/out"; fi
check 'preflight, games with their Role: one Role read per game, no apply' \
  bash -c 'grep -Fxq "get-role rpg-k8s-realtime-ember" "$1" && grep -Fxq "get-role rpg-k8s-realtime-frost" "$1" && ! grep -Fq apply "$1"' _ "$TEST_LOG"

# 7. preflight: a cluster that cannot serve the policy stops the deploy before anything changes
reset; NO_VAP_API=1; export NO_VAP_API
if ! run_pre; then ok 'preflight, no ValidatingAdmissionPolicy API: the script stops'; else bad 'preflight, no ValidatingAdmissionPolicy API: the script stops'; fi
check 'preflight, no policy API: names the API, Kubernetes 1.30 and that nothing was changed' \
  bash -c 'grep -Fq "does not serve admissionregistration.k8s.io/v1 ValidatingAdmissionPolicy" "$1" && grep -Fq "1.30" "$1" && grep -Fq "Nothing was changed" "$1"' _ "$WORK/out"
check 'preflight, no policy API: it is an ERROR and nothing was applied' \
  bash -c 'grep -Fq "ERROR:" "$1" && ! grep -Fq apply "$2" && ! grep -Fxq REACHED-END "$1"' _ "$WORK/out" "$TEST_LOG"
guard_clean 'preflight, no policy API'

# 7b. the group is served but only ONE of the two kinds: still a stop (both are needed)
reset; NO_VAP_API=1 ONLY_VAP_KIND=validatingadmissionpolicies.admissionregistration.k8s.io; export NO_VAP_API ONLY_VAP_KIND
if ! run_pre; then ok 'preflight, only the policy kind served (no binding kind): the script stops'; else bad 'preflight, only the policy kind served (no binding kind): the script stops'; fi
# 7c. a similar name is not the kind: matched whole-line, not as a substring
reset; NO_VAP_API=1 ONLY_VAP_KIND=xvalidatingadmissionpolicies.admissionregistration.k8s.io; export NO_VAP_API ONLY_VAP_KIND
if ! run_pre; then ok 'preflight, a resource that merely contains the name: the script stops'; else bad 'preflight, a resource that merely contains the name: the script stops'; fi

# 8. preflight: a game rendered before the hub existed has no Role: stop, name each game, give the command
reset; GAMES='cuvara ember frost' ROLES_ABSENT='rpg-k8s-realtime-ember rpg-k8s-realtime-frost'; export GAMES ROLES_ABSENT
if ! run_pre; then ok 'preflight, games without a Role: the script stops'; else bad 'preflight, games without a Role: the script stops'; fi
check 'preflight, games without a Role: BOTH games are named, with the render command (TLS off)' \
  bash -c 'grep -Fq "game ember is listed" "$1" && grep -Fq "game frost is listed" "$1" && grep -Fq "render.py --game ember --nakama-tls off | kubectl --context does-not-exist apply -f -" "$1" && grep -Fq "render.py --game frost --nakama-tls off" "$1"' _ "$WORK/out"
check 'preflight, games without a Role: nothing was applied and the end is not reached' \
  bash -c '! grep -Fq apply "$2" && ! grep -Fxq REACHED-END "$1"' _ "$WORK/out" "$TEST_LOG"
reset; GAMES='cuvara ember' ROLES_ABSENT='rpg-k8s-realtime-ember' NK_CERT=/tls/tls.crt; export GAMES ROLES_ABSENT NK_CERT
run_pre || true
check 'preflight, games without a Role: the hint renders with TLS on when Nakama has a certificate' \
  grep -Fq -- '--game ember --nakama-tls on |' "$WORK/out"
reset; GAMES='cuvara ember' ROLES_ABSENT='rpg-k8s-realtime-ember'; export GAMES ROLES_ABSENT
run_pre || true
check 'preflight, a missing Role: tells the operator to read games/README.md first (the apply re-applies fleet and quota)' \
  grep -Fq 'games/README.md' "$WORK/out"
guard_clean 'preflight, games without a Role'

guard_clean 'last case'
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
