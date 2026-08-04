#!/usr/bin/env bash
#
# build-all.sh — vet + test + build every Go module in the repo.
#
# Works both locally (WSL, where `docker` may only exist as `docker.exe`) and in
# CI (ubuntu-latest). Fails fast on the first error.
#
# Usage:
#   scripts/build-all.sh                 # vet + test + build all modules
#   scripts/build-all.sh --skip-tests    # vet + build only (fast compile check)
#   scripts/build-all.sh --race          # add -race to go test (needs gcc/cgo)
#   scripts/build-all.sh --plugin        # also build nakama.so via docker
#   scripts/build-all.sh --images        # also build gateway+gameserver images
#
# Outputs:
#   bin/gateway, bin/gameserver, bin/smoketest
#   backend/deploy/modules/nakama.so     (only with --plugin)
#   $IMAGE_PREFIX/{gateway,gameserver}:$IMAGE_TAG docker images (only with --images)
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

BIN_DIR="$REPO_ROOT/bin"
DEPLOY_DIR="$REPO_ROOT/backend/deploy"
PLUGIN_OUT="$DEPLOY_DIR/modules"
NAKAMA_VERSION="${NAKAMA_VERSION:-3.40.0}"

SKIP_TESTS=0
WITH_PLUGIN=0
WITH_IMAGES=0
IMAGE_PREFIX="${IMAGE_PREFIX:-rpg-mmo}"
IMAGE_TAG="${IMAGE_TAG:-dev}"
RACE=0
TEST_TIMEOUT="${TEST_TIMEOUT:-120s}"

# ---------------------------------------------------------------- arg parsing
for arg in "$@"; do
	case "$arg" in
	--skip-tests) SKIP_TESTS=1 ;;
	--plugin) WITH_PLUGIN=1 ;;
	--images) WITH_IMAGES=1 ;;
	--race) RACE=1 ;;
	-h | --help)
		sed -n '2,18p' "${BASH_SOURCE[0]}"
		exit 0
		;;
	*)
		echo "ERROR: unknown flag: $arg (try --help)" >&2
		exit 2
		;;
	esac
done

# ---------------------------------------------------------------- log helpers
step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() {
	printf '\033[1;31mFAIL: %s\033[0m\n' "$*" >&2
	exit 1
}

# ------------------------------------------------------------- toolchain: go
# Prefer PATH, fall back to the common GOPATH install location (WSL setups
# frequently have go installed there but not exported in non-login shells).
detect_go() {
	if command -v go >/dev/null 2>&1; then
		command -v go
		return 0
	fi
	if [ -x "$HOME/go/bin/go" ]; then
		echo "$HOME/go/bin/go"
		return 0
	fi
	if [ -x /usr/local/go/bin/go ]; then
		echo /usr/local/go/bin/go
		return 0
	fi
	return 1
}

GO="$(detect_go)" || fail "go toolchain not found (PATH, \$HOME/go/bin, /usr/local/go/bin)"

# --------------------------------------------------------- toolchain: docker
# WSL: the Linux `docker` CLI may be absent while Docker Desktop exposes
# `docker.exe` on PATH. Try both.
detect_docker() {
	if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
		echo docker
		return 0
	fi
	if command -v docker.exe >/dev/null 2>&1 && docker.exe info >/dev/null 2>&1; then
		echo docker.exe
		return 0
	fi
	return 1
}

# ------------------------------------------------------------------ go flags
TEST_FLAGS=(-timeout "$TEST_TIMEOUT")
if [ "$RACE" -eq 1 ]; then
	# -race requires cgo + a C toolchain; plain WSL images often lack gcc.
	TEST_FLAGS+=(-race)
fi

# Modules that get vet + test + compile. Each is an independent go module.
# Format: "<relative path>|<build target or ->|<output binary name or ->"
MODULES=(
	"backend/shared|-|-"
	"backend/gateway|./cmd/gateway/|gateway"
	"backend/gameserver|./cmd/gameserver/|gameserver"
	"backend/nakama|./...|-"
	"backend/smoketest|./cmd/smoketest/|smoketest"
)

