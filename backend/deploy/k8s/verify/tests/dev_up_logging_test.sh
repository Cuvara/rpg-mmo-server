#!/usr/bin/env bash
# Offline regression cases for the two log-shipping blocks of k8s/dev-up.sh.
#
# dev-up.sh imports images and applies namespaces, so it is never executed whole.
# Only two spans are extracted, each between a "# BEGIN <name>" and a "# END <name>"
# line that must occur exactly once:
#   - "deploy environment"   maps the kube context (or DEPLOY_ENVIRONMENT) to the
#                            `env` label the collector ships, and stops on a value
#                            it cannot tell or that is odd;
#   - "log collector apply"  applies k8s/logging/, restarts the DaemonSet when a
#                            ConfigMap changed, warns about a game namespace
#                            without the Role, attaches Loki, waits for the rollout.
# They run against a fake kubectl and a fake docker on PATH, on the nonexistent
# context "does-not-exist": the fake answers only the reads and writes the block
# makes and logs (exit 99) anything else, so no cluster or container is reachable.
# connect-loki.sh is replaced by a fake in a copy of the logging directory, and the
# real manifests are copied next to it so the alloy-env substitution is exercised
# against the real 30-alloy-env.yaml.
# Run: bash backend/deploy/k8s/verify/tests/dev_up_logging_test.sh
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

extract() {
  local name=$1 begin="# BEGIN $1" end="# END $1"
  if [ "$(grep -Fxc "$begin" "$DEVUP")" != 1 ] || [ "$(grep -Fxc "$end" "$DEVUP")" != 1 ]; then
    bad "$name markers must occur exactly once"; return 1
  fi
  awk -v b="$begin" -v e="$end" '$0 == b {p=1} p {print} $0 == e {exit}' "$DEVUP" > "$WORK/$name"
  if ! grep -Fxq "$end" "$WORK/$name"; then bad "$name extraction"; return 1; fi
  ok "$name extraction"
}
extract 'deploy environment' || exit 1
extract 'log collector apply' || exit 1
# The collector block calls this helper, which dev-up.sh defines inside the game
# TLS repair block (not extracted whole: that block patches ConfigMaps).
sed -n '/^rendered_game_namespaces() {/,/^}/p' "$DEVUP" > "$WORK/rendered_game_namespaces"
if ! grep -q '^rendered_game_namespaces() {' "$WORK/rendered_game_namespaces"; then
  bad "rendered_game_namespaces extraction"; exit 1
fi

# ------------------------------------------------------------------ placement
line_of() { grep -nFx -- "$1" "$DEVUP" | head -n 1 | cut -d: -f1; }
secret_end=$(line_of '# END app-secret and join-keyring checks')
env_begin=$(line_of '# BEGIN deploy environment')
env_end=$(line_of '# END deploy environment')
col_begin=$(line_of '# BEGIN log collector apply')
col_end=$(line_of '# END log collector apply')
first_apply=$(grep -nF '$K apply' "$DEVUP" | head -n 1 | cut -d: -f1)
state_line=$(grep -nFx 'say "state"' "$DEVUP" | head -n 1 | cut -d: -f1)
check 'collector block sits after the END of the app-secret and join-keyring checks' \
  test -n "$secret_end" -a -n "$col_begin" -a "$col_begin" -gt "$secret_end"
check 'collector block ends before the final state report' \
  test -n "$col_end" -a -n "$state_line" -a "$col_end" -lt "$state_line"
check 'deploy environment is resolved before the first apply' \
  test -n "$env_end" -a -n "$first_apply" -a "$env_begin" -lt "$env_end" -a "$env_end" -lt "$first_apply"
