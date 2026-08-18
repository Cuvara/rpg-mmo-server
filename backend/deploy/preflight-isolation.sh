#!/usr/bin/env bash
# Pre-deploy isolation guard.
#
# All three "environments" (dev / staging / production) are stacks on ONE box,
# so nothing but discipline keeps them apart: a deploy that resolves to another
# environment's deploy directory, compose project, container names or published
# ports does not fail — it WINS. It regenerates deploy/.env from its own
# variables, `docker compose up -d` adopts the other stack's containers under
# its own project, and the environment that was there is silently gone. That is
# not a hypothetical: `staging` shipped with RPG_DEPLOY_DIR, COMPOSE_* and every
# port identical to `dev`.
#
# This script runs BEFORE the deploy writes or starts anything and refuses the
# deploy when the resolved identity is already owned by someone else.
#
# It is a guard, not a scheduler. See "WHAT THIS CANNOT CATCH" at the bottom.
#
# Inputs (environment):
#   DEPLOY_ENVIRONMENT     dev | staging | production   (required)
#   RPG_DEPLOY_DIR         resolved deploy directory     (required)
#   COMPOSE_PROJECT_NAME   resolved compose project      (required)
#   COMPOSE_NAME_PREFIX    resolved container prefix     (required)
#   ISOLATION_PORTS        space-separated "label:port" pairs to claim
#   ISOLATION_ALLOW_ADOPT  set to 1 to downgrade the deploy-dir check to a
#                          warning (first-ever adoption of a pre-marker dir)
set -euo pipefail

: "${DEPLOY_ENVIRONMENT:?DEPLOY_ENVIRONMENT is required}"
: "${RPG_DEPLOY_DIR:?RPG_DEPLOY_DIR is required}"
: "${COMPOSE_PROJECT_NAME:?COMPOSE_PROJECT_NAME is required}"
: "${COMPOSE_NAME_PREFIX:?COMPOSE_NAME_PREFIX is required}"
ISOLATION_PORTS="${ISOLATION_PORTS:-}"

rc=0
err() { echo "::error::$*"; rc=1; }
warn() { echo "::warning::$*"; }
note() { echo "    $*"; }

echo "isolation preflight: environment='${DEPLOY_ENVIRONMENT}'"
note "deploy dir      : ${RPG_DEPLOY_DIR}"
note "compose project : ${COMPOSE_PROJECT_NAME}"
note "name prefix     : ${COMPOSE_NAME_PREFIX}"

# --------------------------------------------------------------------------
# 0. Static registry cross-check (backend/deploy/environments.tsv).
#
# The live checks below can only see collisions that are already running. This
# one is pure comparison, so it catches "staging and dev are configured
# identically" on a host where neither is up — the state staging was actually
# in. The file is an assertion over the GitHub Environment variables, which are
# not reviewable in a diff; when they disagree, the deploy stops rather than
# guessing which side is right.
# --------------------------------------------------------------------------
registry="$(dirname "$0")/environments.tsv"
if [ ! -f "${registry}" ]; then
	warn "no ${registry} — static isolation cross-check skipped"
else
	found_row=0
	while IFS=$'\t' read -r r_env r_dir r_proj r_prefix r_ports; do
		case "${r_env}" in ''|'#'*|environment) continue ;; esac
		if [ "${r_env}" = "${DEPLOY_ENVIRONMENT}" ]; then
			found_row=1
			[ "${r_dir}"    = "${RPG_DEPLOY_DIR}" ]       || err "environments.tsv reserves deploy dir '${r_dir}' for '${r_env}', but this deploy resolved '${RPG_DEPLOY_DIR}'. Fix the GitHub Environment variable or the row."
			[ "${r_proj}"   = "${COMPOSE_PROJECT_NAME}" ] || err "environments.tsv reserves compose project '${r_proj}' for '${r_env}', but this deploy resolved '${COMPOSE_PROJECT_NAME}'."
			[ "${r_prefix}" = "${COMPOSE_NAME_PREFIX}" ]  || err "environments.tsv reserves container prefix '${r_prefix}' for '${r_env}', but this deploy resolved '${COMPOSE_NAME_PREFIX}'."
			continue
		fi
		# A different environment's row: nothing we resolved may equal it.
		[ "${r_dir}"    != "${RPG_DEPLOY_DIR}" ]       || err "deploy dir '${RPG_DEPLOY_DIR}' is reserved for environment '${r_env}', not '${DEPLOY_ENVIRONMENT}'."
		[ "${r_proj}"   != "${COMPOSE_PROJECT_NAME}" ] || err "compose project '${COMPOSE_PROJECT_NAME}' is reserved for environment '${r_env}'. Sharing it shares the network AND the named volumes — i.e. the databases."
		[ "${r_prefix}" != "${COMPOSE_NAME_PREFIX}" ]  || err "container prefix '${COMPOSE_NAME_PREFIX}' is reserved for environment '${r_env}'; deploying would rename its containers into this project."
		for pair in ${ISOLATION_PORTS}; do
			port="${pair##*:}"
			case ",${r_ports}," in
				*",${port},"*) err "port ${port} (${pair%%:*}) is reserved for environment '${r_env}' in environments.tsv. Offset it for '${DEPLOY_ENVIRONMENT}'." ;;
			esac
		done
	done < "${registry}"
	[ "${found_row}" = 1 ] || warn "environment '${DEPLOY_ENVIRONMENT}' has no row in ${registry} — only the live checks apply to it"
	if [ "${rc}" = 0 ]; then note "static registry: consistent"; fi