step "Toolchain"
info "repo:   $REPO_ROOT"
info "go:     $GO ($("$GO" version))"
info "tests:  $([ "$SKIP_TESTS" -eq 1 ] && echo skipped || echo "enabled (race=$RACE, timeout=$TEST_TIMEOUT)")"
info "plugin: $([ "$WITH_PLUGIN" -eq 1 ] && echo requested || echo off)"
info "images: $([ "$WITH_IMAGES" -eq 1 ] && echo "requested ($IMAGE_PREFIX/*:$IMAGE_TAG)" || echo off)"

mkdir -p "$BIN_DIR"

for entry in "${MODULES[@]}"; do
	IFS='|' read -r mod_path build_target bin_name <<<"$entry"
	mod_dir="$REPO_ROOT/$mod_path"
	[ -f "$mod_dir/go.mod" ] || fail "$mod_path: no go.mod"

	step "Module: $mod_path"

	info "go vet ./..."
	(cd "$mod_dir" && "$GO" vet ./...) || fail "$mod_path: go vet"

	if [ "$SKIP_TESTS" -eq 0 ]; then
		info "go test ${TEST_FLAGS[*]} ./..."
		(cd "$mod_dir" && "$GO" test "${TEST_FLAGS[@]}" ./...) || fail "$mod_path: go test"
	fi

	if [ "$build_target" != "-" ]; then
		if [ "$bin_name" != "-" ]; then
			info "go build -o bin/$bin_name $build_target"
			(cd "$mod_dir" && "$GO" build -o "$BIN_DIR/$bin_name" "$build_target") ||
				fail "$mod_path: go build"
		else
			# Compile check only (e.g. the nakama plugin package — the real
			# artifact is built with -buildmode=plugin inside docker).
			info "go build $build_target (compile check, no output)"
			(cd "$mod_dir" && "$GO" build -o /dev/null "$build_target") ||
				fail "$mod_path: go build"
		fi
	fi
done

# ------------------------------------------------------------- integration_test
if [ "$SKIP_TESTS" -eq 0 ]; then
	step "Module: backend/integration_test (E2E)"
	info "go test ${TEST_FLAGS[*]} ./..."
	(cd "$REPO_ROOT/backend/integration_test" && "$GO" test -v "${TEST_FLAGS[@]}" ./...) ||
		fail "backend/integration_test: go test"
else
	step "Module: backend/integration_test — skipped (--skip-tests)"
fi

# ------------------------------------------------------------- nakama plugin
if [ "$WITH_PLUGIN" -eq 1 ]; then
	step "Nakama plugin (nakama.so)"
	if DOCKER="$(detect_docker)"; then
		info "docker cli: $DOCKER"
		mkdir -p "$PLUGIN_OUT"
		# Build context MUST be backend/ — backend/nakama/go.mod has
		# `replace ... => ../shared`. The `export` stage emits nakama.so only.
		# Run from DEPLOY_DIR with relative paths: docker.exe (Windows CLI via
		# WSL interop) cannot resolve absolute /mnt/* paths, but cwd-relative
		# paths translate correctly.
		( cd "$DEPLOY_DIR" && DOCKER_BUILDKIT=1 "$DOCKER" build \
			-f nakama-plugin.Dockerfile \
			--build-arg "NAKAMA_VERSION=$NAKAMA_VERSION" \
			--target export \
			--output type=local,dest=modules \
			.. ) || fail "nakama plugin: docker build"
		[ -f "$PLUGIN_OUT/nakama.so" ] || fail "nakama plugin: nakama.so not produced"
		info "built: $PLUGIN_OUT/nakama.so"
		ls -lh "$PLUGIN_OUT/nakama.so"
	else
		fail "--plugin requested but no working docker CLI found (tried 'docker', 'docker.exe')"
	fi
fi

# --------------------------------------------------- gateway/gameserver images
if [ "$WITH_IMAGES" -eq 1 ]; then
	step "Container images (gateway, gameserver)"
	if DOCKER="$(detect_docker)"; then
		info "docker cli: $DOCKER"
		for svc in gateway gameserver; do
			img="$IMAGE_PREFIX/$svc:$IMAGE_TAG"
			info "building $img"
			# Context MUST be backend/ (go.mod `replace ... => ../shared`).
			# Run from DEPLOY_DIR with relative paths: docker.exe (Windows CLI
			# via WSL interop) cannot resolve absolute /mnt/* paths.
			( cd "$DEPLOY_DIR" && DOCKER_BUILDKIT=1 "$DOCKER" build \
				-f "docker/Dockerfile.$svc" \
				-t "$img" \
				.. ) || fail "$svc image: docker build"
			"$DOCKER" image inspect "$img" >/dev/null 2>&1 || fail "$svc image: $img not produced"
		done
		"$DOCKER" images --format '{{.Repository}}:{{.Tag}} {{.Size}}' | grep "^$IMAGE_PREFIX/" || true
	else
		fail "--images requested but no working docker CLI found (tried 'docker', 'docker.exe')"
	fi
fi

step "Done"
info "binaries in $BIN_DIR:"
ls -lh "$BIN_DIR" 2>/dev/null || true
