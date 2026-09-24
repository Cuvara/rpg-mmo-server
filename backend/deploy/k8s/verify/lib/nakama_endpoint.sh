# shellcheck shell=bash
# Resolve the Nakama endpoint a verification target should probe, from the
# CLUSTER's own meta-hop opt-in (ADR-24) rather than from a literal.
#
# Sourced by a target file, which must already have set K (its kubectl command).
#
# Why this exists. dev-up.sh already discovers the scheme this way and exported
# the result -- but only inside its own process. CD runs dev-up.sh in one step and
# verify.sh in the next, in a fresh shell, so the export never arrived and the
# target fell back to its literal http://127.0.0.1:7001. With the dev cluster
# opted in, every deploy then failed data.nakama_health with
#
#   GET http://127.0.0.1:7001/healthcheck: status 400
#
# against a perfectly healthy Nakama -- 400 being Nakama's answer to "Client sent
# an HTTP request to an HTTPS server". CD on develop was red on every run for three
# days for that reason alone. Reading the opt-in here means the target cannot
# drift from the cluster it targets, which is the rule the server-key lookup in
# the same files already follows.
#
# Precedence: an explicitly set VERIFY_NAKAMA_URL wins untouched, so an operator
# can still point the suite anywhere. verify.sh re-applies caller-set VERIFY_*
# values after sourcing the target, so this only ever fills a gap.

# resolve_nakama_endpoint PORT
resolve_nakama_endpoint() {
  local port="$1" tls_path pin

  if [ -n "${VERIFY_NAKAMA_URL:-}" ]; then
    : "${VERIFY_NAKAMA_TLS_CERT:=}"
    return 0
  fi

  # The same key dev-up.sh and the nakama Deployment read. Optional: a cluster
  # that did not opt in has no such key (or no ConfigMap at all) and is plaintext.
  tls_path="$($K get configmap nakama-config -n rpg-k8s-data \
    -o 'jsonpath={.data.tls-cert-path}' 2>/dev/null || true)"

  if [ -z "$tls_path" ]; then
    VERIFY_NAKAMA_URL="http://127.0.0.1:${port}"
    : "${VERIFY_NAKAMA_TLS_CERT:=}"
    return 0
  fi

  VERIFY_NAKAMA_URL="https://127.0.0.1:${port}"
  [ -n "${VERIFY_NAKAMA_TLS_CERT:-}" ] && return 0

  # Pin read out of the cluster's own Secret, never a copy kept elsewhere: a stale
  # pin fails in the one way that does not name itself. --cacert, not -k, is how
  # the checks use it -- an accept-anything probe passes against anything.
  pin="${RPG_K8S_RUN_DIR:-${TMPDIR:-/tmp}}/nakama-tls-verify-${port}.crt"
  mkdir -p "$(dirname "$pin")"
  if $K get secret nakama-tls -n rpg-k8s-data -o 'jsonpath={.data.tls\.crt}' 2>/dev/null \
       | base64 -d > "$pin" 2>/dev/null \
     && grep -q "BEGIN CERTIFICATE" "$pin"; then
    VERIFY_NAKAMA_TLS_CERT="$pin"
  else
    # Stay on https so the checks fail as a TLS failure. Falling back to http here
    # would reproduce exactly the misleading 400 this file exists to prevent.
    echo "WARNING: the meta hop is on (nakama-config tls-cert-path=${tls_path}) but no PEM" >&2
    echo "  certificate could be read from the nakama-tls Secret; Nakama checks will fail on TLS." >&2
    VERIFY_NAKAMA_TLS_CERT=""
  fi
}
