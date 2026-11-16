#!/usr/bin/env bash
# Live check for compose log shipping (Grafana Alloy -> Loki). Run it against a
# RUNNING `monitoring` profile (and, for the realtime assertions, `realtime`):
#
#   bash monitoring/check-log-shipping.sh                      # from backend/deploy
#   COMPOSE_PROJECT_NAME=rpg-mmo-staging LOKI_PORT=3120 \
#     bash monitoring/check-log-shipping.sh                    # another stack
#
# What it proves (docs/MONITORING.md "How logs flow"):
#   1. Loki's host port is bound to 127.0.0.1 only.
#   2. Loki reports 7-day retention and the compactor enforces it.
#   3. A marker line written by a short-lived container of the stack's compose
#      project reaches Loki with env, service and level (a JSON line gets its
#      level parsed, a plain line gets `unknown`) and no id-like label.
#   4. With the realtime profile up: a gateway stream and a game server stream
#      exist with env, service, level and, for the game server, game and
#      server_id matching its compose labels. SKIP when that profile is down.
#
# The marker container is started from THIS host's Docker CLI with the compose
# project and service labels set by hand (`docker run -l ...`). No container of
# the stack gets a socket, writable or otherwise.
#
# Inputs (all optional):
#   COMPOSE_PROJECT_NAME  stack to check; default: the one running Alloy
#                         (required when several stacks share this Docker daemon)
#   DEPLOY_ENVIRONMENT    expected `env` label; default: read from Alloy's container
#   LOKI_PORT             host port of Loki (default 3100; see environments.tsv)
#   CHECK_TIMEOUT         seconds to wait for each line to appear (default 90)
#   CHECK_RANGE_SECONDS   how far back the gateway/game server streams are looked
#                         up (default 86400)
#   CHECK_IMAGE           image for the marker container (needs `sh`); default:
#                         the image of the stack's own `lgtm` container
#
# Exit status: 0 when nothing FAILed (SKIPs do not fail), 1 otherwise.
# Needs bash, curl and docker only. It never reads deploy/.env.
set -uo pipefail

LOKI_PORT="${LOKI_PORT:-3100}"
CHECK_TIMEOUT="${CHECK_TIMEOUT:-90}"
CHECK_RANGE_SECONDS="${CHECK_RANGE_SECONDS:-86400}"
LOKI="http://127.0.0.1:${LOKI_PORT}"
INTERVAL=3

PASS_N=0
FAIL_N=0
SKIP_N=0
pass() { PASS_N=$((PASS_N + 1)); echo "PASS: $*"; }
fail() { FAIL_N=$((FAIL_N + 1)); echo "FAIL: $*"; }
skip() { SKIP_N=$((SKIP_N + 1)); echo "SKIP: $*"; }
info() { echo "      $*"; }

finish() {
	echo "----"
	echo "log shipping check: ${PASS_N} passed, ${FAIL_N} failed, ${SKIP_N} skipped"
	[ "$FAIL_N" -eq 0 ]
	exit $?
}

command -v docker >/dev/null 2>&1 || { echo "FAIL: docker is not installed or not on PATH"; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "FAIL: curl is not installed or not on PATH"; exit 1; }

MARKER_CID=""
cleanup() {
	[ -n "$MARKER_CID" ] && docker rm -f "$MARKER_CID" >/dev/null 2>&1
	return 0
}
trap cleanup EXIT

# First container id of a compose service in the project, or empty.
service_cid() {
	docker ps -q \
		--filter "label=com.docker.compose.project=$1" \
		--filter "label=com.docker.compose.service=$2" 2>/dev/null | head -n 1
}

# ---------------------------------------------------------------- 0. the stack
if [ -z "${COMPOSE_PROJECT_NAME:-}" ]; then
	alloys="$(docker ps --filter "label=com.docker.compose.service=alloy" \
		--format '{{.ID}} {{.Label "com.docker.compose.project"}}' 2>/dev/null)"
	count="$(printf '%s\n' "$alloys" | grep -c . || true)"
	if [ "$count" -eq 1 ]; then
		COMPOSE_PROJECT_NAME="${alloys#* }"
	elif [ "$count" -eq 0 ]; then
		fail "no running alloy container: start the stack with --profile monitoring"
		finish
	else
		fail "several stacks run alloy on this daemon; set COMPOSE_PROJECT_NAME to one of:"
		printf '%s\n' "$alloys" | sed 's/^[^ ]* /        /'
		finish
	fi
