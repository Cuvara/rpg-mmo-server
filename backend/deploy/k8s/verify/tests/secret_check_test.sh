#!/usr/bin/env bash
# Offline compose secret-check regression cases: only temporary deploy copies
# are used, so the real deploy/.env is never read or created. All values are fake.
# dev-up.sh applies resources before reaching its secret checks; do not execute
# it here. The k8s half is covered by the documented isolated, fake-kubectl dry
# run under Secrets in k8s/data/README.md, which runs only the read-only checks.
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
	if grep -Fq -e test-jwt-a -e test-join-a -e test-join-b \
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

echo
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