fi

# --------------------------------------------------------------------------
# 1. Deploy directory ownership.
#
# CD regenerates $RPG_DEPLOY_DIR/deploy/.env WHOLESALE, so pointing two
# environments at one directory means each deploy overwrites the other's
# configuration — including variables the other set and this one does not,
# which silently revert to defaults (staging has no ALLOCATOR, so a staging
# deploy into dev's directory would reset ALLOCATOR=agones to none).
#
# Deploys from this change onward stamp DEPLOY_ENVIRONMENT into that file, so
# ownership is a string compare. For a directory written before the stamp
# existed, fall back to the identity fields that were always there: a
# different container prefix or gateway port means a different stack.
# --------------------------------------------------------------------------
envfile="${RPG_DEPLOY_DIR}/deploy/.env"
if [ ! -f "${envfile}" ]; then
	note "deploy dir: no existing ${envfile} — first deploy into this directory"
else
	prev_env="$(sed -n 's/^DEPLOY_ENVIRONMENT=//p' "${envfile}" | tail -n1)"
	if [ -n "${prev_env}" ]; then
		if [ "${prev_env}" = "${DEPLOY_ENVIRONMENT}" ]; then
			note "deploy dir: owned by '${prev_env}' — ours"
		else
			err "deploy directory '${RPG_DEPLOY_DIR}' is owned by environment" \
				"'${prev_env}', not '${DEPLOY_ENVIRONMENT}'. Deploying would overwrite its" \
				"deploy/.env wholesale. Give '${DEPLOY_ENVIRONMENT}' its own RPG_DEPLOY_DIR."
		fi
	else
		prev_prefix="$(sed -n 's/^COMPOSE_NAME_PREFIX=//p' "${envfile}" | tail -n1)"
		prev_gwport="$(sed -n 's/^GATEWAY_CONTAINER_PORT=//p' "${envfile}" | tail -n1)"
		if [ "${prev_prefix:-rpg}" = "${COMPOSE_NAME_PREFIX}" ]; then
			note "deploy dir: unstamped but identity matches (prefix '${prev_prefix:-rpg}') — adopting"
		elif [ "${ISOLATION_ALLOW_ADOPT:-0}" = "1" ]; then
			warn "deploy directory '${RPG_DEPLOY_DIR}' holds an unstamped .env for a" \
				"DIFFERENT stack (prefix '${prev_prefix:-rpg}', gateway port '${prev_gwport:-?}')." \
				"ISOLATION_ALLOW_ADOPT=1 — continuing anyway."
		else
			err "deploy directory '${RPG_DEPLOY_DIR}' holds an .env for a DIFFERENT stack" \
				"(prefix '${prev_prefix:-rpg}', gateway port '${prev_gwport:-?}'; this deploy is" \
				"prefix '${COMPOSE_NAME_PREFIX}'). It predates the DEPLOY_ENVIRONMENT stamp, so" \
				"ownership cannot be proven — refusing rather than overwriting it."
		fi
	fi
fi

# --------------------------------------------------------------------------
# 2. Container-name ownership.
#
# container_name is global to the docker daemon, so two stacks sharing a prefix
# do not get two sets of containers: `up -d` takes the existing ones over. The
# names are checked EXACTLY, not by prefix match — "rpg-" is a prefix of
# "rpg-prod-postgres", and a substring filter would report production's
# containers as dev's.
# --------------------------------------------------------------------------
if command -v docker >/dev/null 2>&1; then
	for svc in postgres postgres-game nakama redis gateway gameserver lgtm; do
		cname="${COMPOSE_NAME_PREFIX}-${svc}"
		[ -n "$(docker ps -aq --filter "name=^${cname}$" 2>/dev/null || true)" ] || continue
		owner="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' \
			"${cname}" 2>/dev/null || true)"
		if [ -z "${owner}" ]; then
			err "container '${cname}' exists and carries no compose project label — it is not" \
				"managed by this stack. Change COMPOSE_NAME_PREFIX for '${DEPLOY_ENVIRONMENT}'."
		elif [ "${owner}" != "${COMPOSE_PROJECT_NAME}" ]; then
			err "container '${cname}' belongs to compose project '${owner}', but this deploy" \
				"uses project '${COMPOSE_PROJECT_NAME}'. Bringing the stack up would take it" \
				"over. Change COMPOSE_NAME_PREFIX for '${DEPLOY_ENVIRONMENT}'."
		else
			note "container ${cname}: ours (${owner})"
		fi
	done
