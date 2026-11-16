#!/usr/bin/env bash
# Exercise only the extracted TLS blocks with a fake kubectl and throwaway context.
set -uo pipefail
HERE=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
PASS=0 FAIL=0
ok() { echo "ok - $*"; PASS=$((PASS+1)); }
bad() { echo "FAIL - $*"; FAIL=$((FAIL+1)); }
extract() {
  local name=$1 begin="# BEGIN $1" end="# END $1"
  if [ "$(grep -Fxc "$begin" "$HERE/dev-up.sh")" != 1 ] || [ "$(grep -Fxc "$end" "$HERE/dev-up.sh")" != 1 ]; then
    bad "$name markers must occur once"; return 1
  fi
  awk -v b="$begin" -v e="$end" '$0 == b {p=1} p {print} $0 == e {exit}' "$HERE/dev-up.sh" > "$WORK/$name"
  if ! grep -Fxq "$end" "$WORK/$name"; then bad "$name extraction"; return 1; fi
  ok "$name extraction"
}
extract 'game TLS repair' || exit 1
extract 'game TLS check' || exit 1
mkdir -p "$WORK/bin"
cat > "$WORK/bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
set -eu
[ "$1" = --context ] && [ "$2" = does-not-exist ] || exit 99
shift 2
cmd=$1; shift
case "$cmd" in
get)
  kind=$1 name=$2; shift 2
  [ "$1" = -n ] || exit 99
  ns=$2; shift 2
  if [ "$kind/$name/$ns" = configmap/nakama-config/rpg-k8s-data ]; then
    case "${2-}" in *tls-cert-path*|*tls-key-path*) [ "${TLS_ON:-0}" = 1 ] && printf '/tls/path';; *) exit 99;; esac
  elif [ "$kind/$name" = configmap/gameserver-config ]; then
    [ -f "$FIXTURE/$ns.url" ] || exit 1
    case "${2-}" in *nakama-url*) cat "$FIXTURE/$ns.url";; *nakama-tls-pin*) cat "$FIXTURE/$ns.pin";; '') :;; *) exit 99;; esac
  elif [ "$kind/$name" = secret/nakama-tls-pin ]; then
    [ -f "$FIXTURE/$ns.secret" ] || exit 1
    case "${2-}" in *metadata.name*) printf nakama-tls-pin;; *tls*crt*) base64 < "$FIXTURE/source.crt";; '') :;; *) exit 99;; esac
  elif [ "$kind/$name/$ns" = secret/nakama-tls/rpg-k8s-data ]; then
    printf nakama-tls
  else exit 99; fi ;;
patch)
  [ "$1 $2 $3" = 'configmap gameserver-config -n' ] || exit 99
  ns=$4; payload=$7
  printf 'patch %s\n' "$ns" >> "$LOG"
  url=${payload#*nakama-url\":\"}; url=${url%%\"*}
  printf '%s' "$url" > "$FIXTURE/$ns.url"
  printf '%s' /etc/nakama-tls/tls.crt > "$FIXTURE/$ns.pin" ;;
create)
  [ "$1 $2 $3 $4" = 'secret generic nakama-tls-pin -n' ] || exit 99
  printf 'create %s\n' "$5" >> "$LOG"
  cat >/dev/null
  printf '%s' "$5" ;;
apply)
  [ "$1 $2" = '-f -' ] || exit 99
  ns=$(cat); printf 'apply %s\n' "$ns" >> "$LOG"; : > "$FIXTURE/$ns.secret" ;;