fi
PROJECT="$COMPOSE_PROJECT_NAME"

ALLOY_CID="$(service_cid "$PROJECT" alloy)"
LGTM_CID="$(service_cid "$PROJECT" lgtm)"
if [ -z "$ALLOY_CID" ] || [ -z "$LGTM_CID" ]; then
	fail "project '${PROJECT}': alloy and lgtm must both be running (alloy='${ALLOY_CID:-none}' lgtm='${LGTM_CID:-none}')"
	finish
fi

if [ -z "${DEPLOY_ENVIRONMENT:-}" ]; then
	# Alloy's own environment holds only these two names, never a secret.
	DEPLOY_ENVIRONMENT="$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$ALLOY_CID" 2>/dev/null \
		| sed -n 's/^DEPLOY_ENVIRONMENT=//p' | head -n 1)"
fi
if [ -z "$DEPLOY_ENVIRONMENT" ]; then
	fail "cannot determine the env label: set DEPLOY_ENVIRONMENT"
	finish
fi
ENVL="$DEPLOY_ENVIRONMENT"
echo "stack: project=${PROJECT} env=${ENVL} loki=${LOKI}"

# --------------------------------------------------------- helpers around Loki
START_NS="$(( $(date +%s) - CHECK_RANGE_SECONDS ))000000000"
BODY=""

# loki_query <logql> [start_ns]: sets BODY, returns 0 when it holds >= 1 line.
loki_query() {
	BODY="$(curl -fsS --max-time 10 -G "${LOKI}/loki/api/v1/query_range" \
		--data-urlencode "query=$1" \
		--data-urlencode "start=${2:-$START_NS}" \
		--data-urlencode "limit=20" \
		--data-urlencode "direction=backward" 2>/dev/null)" || return 1
	case "$BODY" in *'"values":[['*) return 0 ;; esac
	return 1
}

# wait_for <logql> [start_ns]: bounded retry around loki_query.
wait_for() {
	local waited=0
	while :; do
		loki_query "$1" "${2:-$START_NS}" && return 0
		[ "$waited" -ge "$CHECK_TIMEOUT" ] && return 1
		sleep "$INTERVAL"
		waited=$((waited + INTERVAL))
	done
}

# stream_labels: the label object of the first stream in BODY.
stream_labels() { printf '%s' "$BODY" | grep -o '"stream":{[^}]*}' | head -n 1; }
has_label() { printf '%s' "$1" | grep -q "\"$2\":\""; }
label_is() { printf '%s' "$1" | grep -qF "\"$2\":\"$3\""; }

# Labels that would mean a per-container stream (unbounded cardinality).
forbidden_labels() {
	local labels="$1" found="" k
	for k in container container_id container_name instance id filename; do
		has_label "$labels" "$k" && found="$found $k"
	done
	printf '%s' "$found"
}

# ready: Loki answers on the published loopback port.
waited=0
until curl -fsS --max-time 5 "${LOKI}/ready" >/dev/null 2>&1; do
	if [ "$waited" -ge "$CHECK_TIMEOUT" ]; then
		fail "Loki is not ready on ${LOKI}/ready after ${CHECK_TIMEOUT}s (wrong LOKI_PORT?)"
		finish
	fi
	sleep "$INTERVAL"
	waited=$((waited + INTERVAL))
done
pass "Loki is ready on ${LOKI}"

# -------------------------------------------------------- 1. loopback binding
ports="$(docker port "$LGTM_CID" 3100/tcp 2>/dev/null)"
if [ -z "$ports" ]; then
	fail "Loki port 3100 is not published by the lgtm container"
else
	bad="$(printf '%s\n' "$ports" | grep -v '^127\.0\.0\.1:' || true)"
	if [ -n "$bad" ]; then
		fail "Loki is published beyond loopback: $(printf '%s' "$bad" | tr '\n' ' ')"
	elif ! printf '%s\n' "$ports" | grep -q "^127\.0\.0\.1:${LOKI_PORT}\$"; then
		fail "Loki is bound to 127.0.0.1 but not on port ${LOKI_PORT}: $(printf '%s' "$ports" | tr '\n' ' ')"
	else
		pass "Loki port is bound to 127.0.0.1 only (docker port: $(printf '%s' "$ports" | tr '\n' ' '))"
	fi