else
	warn "docker not on PATH — container and port ownership checks skipped"
fi

# --------------------------------------------------------------------------
# 3. Published-port ownership.
#
# A port bound by another stack makes `up -d` fail late, half-deployed, with a
# bind error; a port bound by an unrelated host process is worse, because in
# host mode the binary simply fails to listen. Both are cheap to detect here.
#
# Docker first: a published port maps to a container, and the container's
# compose project says whether it is ours. Anything else that holds the port is
# a foreign listener regardless of what it is.
# --------------------------------------------------------------------------
# Build a published-host-port -> container map ONCE. Note the `< /dev/null`:
# the docker CLI reads stdin, so calling it inside a `... | while read` loop
# eats the rest of the container list and every port after the first looks
# free — which is exactly the kind of silent all-clear this guard exists to
# prevent.
declare -A PORT_HOLDER=()
if command -v docker >/dev/null 2>&1; then
	mapfile -t _containers < <(docker ps --format '{{.Names}}' < /dev/null 2>/dev/null || true)
	for c in "${_containers[@]:-}"; do
		[ -n "${c}" ] || continue
		while read -r line; do
			# "5432/tcp -> 0.0.0.0:5432" / "... -> [::]:5432": the HOST port is the
			# tail after the last colon, never the container port on the left.
			hp="${line##*:}"
			case "${hp}" in ''|*[!0-9]*) continue ;; esac
			[ -n "${PORT_HOLDER[$hp]:-}" ] || PORT_HOLDER[$hp]="${c}"
		done < <(docker port "${c}" < /dev/null 2>/dev/null || true)
	done
fi

if command -v docker >/dev/null 2>&1; then
	for pair in ${ISOLATION_PORTS}; do
		label="${pair%%:*}"
		port="${pair##*:}"
		case "${port}" in
			''|*[!0-9]*) warn "ignoring malformed ISOLATION_PORTS entry '${pair}'"; continue ;;
		esac
		holder="${PORT_HOLDER[$port]:-}"
		if [ -n "${holder}" ]; then
			owner="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' \
				"${holder}" 2>/dev/null || true)"
			if [ "${owner}" = "${COMPOSE_PROJECT_NAME}" ]; then
				note "port ${port} (${label}): held by ${holder} — ours"
			else
				err "port ${port} (${label}) is published by container '${holder}'" \
					"(compose project '${owner:-<none>}'), not by '${COMPOSE_PROJECT_NAME}'." \
					"Two stacks cannot share a published port — offset it for '${DEPLOY_ENVIRONMENT}'."
			fi
			continue
		fi
		# Not a docker publish. Any other listener still owns the port.
		if command -v ss >/dev/null 2>&1 &&
			ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${port}\$"; then
			err "port ${port} (${label}) is already bound by a non-docker process on this host." \
				"Offset it for '${DEPLOY_ENVIRONMENT}' or stop the listener."
		else
			note "port ${port} (${label}): free"
		fi
	done
fi

if [ "${rc}" != 0 ]; then
	echo "::error::isolation preflight FAILED — refusing to deploy '${DEPLOY_ENVIRONMENT}'."
	exit 1
fi
echo "isolation preflight OK"

# --------------------------------------------------------------------------
# WHAT THIS CANNOT CATCH
#
#  * A collision with an environment that has NO row in environments.tsv and is
#    not currently running. Check 0 is the only check that does not need the
#    other stack to be live, and it only knows what that file declares.
#  * environments.tsv drifting from the GitHub Environment variables in a way
#    that is self-consistent — e.g. two rows edited to share a port. The file is
#    reviewed, the variables are not; the guard compares them, it does not audit
#    the file against itself.
#  * Two deploys racing. The checks are not atomic; between this step and
#    `up -d` another environment can claim a port. Serialisation comes from the
#    per-environment `concurrency` group in cd.yml, and that is per environment,
#    not per host.
#  * Named-volume sharing on its own. Volumes are owned by the compose project,
#    so a project-name collision means shared data — but only the container and
#    port symptoms of that are visible here. Two projects with different names
#    and hand-created external volumes would pass.
#  * Ports opened after this step, including every port an Agones fleet
#    GameServer takes from the k3d node range, and any host-mode binary a human
#    started by hand.
#  * systemd unit names in DEPLOY_MODE=host. scripts/deploy-local.sh derives its
#    unit names from COMPOSE_NAME_PREFIX, so a prefix collision is caught by
#    check 2 above — but only if the stack also has containers. A pure host-mode
#    environment with no compose stack has nothing for check 2 to find.
#  * Anything on another machine. This is a single-box guard by construction.
# --------------------------------------------------------------------------
