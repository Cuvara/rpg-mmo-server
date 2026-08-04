#!/usr/bin/env bash
# Shared helpers for the k3s/Agones dev scripts.
#
# The main job of this file is to paper over the WSL2 quirks documented in
# docs/K3S.md:
#
#   * `kubectl` may be a Linux binary (talks to any cluster whose kubeconfig is
#     reachable from Linux) or `kubectl.exe` (Docker Desktop's Windows binary).
#   * kubectl.exe CANNOT read Linux paths (/mnt/... is fine for *Windows* only
#     via the drive letter, and /home/... is invisible). So every local manifest
#     is piped through stdin (`kubectl apply -f -`), which works for both.
#   * URLs are passed straight through — both binaries fetch those themselves.
#
# Source this file; do not execute it.

set -euo pipefail

# ---------------------------------------------------------------------------
# kubectl resolution
# ---------------------------------------------------------------------------

# KUBECTL_BIN can be forced by the caller (e.g. KUBECTL_BIN=microk8s.kubectl).
: "${KUBECTL_BIN:=}"

# resolve_kubectl picks a working kubectl: an explicit override, then the Linux
# binary, then Docker Desktop's kubectl.exe.
resolve_kubectl() {
  if [[ -n "$KUBECTL_BIN" ]]; then
    command -v "$KUBECTL_BIN" >/dev/null 2>&1 || die "KUBECTL_BIN=$KUBECTL_BIN not found in PATH"
    return
  fi

  local candidates=(
    kubectl
    kubectl.exe
    "/mnt/c/Program Files/Docker/Docker/resources/bin/kubectl.exe"
  )
  # Prefer a binary that actually has a kube context: on WSL the Linux kubectl
  # often exists with an EMPTY kubeconfig while kubectl.exe holds the
  # docker-desktop context (they read different kubeconfig files).
  local c
  for c in "${candidates[@]}"; do
    if command -v "$c" >/dev/null 2>&1 &&
      [[ -n "$("$c" config current-context 2>/dev/null)" ]]; then
      KUBECTL_BIN="$c"
      return
    fi
  done
  # Fall back to the first binary that exists (fail-fast messaging downstream).
  for c in "${candidates[@]}"; do
    if command -v "$c" >/dev/null 2>&1; then
      KUBECTL_BIN="$c"
      return
    fi
  done

  die "no kubectl found (tried: ${candidates[*]})"
}

# kubectl_is_exe reports whether the resolved binary is the Windows one, which
# means local file paths must be piped via stdin.
kubectl_is_exe() {
  [[ "$KUBECTL_BIN" == *.exe ]]
}

# kube runs kubectl with the resolved binary.
kube() {
  "$KUBECTL_BIN" "$@"
}

# kube_apply_file applies a LOCAL manifest path. Always via stdin so the same
# call works for kubectl and kubectl.exe.
kube_apply_file() {
  local file="$1"; shift
  [[ -f "$file" ]] || die "manifest not found: $file"
  kube apply -f - "$@" <"$file"
}

# kube_delete_file deletes a LOCAL manifest path, ignoring "not found".
kube_delete_file() {
  local file="$1"; shift
  [[ -f "$file" ]] || return 0
  kube delete -f - --ignore-not-found "$@" <"$file"
}

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

log()  { printf '\033[0;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[0;33m[warn]\033[0m %s\n' "$*" >&2; }
ok()   { printf '\033[0;32m[ok]\033[0m %s\n' "$*"; }
die()  { printf '\033[0;31m[fail]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Cluster reachability
# ---------------------------------------------------------------------------

# require_cluster fails with an actionable message when no cluster answers.
# This is THE failure mode on a fresh WSL2 box: Docker Desktop ships kubectl but
# Kubernetes itself is off, so the kubeconfig has zero contexts.
#
# Order matters. The context check is offline and instant; going straight to a
# connectivity probe with an empty kubeconfig makes kubectl fall back to
# http://localhost:8080 and burn ~25s on discovery retries before failing.
require_cluster() {
  local ctx
  if ctx="$(kube config current-context 2>/dev/null)" && [[ -n "$ctx" ]]; then
    # /readyz is a single request — no API discovery, no retry storm.
    if kube get --raw=/readyz --request-timeout=10s >/dev/null 2>&1; then
      ok "cluster reachable (kubectl=$KUBECTL_BIN, context=$ctx)"
      return 0
    fi
    die "context '$ctx' is set but the API server did not answer /readyz within 10s — is the cluster running?"
  fi

  cat >&2 <<'EOF'
[fail] no Kubernetes cluster reachable.

`kubectl config get-contexts` is empty, which on this machine means Docker
Desktop's Kubernetes is disabled. Enable it once (single human step):

    Docker Desktop -> Settings -> Kubernetes -> "Enable Kubernetes" -> Apply & restart

Then re-run this script. See backend/deploy/docs/K3S.md for the alternatives
(k3d, or a real k3s VPS) and why they are not usable here.
EOF
  exit 1
}

# ---------------------------------------------------------------------------
# Waiting
# ---------------------------------------------------------------------------

# retry <attempts> <sleep_seconds> <cmd...> — run cmd until it succeeds.
retry() {
  local attempts="$1" delay="$2"; shift 2
  local i=1
  while true; do
    if "$@"; then return 0; fi
    (( i >= attempts )) && return 1
    sleep "$delay"
    i=$(( i + 1 ))
  done
}

# wait_for <timeout_seconds> <description> <cmd...> — poll cmd until success.
wait_for() {
  local timeout="$1" desc="$2"; shift 2
  local waited=0
  log "waiting for $desc (timeout ${timeout}s)"
  while ! "$@" >/dev/null 2>&1; do
    if (( waited >= timeout )); then
      die "timed out after ${timeout}s waiting for $desc"
    fi
    sleep 5
    waited=$(( waited + 5 ))
  done
  ok "$desc"
}