*) exit 99;;
esac
FAKE
chmod +x "$WORK/bin/kubectl"
export PATH="$WORK/bin:$PATH" K='kubectl --context does-not-exist' FIXTURE="$WORK/fixture" LOG="$WORK/log"
mkdir "$FIXTURE"
setup() {
  rm -f "$FIXTURE"/*; : > "$LOG"
  for ns in rpg-k8s-realtime rpg-k8s-realtime-ember rpg-k8s-realtime-frost; do
    printf 'http://%s.example:7350' "$ns" > "$FIXTURE/$ns.url"
    : > "$FIXTURE/$ns.pin"
  done
  : > "$FIXTURE/rpg-k8s-realtime.secret"
  printf '%s\n' '-----BEGIN CERTIFICATE-----' 'fake-public-certificate' '-----END CERTIFICATE-----' > "$FIXTURE/source.crt"
}
run_blocks() {
  GAMES_LIST=(cuvara ember frost)
  source "$WORK/game TLS repair"
  source "$WORK/game TLS check"
}
setup
GAMES_LIST=(cuvara)
source <(sed -n '/^rendered_game_namespaces() {/,/^}/p' "$WORK/game TLS repair")
rendered_game_namespaces
if [ "${#GAME_NAMESPACES[@]}" -eq 0 ]; then ok 'cuvara has no rendered namespace'; else bad 'cuvara has no rendered namespace'; fi
GAMES_LIST=(cuvara ember frost ember)
rendered_game_namespaces
if [ "${GAME_NAMESPACES[*]}" = 'rpg-k8s-realtime-ember rpg-k8s-realtime-frost' ]; then ok 'game namespaces fold duplicates'; else bad 'game namespaces fold duplicates'; fi
TLS_ON=1; export TLS_ON
if (run_blocks) > "$WORK/out" 2>&1; then ok 'TLS on succeeds'; else bad 'TLS on succeeds'; fi
for ns in rpg-k8s-realtime rpg-k8s-realtime-ember rpg-k8s-realtime-frost; do
  if grep -Fxq "patch $ns" "$LOG" && grep -q '^https://' "$FIXTURE/$ns.url" && [ "$(cat "$FIXTURE/$ns.pin")" = /etc/nakama-tls/tls.crt ]; then ok "$ns repaired"; else bad "$ns repaired"; fi
done
for ns in rpg-k8s-realtime-ember rpg-k8s-realtime-frost; do
  if grep -Fxq "create $ns" "$LOG" && grep -Fxq "apply $ns" "$LOG" && [ -f "$FIXTURE/$ns.secret" ]; then ok "$ns pin provisioned"; else bad "$ns pin provisioned"; fi
done
setup; TLS_ON=0
if (run_blocks) > "$WORK/out" 2>&1 && [ ! -s "$LOG" ]; then ok 'TLS off does not patch'; else bad 'TLS off does not patch'; fi
printf stray > "$FIXTURE/rpg-k8s-realtime-ember.pin"
if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'rpg-k8s-realtime-ember' "$WORK/out"; then ok 'TLS off rejects game pin'; else bad 'TLS off rejects game pin'; fi
setup; printf 'https://ember.example:7350' > "$FIXTURE/rpg-k8s-realtime-ember.url"
if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'rpg-k8s-realtime-ember' "$WORK/out"; then ok 'TLS off rejects game https'; else bad 'TLS off rejects game https'; fi
setup; TLS_ON=1; rm "$FIXTURE/rpg-k8s-realtime.secret"
if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'ERROR:' "$WORK/out"; then ok 'missing source pin refused'; else bad 'missing source pin refused'; fi
for source in empty non-PEM; do
  setup
  : > "$FIXTURE/source.crt"
  [ "$source" = empty ] || printf '%s\n' 'fake-invalid-certificate-value' > "$FIXTURE/source.crt"
  if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'ERROR:.*source.*tls.crt' "$WORK/out" \
    && ! grep -Eq '^(create|apply) ' "$LOG" \
    && ! grep -Fq 'fake-invalid-certificate-value' "$WORK/out"; then
    ok "$source source certificate refused without provisioning or value output"
  else
    bad "$source source certificate refused without provisioning or value output"
  fi
done
for key_type in '' 'RSA ' 'ENCRYPTED '; do
  setup
  printf '%s\n' "-----BEGIN ${key_type}PRIVATE KEY-----" 'fake-private-key-value' "-----END ${key_type}PRIVATE KEY-----" >> "$FIXTURE/source.crt"
  if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'ERROR:.*source.*tls.crt' "$WORK/out" \
    && ! grep -Eq '^(create|apply) ' "$LOG" \
    && ! grep -Eq 'fake-private-key-value|BEGIN .*PRIVATE KEY' "$WORK/out"; then
    ok "${key_type}private key bundled with source certificate refused without provisioning or value output"
  else
    bad "${key_type}private key bundled with source certificate refused without provisioning or value output"
  fi
done
setup; rm "$FIXTURE/rpg-k8s-realtime-ember.url"
if ! (run_blocks) > "$WORK/out" 2>&1 && grep -q 'render.py --game ember --nakama-tls' "$WORK/out"; then ok 'missing game config refused'; else bad 'missing game config refused'; fi
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