# Reads and writes the block makes on its own must stay out of the extraction.
check 'collector block does not call the game TLS repair markers' \
  bash -c '! grep -q "BEGIN game TLS" "$1"' _ "$WORK/log collector apply"

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
# Answers only what the collector block asks, from state files in $FIXTURE:
#   ds                 file present = daemonset/alloy exists
#   cfg_rv, env_rv     resourceVersion of alloy-config / alloy-env (file absent = no ConfigMap)
# and from the case environment:
#   CFG_CHANGES=1      applying 40-alloy-config.yaml bumps alloy-config's resourceVersion
#   ENV_CHANGES=1      applying alloy-env (stdin) bumps alloy-env's resourceVersion
#   ROLLOUT_RC         exit status of `rollout status` (default 0)
#   ROLES_ABSENT       space-separated namespaces without Role rpg-log-collector
cat > "$WORK/bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
unexpected() { printf 'unexpected kubectl %s\n' "$*" >> "$GUARD"; exit 99; }
[ "${1-}" = --context ] && [ "${2-}" = does-not-exist ] || unexpected "$@"
shift 2
cmd="$*"
bump() { printf '%s' "$(( $(cat "$1" 2>/dev/null || echo 100) + 1 ))" > "$1"; }
case "$cmd" in
"get daemonset alloy -n rpg-k8s-logging -o jsonpath={.metadata.name}")
  [ -f "$FIXTURE/ds" ] || exit 1
  printf alloy ;;
"get configmap alloy-config -n rpg-k8s-logging -o jsonpath={.metadata.resourceVersion}")
  [ -f "$FIXTURE/cfg_rv" ] || exit 1
  cat "$FIXTURE/cfg_rv" ;;
"get configmap alloy-env -n rpg-k8s-logging -o jsonpath={.metadata.resourceVersion}")
  [ -f "$FIXTURE/env_rv" ] || exit 1
  cat "$FIXTURE/env_rv" ;;
"apply -f -")
  cat > "$FIXTURE/env.applied"
  echo "apply -" >> "$TEST_LOG"
  [ "${ENV_CHANGES:-0}" != 1 ] || bump "$FIXTURE/env_rv" ;;
"apply -f "*)
  f=$(basename "${cmd#apply -f }")
  echo "apply $f" >> "$TEST_LOG"
  if [ "$f" = 40-alloy-config.yaml ] && [ "${CFG_CHANGES:-0}" = 1 ]; then bump "$FIXTURE/cfg_rv"; fi ;;
"rollout restart daemonset/alloy -n rpg-k8s-logging")
  echo restart >> "$TEST_LOG" ;;
"rollout status -n rpg-k8s-logging daemonset/alloy --timeout=180s")
  echo rollout-status >> "$TEST_LOG"
  exit "${ROLLOUT_RC:-0}" ;;
"logs -n rpg-k8s-logging daemonset/alloy --tail=20")
  echo logs >> "$TEST_LOG"
  echo 'alloy: fake last log line' ;;
"get role rpg-log-collector -n "*)
  ns=${cmd##* }
  echo "get-role $ns" >> "$TEST_LOG"
  case " ${ROLES_ABSENT:-} " in *" $ns "*) exit 1 ;; esac ;;
*) unexpected "$@" ;;
esac
FAKE
chmod +x "$WORK/bin/kubectl"

