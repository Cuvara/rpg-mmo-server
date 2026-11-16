#!/usr/bin/env bash
# Offline secret-check regression cases for both deploy paths. All values are fake.
# Compose: only temporary deploy copies are used, so the real deploy/.env is
# never read or created. k8s: dev-up.sh imports images and applies namespaces
# before its secret checks, so it is never executed whole; only its read-only
# app-secret and join-keyring block (between the "# BEGIN app-secret and
# join-keyring checks" and "# END app-secret and join-keyring checks" lines, each
# required to occur exactly once) is extracted and run against a fake kubectl
# that answers only read-only reads on the nonexistent context
# k3d-rpg-secret-check-does-not-exist and exits 99 otherwise.
# Run: bash backend/deploy/k8s/verify/tests/secret_check_test.sh
set -uo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY="$(cd -- "$HERE/../../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); echo "ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "FAIL - $1"; }

mkdir -p "$WORK/deploy" "$WORK/bin"
cp "$DEPLOY/stack.sh" "$DEPLOY/.env.example" "$WORK/deploy/"
# Any unexpected external-service command fails and leaves evidence, even if
# the script swallows its exit status. No real executable can be reached.
for tool in docker kubectl curl wget k3d wslinfo; do
	cat > "$WORK/bin/$tool" <<'EOF'
#!/usr/bin/env bash
printf 'unexpected service command\n' >> "$SECRET_CHECK_GUARD_LOG"
exit 99
EOF
	chmod +x "$WORK/bin/$tool"
done

run_case() { # name expected-status env-file-lines...
	local name="$1" want="$2" rc=0
	shift 2
	printf '%s\n' "$@" > "$WORK/deploy/.env"
	: > "$WORK/guard.log"
	env -i PATH="$WORK/bin:$PATH" \
		RPG_FACTORY_WORKSPACE="${RPG_FACTORY_WORKSPACE:-}" \
		SECRET_CHECK_GUARD_LOG="$WORK/guard.log" \
		bash "$WORK/deploy/stack.sh" check-secrets > "$WORK/output" 2>&1 || rc=$?
	if [ "$rc" -eq "$want" ]; then ok "$name: status"; else bad "$name: status"; fi
	if grep -Fq -e test-jwt-a -e test-join-a -e test-join-b -e test-jwt-new -e test-jwt-old \
		-e dev-secret-change-me -e dev-join-secret-change-me "$WORK/output"; then
		bad "$name: secret appeared in stdout/stderr"
	else
		ok "$name: no secret in stdout/stderr"
	fi
	if [ ! -s "$WORK/guard.log" ]; then ok "$name: no service command"; else bad "$name: service command"; fi
}

run_case equal 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-jwt-a'
run_case distinct 0 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a'
run_case defaults 0 'unset JWT_SECRET JOIN_TOKEN_SECRET'
run_case empty-join-default 0 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET='
run_case empty-join-default-collision 1 'JWT_SECRET=dev-join-secret-change-me' 'JOIN_TOKEN_SECRET='
run_case unset-join-default-collision 1 'JWT_SECRET=dev-join-secret-change-me' 'unset JOIN_TOKEN_SECRET'
run_case empty-jwt-default 0 'JWT_SECRET=' 'JOIN_TOKEN_SECRET=test-join-a'
run_case empty-jwt-default-collision 1 'JWT_SECRET=' 'JOIN_TOKEN_SECRET=dev-secret-change-me'
run_case rotation-first 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-jwt-a,test-join-a'
run_case rotation-middle 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a,test-jwt-a,test-join-b'
run_case rotation-last 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a,test-jwt-a'
run_case rotation-trimmed 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET="test-join-a, test-jwt-a "'
run_case rotation-distinct 0 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a,test-join-b'
run_case per-game-equal 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a' 'JOIN_TOKEN_SECRET_FOO=test-jwt-a'
run_case per-game-empty 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a' 'JOIN_TOKEN_SECRET_FOO='
run_case per-game-rotation 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a' 'JOIN_TOKEN_SECRET_FOO=test-join-b,test-jwt-a'
run_case per-game-distinct 0 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=test-join-a' 'JOIN_TOKEN_SECRET_FOO=test-join-b'
run_case unusable-keyring 1 'JWT_SECRET=test-jwt-a' 'JOIN_TOKEN_SECRET=" , "'
# JWT_SECRET is a keyring at the gateway too: every join key is compared with
# every JWT key, not with the whole JWT_SECRET string.
run_case jwt-keyring-shared 1 'JWT_SECRET=test-jwt-new,test-jwt-old' 'JOIN_TOKEN_SECRET=test-jwt-old'
run_case jwt-keyring-distinct 0 'JWT_SECRET=test-jwt-new,test-jwt-old' 'JOIN_TOKEN_SECRET=test-join-a'
run_case jwt-keyring-trimmed 1 'JWT_SECRET=" test-jwt-new , test-jwt-old"' 'JOIN_TOKEN_SECRET=test-join-a,test-jwt-new'
run_case jwt-keyring-per-game 1 'JWT_SECRET=test-jwt-new,test-jwt-old' 'JOIN_TOKEN_SECRET=test-join-a' 'JOIN_TOKEN_SECRET_FOO=test-join-b,test-jwt-old'

