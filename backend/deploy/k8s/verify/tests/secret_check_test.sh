#!/usr/bin/env bash
# Offline secret-check regression cases for both deploy paths. All values are fake.
# Compose: only temporary deploy copies are used, so the real deploy/.env is
# never read or created. k8s: dev-up.sh applies resources before reaching its
# secret checks, so it is never executed whole; only its read-only JWT/join
# check block is extracted (the same awk markers as the dry run under Secrets in
# k8s/data/README.md) and run against a fake kubectl that answers only the
# nonexistent context k3d-rpg-secret-check-does-not-exist and exits 99 otherwise.
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
awk '/^nk_jwt=/ { copy=1 } copy { print }
     /^echo "checked: rpg-app-secrets.*join-token-secret/ { exit }' \
	"$DEPLOY/k8s/dev-up.sh" > "$WORK/check.sh"
if grep -q '^echo "checked: rpg-app-secrets.*join-token-secret' "$WORK/check.sh" \
	&& grep -q '^join_secret=' "$WORK/check.sh"; then
	ok "k8s: check block extracted from dev-up.sh"
else
	bad "k8s: check block extracted from dev-up.sh"
fi
# Answers only the three read-only Secret reads on the throwaway context and
# logs (exit 99) anything else, so no cluster can be reached.
cat > "$WORK/k8s-bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
ctx='--context k3d-rpg-secret-check-does-not-exist'
case "$*" in
"$ctx get secret nakama -n rpg-k8s-data -o jsonpath={.data.JWT_SECRET}" | \
"$ctx get secret rpg-app-secrets -n rpg-k8s-realtime -o jsonpath={.data.jwt-secret}")
	printf '%s' "$FAKE_JWT" | base64 ;;
"$ctx get secret rpg-app-secrets -n rpg-k8s-realtime -o jsonpath={.data.join-token-secret}")
	printf '%s' "$FAKE_JOIN" | base64 ;;
*)
	printf 'unexpected kubectl command\n' >> "$SECRET_CHECK_GUARD_LOG"
	exit 99 ;;
esac
FAKE
chmod +x "$WORK/k8s-bin/kubectl"

run_k8s_case() { # name expected-status jwt-secret join-token-secret
	local name="k8s $1" want="$2" rc=0
	: > "$WORK/guard.log"
	env -i PATH="$WORK/k8s-bin:$WORK/bin:$PATH" \
		FAKE_JWT="$3" FAKE_JOIN="$4" \
		SECRET_CHECK_GUARD_LOG="$WORK/guard.log" \
		K='kubectl --context k3d-rpg-secret-check-does-not-exist' \
		bash -euo pipefail "$WORK/check.sh" > "$WORK/output" 2>&1 || rc=$?
	if [ "$rc" -eq "$want" ]; then ok "$name: status"; else bad "$name: status (rc=$rc want=$want)"; fi
	if grep -Fq -e test-jwt-a -e test-join-a -e test-join-b -e test-jwt-new -e test-jwt-old \
		"$WORK/output"; then
		bad "$name: secret appeared in stdout/stderr"
	else
		ok "$name: no secret in stdout/stderr"
	fi
	if [ ! -s "$WORK/guard.log" ]; then ok "$name: no unexpected command"; else bad "$name: unexpected command"; fi
}

run_k8s_case distinct 0 'test-jwt-a' 'test-join-a'
run_k8s_case equal 1 'test-jwt-a' 'test-jwt-a'
run_k8s_case empty 1 'test-jwt-a' ''
run_k8s_case unusable-keyring 1 'test-jwt-a' ' , '
run_k8s_case rotation 1 'test-jwt-a' 'test-join-a,test-jwt-a'
run_k8s_case rotation-trimmed 1 'test-jwt-a' 'test-join-a, test-jwt-a '
run_k8s_case rotation-distinct 0 'test-jwt-a' 'test-join-a,test-join-b'
run_k8s_case jwt-keyring-shared 1 'test-jwt-new,test-jwt-old' 'test-jwt-old'
run_k8s_case jwt-keyring-distinct 0 'test-jwt-new,test-jwt-old' 'test-join-a'

echo
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