fi

# ------------------------------------------------------------- 2. retention
# /config prints the effective configuration. Loki renders a duration in weeks
# where it can, so 168h reads back as 1w.
CONFIG="$(curl -fsS --max-time 10 "${LOKI}/config" 2>/dev/null || true)"
if [ -z "$CONFIG" ]; then
	fail "GET ${LOKI}/config returned nothing, cannot read retention"
else
	period="$(printf '%s\n' "$CONFIG" | awk '/^limits_config:/{f=1;next} /^[^ ]/{f=0} f && /^ +retention_period:/{print $2; exit}')"
	case "$period" in
		168h | 1w | 7d) pass "limits_config.retention_period is ${period} (7 days)" ;;
		*) fail "limits_config.retention_period is '${period:-unset}', expected 168h (1w)" ;;
	esac
	enabled="$(printf '%s\n' "$CONFIG" | awk '/^compactor:/{f=1;next} /^[^ ]/{f=0} f && /^ +retention_enabled:/{print $2; exit}')"
	if [ "$enabled" = "true" ]; then
		pass "compactor.retention_enabled is true (retention is enforced)"
	else
		fail "compactor.retention_enabled is '${enabled:-unset}', expected true"
	fi
fi

# --------------------------------------------------------------- 3. marker
IMAGE="${CHECK_IMAGE:-$(docker inspect --format '{{.Config.Image}}' "$LGTM_CID" 2>/dev/null)}"
MARKER="logship-$(date +%s)-$$-${RANDOM}"
CHECK_SERVICE="log-shipping-check"
LIFETIME=$(( (CHECK_TIMEOUT + 30) / 2 ))
# One JSON line (level WARN, the spelling the gateway and the game server JSON
# logger write; expected label: warn) and one plain line (expected label:
# unknown), repeated, because Alloy only tails a container once its 10s discovery
# refresh has seen it. The loop ends by itself so a killed check leaves nothing
# running.
SCRIPT="i=0; while [ \$i -lt ${LIFETIME} ]; do echo '{\"level\":\"WARN\",\"msg\":\"${MARKER} json\"}'; echo '${MARKER} plain'; i=\$((i+1)); sleep 2; done"
MARKER_CID="$(docker run -d --rm --entrypoint sh \
	--label "com.docker.compose.project=${PROJECT}" \
	--label "com.docker.compose.service=${CHECK_SERVICE}" \
	"$IMAGE" -c "$SCRIPT" 2>&1)" || {
	fail "could not start the marker container from image '${IMAGE}': ${MARKER_CID}"
	MARKER_CID=""
	finish
}
info "marker ${MARKER} written by container ${MARKER_CID:0:12} (service ${CHECK_SERVICE})"

MARKER_START="$(( $(date +%s) - 60 ))000000000"
SEL="env=\"${ENVL}\",service=\"${CHECK_SERVICE}\""

if wait_for "{${SEL},level=\"warn\"} |= \"${MARKER} json\"" "$MARKER_START"; then
	labels="$(stream_labels)"
	ok=1
	for k in env service level; do has_label "$labels" "$k" || { fail "marker stream lacks label ${k}: ${labels}"; ok=0; }; done
	label_is "$labels" env "$ENVL" || { fail "marker stream env is not '${ENVL}': ${labels}"; ok=0; }
	label_is "$labels" service "$CHECK_SERVICE" || { fail "marker stream service is not '${CHECK_SERVICE}': ${labels}"; ok=0; }
	label_is "$labels" level warn || { fail "marker level is not 'warn' (JSON \"WARN\"): ${labels}"; ok=0; }
	extra="$(forbidden_labels "$labels")"
	[ -z "$extra" ] || { fail "marker stream carries per-container labels:${extra}"; ok=0; }
	[ "$ok" -eq 1 ] && pass "JSON marker found with env=${ENVL} service=${CHECK_SERVICE} level=warn and no id-like label"
	if printf '%s' "$BODY" | grep -qF "\\\"level\\\":\\\"WARN\\\""; then
		pass "the JSON line was shipped as written (body not rewritten)"
	else
		fail "the shipped JSON line differs from what the container wrote"
	fi