# ------------------------------------------------------------------ k8s path
mkdir -p "$WORK/k8s-bin"
DEVUP="$DEPLOY/k8s/dev-up.sh"
BEGIN_MARK='# BEGIN app-secret and join-keyring checks'
END_MARK='# END app-secret and join-keyring checks'
# Each marker must occur exactly once, or the extraction could silently cover the
# wrong span (or nothing) and every case below would test something else.
if [ "$(grep -c "^$BEGIN_MARK" "$DEVUP")" -eq 1 ] && [ "$(grep -c "^$END_MARK" "$DEVUP")" -eq 1 ]; then
	ok "k8s: dev-up.sh has exactly one BEGIN and one END check marker"
else
	bad "k8s: dev-up.sh has exactly one BEGIN and one END check marker"
fi
awk -v b="$BEGIN_MARK" -v e="$END_MARK" \
	'index($0, b) == 1 { copy = 1 } copy { print } copy && index($0, e) == 1 { exit }' \
	"$DEVUP" > "$WORK/check.sh"
if grep -q "^$END_MARK" "$WORK/check.sh" \
	&& grep -q '^if ! \$K get secret rpg-app-secrets -n rpg-k8s-realtime ' "$WORK/check.sh" \
	&& grep -q '^nk_jwt=' "$WORK/check.sh" \
	&& grep -q '^if ! parse_games "\$gw_games"' "$WORK/check.sh" \
	&& grep -q '^if ! check_join_keyrings "\$gw_jwt"' "$WORK/check.sh" \
	&& grep -q 'keyrings_equal "\$(app_secret_key "\$game_ns" join-token-secret)"' "$WORK/check.sh"; then
	ok "k8s: check block extracted from dev-up.sh"
else
	bad "k8s: check block extracted from dev-up.sh"
fi
# The block is executed below; it must stay read-only.
if grep -Eq '\$K +(apply|create|delete|replace|patch|label|annotate|scale|rollout|exec|edit|set)( |$)' \
	"$WORK/check.sh"; then
	bad "k8s: check block is read-only"
else
	ok "k8s: check block is read-only"
fi
# Ordering guard (MG-02): the checks must run before the first workload apply, so a
# shared key is refused before any pod that would hold it exists. Only namespace
# applies may precede them.
begin_line=$(grep -n "^$BEGIN_MARK" "$DEVUP" | head -n 1 | cut -d: -f1)
end_line=$(grep -n "^$END_MARK" "$DEVUP" | head -n 1 | cut -d: -f1)
data_line=$(grep -nF 'apply -k "$HERE/data"' "$DEVUP" | head -n 1 | cut -d: -f1)
app_line=$(grep -nF '$K apply -f "$HERE/app/$f"' "$DEVUP" | head -n 1 | cut -d: -f1)
if [ -n "$begin_line" ] && [ -n "$end_line" ] && [ -n "$data_line" ] && [ -n "$app_line" ] \
	&& [ "$begin_line" -lt "$end_line" ] && [ "$end_line" -lt "$data_line" ] \
	&& [ "$end_line" -lt "$app_line" ]; then
	ok "k8s: check block runs before the first data-tier and app-tier apply"
else
	bad "k8s: check block runs before the first data-tier and app-tier apply"
fi
if [ -n "$begin_line" ] && ! head -n "$begin_line" "$DEVUP" | grep -E '\$K +apply' \
	| grep -vqE 'apply -f "\$HERE/(data/namespace|app/00-namespace)\.yaml"'; then
	ok "k8s: only namespaces are applied before the check block"
else
	bad "k8s: only namespaces are applied before the check block"
fi