# A copy of k8s/ whose logging/ holds the real manifests and a fake connect-loki.sh.
mkdir -p "$WORK/k8s/logging"
cp "$HERE"/logging/*.yaml "$WORK/k8s/logging/"
cat > "$WORK/k8s/logging/connect-loki.sh" <<'FAKE'
#!/usr/bin/env bash
echo "connect-loki ctx=${KUBE_CONTEXT-} env=${DEPLOY_ENVIRONMENT-}" >> "$TEST_LOG"
exit "${CONNECT_RC:-0}"
FAKE

export WORK PATH="$WORK/bin:$PATH" FIXTURE="$WORK/fixture" TEST_LOG="$WORK/log" GUARD="$WORK/guard"

# ------------------------------------------------------------------ deploy environment
# run_env <context> -> $WORK/out; prints "ENV=<value>" when the block lets it through.
run_env() {
  cat > "$WORK/run_env.sh" <<'EOF'
set -euo pipefail
CTX=$1
source "$WORK/deploy environment"
echo "ENV=$DEPLOY_ENV"
EOF
  bash "$WORK/run_env.sh" "$1" > "$WORK/out" 2>&1
}
env_case() { # name context deploy-environment-or-unset expected-ENV
  local name=$1 ctx=$2 override=$3 want=$4
  unset DEPLOY_ENVIRONMENT
  [ "$override" = UNSET ] || export DEPLOY_ENVIRONMENT="$override"
  if run_env "$ctx" && grep -Fxq "ENV=$want" "$WORK/out"; then ok "$name"; else bad "$name"; cat "$WORK/out"; fi
  unset DEPLOY_ENVIRONMENT
}
env_refused() { # name context deploy-environment-or-unset expected-message-substring
  local name=$1 ctx=$2 override=$3 needle=$4
  unset DEPLOY_ENVIRONMENT
  [ "$override" = UNSET ] || export DEPLOY_ENVIRONMENT="$override"
  if ! run_env "$ctx" && grep -Fq "$needle" "$WORK/out" && ! grep -q '^ENV=' "$WORK/out"; then ok "$name"; else bad "$name"; cat "$WORK/out"; fi
  unset DEPLOY_ENVIRONMENT
}
env_case 'k3d-rpg-dev is dev' k3d-rpg-dev UNSET dev
env_case 'k3d-rpg-stg is staging' k3d-rpg-stg UNSET staging
env_case 'DEPLOY_ENVIRONMENT wins over the dev context' k3d-rpg-dev production production
env_case 'DEPLOY_ENVIRONMENT wins over the staging context' k3d-rpg-stg dev dev
env_case 'DEPLOY_ENVIRONMENT names an unknown context' does-not-exist production production
env_case 'DEPLOY_ENVIRONMENT may carry . _ -' does-not-exist 'stg-1.eu_west' 'stg-1.eu_west'
env_case 'empty DEPLOY_ENVIRONMENT falls back to the context' k3d-rpg-dev '' dev
env_refused 'unknown context without DEPLOY_ENVIRONMENT stops' does-not-exist UNSET "kube context 'does-not-exist'"
env_refused 'unknown context names DEPLOY_ENVIRONMENT as the way out' does-not-exist UNSET 'Set DEPLOY_ENVIRONMENT'
env_refused 'empty DEPLOY_ENVIRONMENT on an unknown context stops' does-not-exist '' "kube context 'does-not-exist'"
for odd in 'a b' 'x;y' 'a/b' 'a"b' 'a$b' 'prod
x'; do
  env_refused "odd DEPLOY_ENVIRONMENT '$(printf %s "$odd" | tr '\n' '~')' stops on a known context" k3d-rpg-dev "$odd" 'must match [A-Za-z0-9_.-]+'
done
env_refused 'odd DEPLOY_ENVIRONMENT stops on an unknown context' does-not-exist 'a b' 'must match [A-Za-z0-9_.-]+'

# ------------------------------------------------------------------ collector block
# Per case: CASE_ENV, GAMES (space-separated game ids), NK_CERT (non-empty = TLS on).
cat > "$WORK/run_col.sh" <<'EOF'
set -euo pipefail
HERE="$WORK/k8s"
CTX=does-not-exist
K="kubectl --context $CTX"
DEPLOY_ENV="${CASE_ENV:-dev}"
GAMES_LIST=(${GAMES:-cuvara})
nk_cert_path="${NK_CERT:-}"
say() { printf '\n== %s\n' "$*"; }
source "$WORK/rendered_game_namespaces"
source "$WORK/log collector apply"
echo "REACHED-END"
EOF
reset() {
  rm -rf "$FIXTURE"; mkdir -p "$FIXTURE"
  : > "$TEST_LOG"; : > "$GUARD"
  printf 100 > "$FIXTURE/cfg_rv"; printf 100 > "$FIXTURE/env_rv"; : > "$FIXTURE/ds"
  unset CFG_CHANGES ENV_CHANGES ROLLOUT_RC ROLES_ABSENT CONNECT_RC CASE_ENV GAMES NK_CERT LOG_ROLLOUT_TIMEOUT
}
run_col() { bash "$WORK/run_col.sh" > "$WORK/out" 2>&1; }
guard_clean() { # name: the fakes saw no command they do not answer
  if [ -s "$GUARD" ]; then bad "$1: fake saw an unexpected command"; cat "$GUARD"; else return 0; fi
}
applies() { grep '^apply ' "$TEST_LOG" | sed 's/^apply //' | tr '\n' ' '; }
seq_of() { tr '\n' ' ' < "$TEST_LOG"; }
env_yaml_line() { grep -Fx "  $1: \"$2\"" "$FIXTURE/env.applied" >/dev/null 2>&1; }

# 1. apply order, alloy-env contents, no restart when nothing changed
reset; GAMES='cuvara ember frost' CASE_ENV=staging
export GAMES CASE_ENV
if run_col; then ok 'collector block succeeds on a clean run'; else bad 'collector block succeeds on a clean run'; cat "$WORK/out"; fi
guard_clean 'clean run'
check 'collector applies 00,10,20, alloy-env (stdin), then 40,50,60 in that order' \
  test "$(applies)" = '00-namespace.yaml 10-serviceaccount.yaml 20-rbac.yaml - 40-alloy-config.yaml 50-daemonset.yaml 60-loki-service.yaml '
check 'alloy-env carries the resolved environment' env_yaml_line deploy-environment staging
check 'alloy-env lists the fixed namespaces and every game namespace' \
  env_yaml_line log-namespaces 'rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging,rpg-k8s-realtime-ember,rpg-k8s-realtime-frost'
check 'alloy-env keeps the rest of 30-alloy-env.yaml (name, namespace)' \
  bash -c 'grep -Fxq "  name: alloy-env" "$1" && grep -Fxq "  namespace: rpg-k8s-logging" "$1"' _ "$FIXTURE/env.applied"
check 'unchanged ConfigMaps: no restart' bash -c '! grep -Fxq restart "$1"' _ "$TEST_LOG"
check 'unchanged ConfigMaps: says so' grep -Fq 'unchanged (or the collector is new); no restart' "$WORK/out"
check 'clean run: waits for the rollout, then reports the collected namespaces' \
  bash -c 'grep -Fxq rollout-status "$1" && grep -Fxq "checked: the log collector is Ready, collecting: rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging,rpg-k8s-realtime-ember,rpg-k8s-realtime-frost (env=staging)" "$2"' _ "$TEST_LOG" "$WORK/out"
check 'clean run: reaches the end of the block' grep -Fxq REACHED-END "$WORK/out"
check 'clean run: full call order (applies, Role reads, Loki attach, rollout)' \
  test "$(seq_of)" = 'apply 00-namespace.yaml apply 10-serviceaccount.yaml apply 20-rbac.yaml apply - apply 40-alloy-config.yaml apply 50-daemonset.yaml apply 60-loki-service.yaml get-role rpg-k8s-realtime-ember get-role rpg-k8s-realtime-frost connect-loki ctx=does-not-exist env=staging rollout-status '

# 2. cuvara alone: the fixed three namespaces only; duplicates fold
reset; GAMES='cuvara' CASE_ENV=dev; export GAMES CASE_ENV
run_col
check 'cuvara alone: log-namespaces is the three fixed namespaces' \
  env_yaml_line log-namespaces 'rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging'
check 'cuvara alone: no Role read for a game namespace' bash -c '! grep -q "^get-role" "$1"' _ "$TEST_LOG"
reset; GAMES='cuvara ember frost ember'; export GAMES
run_col
check 'duplicate game folds to one namespace' \
  env_yaml_line log-namespaces 'rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging,rpg-k8s-realtime-ember,rpg-k8s-realtime-frost'
reset; run_col
check 'GAMES unset in the case: the default game list is cuvara only' \
  env_yaml_line log-namespaces 'rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging'
reset; CASE_ENV=production; export CASE_ENV; run_col
check 'environment from the deploy environment block reaches alloy-env' env_yaml_line deploy-environment production

# 3. restart only when a ConfigMap resourceVersion changed AND the DaemonSet existed
restart_case() { # name expect(restart|none) ds(yes|no) cfg(0|1) env(0|1)
  reset
  [ "$3" = yes ] || rm -f "$FIXTURE/ds"
  CFG_CHANGES=$4 ENV_CHANGES=$5; export CFG_CHANGES ENV_CHANGES
  if ! run_col; then bad "$1: block succeeds"; cat "$WORK/out"; return; fi
  guard_clean "$1"
  if [ "$2" = restart ]; then
    if grep -Fxq restart "$TEST_LOG" && grep -Fq 'changed; restarting the collector' "$WORK/out" \
      && [ "$(grep -c '^restart$' "$TEST_LOG")" = 1 ] \
      && [ "$(grep -nFx restart "$TEST_LOG" | cut -d: -f1)" -lt "$(grep -nFx rollout-status "$TEST_LOG" | cut -d: -f1)" ]; then
      ok "$1"
    else bad "$1"; fi
  else
    if ! grep -Fxq restart "$TEST_LOG" && grep -Fq 'no restart' "$WORK/out"; then ok "$1"; else bad "$1"; fi
  fi
}
restart_case 'alloy-config changed, DaemonSet existed: restarts once, before the rollout wait' restart yes 1 0
restart_case 'alloy-env changed, DaemonSet existed: restarts' restart yes 0 1
restart_case 'both changed, DaemonSet existed: restarts once' restart yes 1 1
restart_case 'nothing changed, DaemonSet existed: no restart' none yes 0 0
restart_case 'ConfigMaps changed, DaemonSet new: no restart' none no 1 1
restart_case 'nothing changed, DaemonSet new: no restart' none no 0 0
# First deploy: the ConfigMaps did not exist before either.
reset; rm -f "$FIXTURE/ds" "$FIXTURE/cfg_rv" "$FIXTURE/env_rv"; CFG_CHANGES=1 ENV_CHANGES=1; export CFG_CHANGES ENV_CHANGES
if run_col && ! grep -Fxq restart "$TEST_LOG"; then ok 'first deploy (nothing existed): no restart'; else bad 'first deploy (nothing existed): no restart'; cat "$WORK/out"; fi
# The DaemonSet existed, the ConfigMaps did not (partial state): a created ConfigMap is a change.
reset; rm -f "$FIXTURE/cfg_rv" "$FIXTURE/env_rv"; CFG_CHANGES=1 ENV_CHANGES=1; export CFG_CHANGES ENV_CHANGES
if run_col && grep -Fxq restart "$TEST_LOG"; then ok 'DaemonSet existed, ConfigMaps newly created: restarts'; else bad 'DaemonSet existed, ConfigMaps newly created: restarts'; fi

# 4. connect-loki.sh failing warns and does not stop
reset; CONNECT_RC=3; export CONNECT_RC
if run_col; then ok 'failing connect-loki.sh does not stop the deploy'; else bad 'failing connect-loki.sh does not stop the deploy'; cat "$WORK/out"; fi
check 'failing connect-loki.sh prints the warning annotation' grep -Fq '::warning title=Loki not attached::' "$WORK/out"
check 'failing connect-loki.sh prints the re-run hint' grep -Fq 'WARN: connect-loki.sh failed; re-run it: KUBE_CONTEXT=does-not-exist DEPLOY_ENVIRONMENT=dev' "$WORK/out"
check 'failing connect-loki.sh: the rollout is still waited for and reported' \
  bash -c 'grep -Fxq rollout-status "$1" && grep -Fq "checked: the log collector is Ready" "$2" && grep -Fxq REACHED-END "$2"' _ "$TEST_LOG" "$WORK/out"
check 'connect-loki.sh gets the context and the resolved environment' grep -Fxq 'connect-loki ctx=does-not-exist env=dev' "$TEST_LOG"
reset; run_col
check 'passing connect-loki.sh prints no Loki warning' bash -c '! grep -Fq "Loki not attached" "$1"' _ "$WORK/out"

# 5. a failed rollout exits non-zero
reset; ROLLOUT_RC=1; export ROLLOUT_RC
if ! run_col; then ok 'failed rollout exits non-zero'; else bad 'failed rollout exits non-zero'; fi
check 'failed rollout: says the collector did not become Ready' grep -Fq 'ERROR: the log collector (daemonset/alloy in rpg-k8s-logging) did not become Ready within 180s' "$WORK/out"
check 'failed rollout: shows the collector log tail' bash -c 'grep -Fxq logs "$1" && grep -Fq "alloy: fake last log line" "$2"' _ "$TEST_LOG" "$WORK/out"
check 'failed rollout: the Ready line is not printed and the block does not finish' \
  bash -c '! grep -Fq "checked: the log collector is Ready" "$1" && ! grep -Fxq REACHED-END "$1"' _ "$WORK/out"
reset; ROLLOUT_RC=1 LOG_ROLLOUT_TIMEOUT=60s; export ROLLOUT_RC LOG_ROLLOUT_TIMEOUT
# the fake answers only the default timeout, so a changed one is refused (exit 99) and logged
run_col || true
check 'LOG_ROLLOUT_TIMEOUT is passed through to rollout status' grep -Fq 'unexpected kubectl rollout status -n rpg-k8s-logging daemonset/alloy --timeout=60s' "$GUARD"
: > "$GUARD"

# 6. a game namespace without the Role warns and continues
reset; GAMES='cuvara ember frost' ROLES_ABSENT='rpg-k8s-realtime-ember'; export GAMES ROLES_ABSENT
if run_col; then ok 'game namespace without the Role does not stop the deploy'; else bad 'game namespace without the Role does not stop the deploy'; cat "$WORK/out"; fi
check 'missing Role: warning annotation names the namespace' \
  grep -Fq '::warning title=Game logs not collected::rpg-k8s-realtime-ember has no Role rpg-log-collector' "$WORK/out"
check 'missing Role: only the namespace without the Role is warned about' \
  bash -c '! grep -Fq "rpg-k8s-realtime-frost has no Role" "$1"' _ "$WORK/out"
check 'missing Role: hint renders the game with TLS off' \
  grep -Fq 'python backend/deploy/k8s/games/render.py --game ember --nakama-tls off | kubectl --context does-not-exist apply -f -' "$WORK/out"
check 'missing Role: the other game is still checked, Loki attached, rollout waited for' \
  bash -c 'grep -Fxq "get-role rpg-k8s-realtime-frost" "$1" && grep -q "^connect-loki" "$1" && grep -Fxq rollout-status "$1" && grep -Fxq REACHED-END "$2"' _ "$TEST_LOG" "$WORK/out"
reset; GAMES='cuvara ember' ROLES_ABSENT='rpg-k8s-realtime-ember' NK_CERT=/tls/tls.crt; export GAMES ROLES_ABSENT NK_CERT
run_col
check 'missing Role: hint renders the game with TLS on when Nakama has a certificate' grep -Fq -- '--game ember --nakama-tls on |' "$WORK/out"
reset; GAMES='cuvara ember frost' ROLES_ABSENT='rpg-k8s-realtime-ember rpg-k8s-realtime-frost'; export GAMES ROLES_ABSENT
run_col
check 'every game namespace without the Role is warned about' \
  bash -c '[ "$(grep -c "^::warning title=Game logs not collected" "$1")" = 2 ]' _ "$WORK/out"
reset; GAMES='cuvara ember'; export GAMES
run_col
check 'game namespace with the Role: no Role warning' bash -c '! grep -Fq "has no Role" "$1"' _ "$WORK/out"

# 7. a reworded alloy-env manifest is refused before anything past it is applied
reset
cp "$WORK/k8s/logging/30-alloy-env.yaml" "$WORK/30-alloy-env.orig"
sed -i 's/^  deploy-environment: /  deployEnvironment: /' "$WORK/k8s/logging/30-alloy-env.yaml"
if ! run_col; then ok 'reworded alloy-env manifest stops the deploy'; else bad 'reworded alloy-env manifest stops the deploy'; fi
check 'reworded alloy-env manifest: error says it refuses to apply it' grep -Fq 'lost its deploy-environment / log-namespaces lines; refusing to apply it' "$WORK/out"
check 'reworded alloy-env manifest: neither alloy-env nor the Alloy workload is applied' \
  bash -c '! grep -Eq "^apply (-|40|50|60)" "$1"' _ "$TEST_LOG"
cp "$WORK/30-alloy-env.orig" "$WORK/k8s/logging/30-alloy-env.yaml"

guard_clean 'last case'
# Every case ran with the fakes only: kubectl on does-not-exist, no docker.
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