else
	fail "JSON marker '${MARKER} json' not found in Loki within ${CHECK_TIMEOUT}s with {${SEL},level=\"warn\"}"
	info "look at: docker logs ${ALLOY_CID:0:12}; Alloy UI inside the network on alloy:12345"
fi

if wait_for "{${SEL},level=\"unknown\"} |= \"${MARKER} plain\"" "$MARKER_START"; then
	labels="$(stream_labels)"
	if has_label "$labels" env && has_label "$labels" service && label_is "$labels" level unknown; then
		pass "plain-line marker found with level=unknown"
	else
		fail "plain-line marker stream has unexpected labels: ${labels}"
	fi
else
	fail "plain marker '${MARKER} plain' not found with level=\"unknown\" within ${CHECK_TIMEOUT}s"
fi

# ------------------------------------------------------------ 4. realtime
GW_CID="$(service_cid "$PROJECT" gateway)"
if [ -z "$GW_CID" ]; then
	skip "gateway and game server assertions: realtime profile is down in project ${PROJECT}"
	finish
fi

if wait_for "{env=\"${ENVL}\",service=\"gateway\",level=~\".+\"}"; then
	labels="$(stream_labels)"
	if has_label "$labels" env && has_label "$labels" service && has_label "$labels" level \
		&& label_is "$labels" env "$ENVL" && label_is "$labels" service gateway; then
		pass "gateway stream found with env, service and level (${labels})"
	else
		fail "gateway stream has unexpected labels: ${labels}"
	fi
	[ -z "$(forbidden_labels "$labels")" ] || fail "gateway stream carries per-container labels: $(forbidden_labels "$labels")"
else
	fail "no gateway line in Loki for env=${ENVL} in the last ${CHECK_RANGE_SECONDS}s"
	info "Alloy ships from when it first saw the container: generate traffic (stack.sh check) or restart the gateway"
fi

# Every game server container of the project: the compose labels are the truth.
gs_found=0
while read -r cid svc; do
	[ -n "$cid" ] || continue
	game="$(docker inspect --format '{{index .Config.Labels "cuvara.log.game"}}' "$cid" 2>/dev/null)"
	sid="$(docker inspect --format '{{index .Config.Labels "cuvara.log.server_id"}}' "$cid" 2>/dev/null)"
	[ -n "$game" ] || continue
	gs_found=$((gs_found + 1))
	if [ -z "$sid" ]; then
		fail "${svc}: compose label cuvara.log.server_id is empty"
		continue
	fi
	# A real level is required: `unknown` (a plain-text game server) must not pass.
	if wait_for "{env=\"${ENVL}\",service=\"${svc}\",game=\"${game}\",server_id=\"${sid}\",level=~\".+\",level!=\"unknown\"}"; then
		labels="$(stream_labels)"
		ok=1
		for k in env service level game server_id; do has_label "$labels" "$k" || { fail "${svc}: stream lacks label ${k}: ${labels}"; ok=0; }; done
		label_is "$labels" level unknown && { fail "${svc}: level is unknown: ${labels}"; ok=0; }
		[ -z "$(forbidden_labels "$labels")" ] || { fail "${svc}: stream carries per-container labels: $(forbidden_labels "$labels")"; ok=0; }
		[ "$ok" -eq 1 ] && pass "${svc} stream found with env, service, a real level, game=${game} server_id=${sid}"
	elif loki_query "{env=\"${ENVL}\",service=\"${svc}\",game=\"${game}\",server_id=\"${sid}\"}"; then
		fail "${svc}: lines found for game=${game} server_id=${sid} but none with a real level (level=\"unknown\")"
		info "the game server must log JSON: GAMESERVER_LOG_FORMAT=json (the default; text gives level unknown)"
	else
		fail "${svc}: no line in Loki for game=${game} server_id=${sid} in the last ${CHECK_RANGE_SECONDS}s"
		info "restart the game server or connect a client so it logs, then rerun"
	fi
done < <(docker ps --filter "label=com.docker.compose.project=${PROJECT}" \
	--filter "label=cuvara.log.game" \
	--format '{{.ID}} {{.Label "com.docker.compose.service"}}' 2>/dev/null)

if [ "$gs_found" -eq 0 ]; then
	skip "game server assertions: no running container of ${PROJECT} carries the cuvara.log.game label"
fi

finish