# Answers only the check block's read-only reads on the throwaway context and logs
# (exit 99) anything else, so no cluster can be reached. Inputs, all fake:
#   FAKE_JWT                 jwt-secret (and nakama's JWT_SECRET unless FAKE_NK_JWT is set)
#   FAKE_JOIN                rpg-k8s-realtime join-token-secret (cuvara)
#   FAKE_GAMES               gateway-config's games; unset = ConfigMap absent
#   FAKE_JOIN_<GAME>         rpg-k8s-realtime join-token-secret-<game>; unset = key absent
#   FAKE_NS_JOIN_<GAME>      rpg-k8s-realtime-<game> join-token-secret; unset = Secret absent
#   FAKE_APP_SECRET_ABSENT   non-empty = rpg-k8s-realtime/rpg-app-secrets absent
cat > "$WORK/k8s-bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
ctx='--context k3d-rpg-secret-check-does-not-exist'
app='get secret rpg-app-secrets'
unexpected() {
	printf 'unexpected kubectl command\n' >> "$SECRET_CHECK_GUARD_LOG"
	exit 99
}
game_var() { # prefix game -> the FAKE_ variable name; unexpected on a non-game id
	case "$2" in
	'' | *[!abcdefghijklmnopqrstuvwxyz0123456789]*) unexpected ;;
	esac
	printf '%s%s' "$1" "$(printf '%s' "$2" | tr abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ)"
}
cmd="$*"
case "$cmd" in
"$ctx get secret nakama -n rpg-k8s-data -o jsonpath={.data.JWT_SECRET}")
	printf '%s' "${FAKE_NK_JWT-$FAKE_JWT}" | base64 ;;
"$ctx $app -n rpg-k8s-realtime")
	[ -z "${FAKE_APP_SECRET_ABSENT:-}" ] || exit 1 ;;
"$ctx $app -n rpg-k8s-realtime -o jsonpath={.data.jwt-secret}")
	printf '%s' "$FAKE_JWT" | base64 ;;
"$ctx $app -n rpg-k8s-realtime -o jsonpath={.data.join-token-secret}")
	printf '%s' "$FAKE_JOIN" | base64 ;;
"$ctx get configmap gateway-config -n rpg-k8s-realtime -o jsonpath={.data.games}")
	[ -n "${FAKE_GAMES+x}" ] || exit 1
	printf '%s' "$FAKE_GAMES" ;;
"$ctx $app -n rpg-k8s-realtime -o jsonpath={.data.join-token-secret-"*"}")
	game=${cmd##*join-token-secret-}
	var=$(game_var FAKE_JOIN_ "${game%\}}") || unexpected
	# A missing data key prints nothing and succeeds, as kubectl's jsonpath does.
	printf '%s' "${!var-}" | base64 ;;
"$ctx $app -n rpg-k8s-realtime-"*" -o jsonpath={.data.join-token-secret}")
	game=${cmd##*-n rpg-k8s-realtime-}
	var=$(game_var FAKE_NS_JOIN_ "${game%% *}") || unexpected
	[ -n "${!var+x}" ] || exit 1
	printf '%s' "${!var}" | base64 ;;
"$ctx $app -n rpg-k8s-realtime-"*)
	game=${cmd##*-n rpg-k8s-realtime-}
	var=$(game_var FAKE_NS_JOIN_ "$game") || unexpected
	[ -n "${!var+x}" ] || exit 1 ;;
*)
	unexpected ;;
esac
FAKE
chmod +x "$WORK/k8s-bin/kubectl"

run_k8s_case() { # name expected-status expected-output-substring-or-'' FAKE_*=value...
	local name="k8s $1" want="$2" expect="$3" rc=0 kv key
	local -a leak=(-e test-jwt-a -e test-join-a -e test-join-b -e test-jwt-new -e test-jwt-old)
	shift 3
	# Every key of every fake keyring this case passes must stay out of the output.
	for kv in "$@"; do
		case "$kv" in
		FAKE_GAMES=* | FAKE_APP_SECRET_ABSENT=*) ;;
		FAKE_*=*)
			for key in $(printf '%s' "${kv#*=}" | tr ',' ' '); do leak+=(-e "$key"); done ;;
		esac
	done
	: > "$WORK/guard.log"
	env -i PATH="$WORK/k8s-bin:$WORK/bin:$PATH" \
		SECRET_CHECK_GUARD_LOG="$WORK/guard.log" \
		K='kubectl --context k3d-rpg-secret-check-does-not-exist' \
		"$@" \
		bash -euo pipefail "$WORK/check.sh" > "$WORK/output" 2>&1 || rc=$?
	if [ "$rc" -eq "$want" ]; then
		ok "$name: status"
	else
		bad "$name: status (rc=$rc want=$want)"
		sed 's/^/    | /' "$WORK/output"
	fi
	if [ -n "$expect" ]; then
		if grep -Fq -- "$expect" "$WORK/output"; then ok "$name: output says '$expect'"; else bad "$name: output says '$expect'"; fi
	fi
	if grep -Fq "${leak[@]}" "$WORK/output"; then
		bad "$name: secret appeared in stdout/stderr"
	else
		ok "$name: no secret in stdout/stderr"
	fi
	if [ ! -s "$WORK/guard.log" ]; then ok "$name: no unexpected command"; else bad "$name: unexpected command"; fi
}

# cuvara only (gateway-config absent, the first-deploy case): jwt-secret vs join-token-secret.
run_k8s_case distinct 0 '' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a'
run_k8s_case equal 1 'join-token-secret shares a key with its jwt-secret' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-jwt-a'
run_k8s_case empty 1 'game cuvara' FAKE_JWT='test-jwt-a' FAKE_JOIN=''
run_k8s_case unusable-keyring 1 'game cuvara' FAKE_JWT='test-jwt-a' FAKE_JOIN=' , '
run_k8s_case rotation 1 '' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a,test-jwt-a'
run_k8s_case rotation-trimmed 1 '' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a, test-jwt-a '
run_k8s_case rotation-distinct 0 '' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a,test-join-b'
run_k8s_case jwt-keyring-shared 1 '' FAKE_JWT='test-jwt-new,test-jwt-old' FAKE_JOIN='test-jwt-old'
run_k8s_case jwt-keyring-distinct 0 '' FAKE_JWT='test-jwt-new,test-jwt-old' FAKE_JOIN='test-join-a'
run_k8s_case app-secret-absent 1 'rpg-k8s-realtime/rpg-app-secrets is absent' \
	FAKE_APP_SECRET_ABSENT=1 FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a'
run_k8s_case nakama-jwt-mismatch 1 "nakama's JWT_SECRET does not equal" \
	FAKE_NK_JWT='test-jwt-new' FAKE_JWT='test-jwt-a' FAKE_JOIN='test-join-a'

# Every game the gateway serves (MULTI-GAME-CROSS-REVIEW MG-02).
# The review's exact configuration: ember's join keyring holds the retiring auth key.
run_k8s_case review-ember-holds-auth-key 1 'game ember' \
	FAKE_JWT='auth-new,auth-old' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_JOIN_EMBER='join-ember,auth-old' FAKE_NS_JOIN_EMBER='join-ember,auth-old'
run_k8s_case games-share-cuvara-ember 1 'games cuvara and ember share a join key' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_JOIN_EMBER='join-ember,join-cuvara' FAKE_NS_JOIN_EMBER='join-ember,join-cuvara'
run_k8s_case games-share-ember-third 1 'games ember and third share a join key' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember,third' \
	FAKE_JOIN_EMBER='join-ember' FAKE_NS_JOIN_EMBER='join-ember' \
	FAKE_JOIN_THIRD='join-third,join-ember' FAKE_NS_JOIN_THIRD='join-third,join-ember'
run_k8s_case games-distinct 0 'cuvara, ember, third' \
	FAKE_JWT='auth-new,auth-old' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember,third' \
	FAKE_JOIN_EMBER='join-ember-new,join-ember' FAKE_NS_JOIN_EMBER=' join-ember-new , join-ember' \
	FAKE_JOIN_THIRD='join-third' FAKE_NS_JOIN_THIRD='join-third'
run_k8s_case game-join-missing 1 'join-token-secret-ember' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_NS_JOIN_EMBER='join-ember'
run_k8s_case game-ns-secret-absent 1 'rpg-k8s-realtime-ember/rpg-app-secrets is absent' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_JOIN_EMBER='join-ember'
run_k8s_case game-ns-secret-differs 1 'game ember' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_JOIN_EMBER='join-ember' FAKE_NS_JOIN_EMBER='join-ember-stale'
run_k8s_case game-ns-secret-reordered 1 'game ember' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,ember' \
	FAKE_JOIN_EMBER='join-ember-new,join-ember' FAKE_NS_JOIN_EMBER='join-ember,join-ember-new'
run_k8s_case games-invalid-entry 1 "games has an invalid entry" \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES='cuvara,Ember'
# No `games` means cuvara only: an unlisted game's keys are not read at all, so a
# leftover join-token-secret-ember that collides is not this deploy's concern.
run_k8s_case games-configmap-absent 0 'join keyrings of cuvara each' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_JOIN_EMBER='test-jwt-a'
run_k8s_case games-key-empty 0 'join keyrings of cuvara each' \
	FAKE_JWT='test-jwt-a' FAKE_JOIN='join-cuvara' FAKE_GAMES=''

echo
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
