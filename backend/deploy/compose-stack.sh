#!/usr/bin/env bash
# Sourced by .github/workflows/cd.yml (`Start the data tier` and `Start / update
# the stack`). Not run on its own.
#
# WHAT IT IS FOR. The `monitoring` compose profile now holds three things: lgtm
# (Grafana, Prometheus, Loki; it was the whole profile before log shipping) and
# the two log collector containers, alloy (grafana/alloy) and docker-socket-proxy
# (tecnativa/docker-socket-proxy). Both collector images are new, pulled from the
# registry on first use. A bare `docker compose --profile monitoring up -d` pulls
# and starts all of them in one command, and under `set -e` a pull that fails (a
# registry outage, a tag that does not exist) or a collector that cannot start
# fails the step, so the game stack in the same command was not updated for the
# sake of log shipping.
#
# So the stack is brought up in two commands:
#
#   1. every service of the active profiles EXCEPT the two collector services.
#      Fatal exactly as before: a failure here fails the step.
#   2. the two collector services, when monitoring is on. Best effort: a failure
#      prints a ::warning:: with the command to retry, adds a line to the job
#      summary, and the step carries on.
#
# Nothing that was fatal before log shipping becomes silent: lgtm and every game
# and data service stay in command 1.
#
# Reads from the caller (bash arrays and variables, never exported):
#   file_args      array, extra `-f` arguments (may be empty or unset)
#   profile_args   array, `--profile` arguments (may be empty or unset)
#   MONITORING_ENABLED   anything but "false" means the collector is wanted
#   RPG_DEPLOY_DIR       only used to word the retry command
# Uses `docker` from PATH.

COLLECTOR_SERVICES=(alloy docker-socket-proxy)

# _compose ARGS...   docker compose with the caller's file and profile arguments.
_compose() {
	docker compose ${file_args[@]+"${file_args[@]}"} ${profile_args[@]+"${profile_args[@]}"} "$@"
}

# compose_up_collector
# Starts the collector services; ALWAYS returns 0 (a failure only warns).
compose_up_collector() {
	local rc=0 retry detail
	_compose up -d "${COLLECTOR_SERVICES[@]}" || rc=$?
	if [ "${rc}" -eq 0 ]; then
		echo "log collector: ${COLLECTOR_SERVICES[*]} started"
		return 0
	fi
	retry="cd ${RPG_DEPLOY_DIR:-<deploy dir>}/deploy && docker compose --profile monitoring up -d ${COLLECTOR_SERVICES[*]}"
	detail="the log collector (${COLLECTOR_SERVICES[*]}) could not be pulled or started (docker compose exit ${rc}). The game stack was deployed as usual; logs are NOT being shipped to Loki. Retry: ${retry}"
	echo "::warning title=Log collector not started::${detail}"
	echo "WARN: ${detail}" >&2
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
		echo "- :warning: log collector (${COLLECTOR_SERVICES[*]}) not started (exit ${rc}); logs are not shipped. Retry: \`${retry}\`" >> "${GITHUB_STEP_SUMMARY}" || true
	fi
	return 0
}

# compose_up_stack [UP FLAGS...]   e.g. compose_up_stack --remove-orphans
compose_up_stack() {
	local services svc core=()
	services="$(_compose config --services)" || return
	while IFS= read -r svc; do
		svc="${svc%$'\r'}"
		[ -n "${svc}" ] || continue
		case " ${COLLECTOR_SERVICES[*]} " in
			*" ${svc} "*) continue ;;
		esac
		core+=("${svc}")
	done <<<"${services}"
	if [ "${#core[@]}" -eq 0 ]; then
		echo "::error::docker compose config --services listed no service outside the log collector; refusing to start nothing silently"
		return 1
	fi
	_compose up -d "$@" "${core[@]}" || return
	if [ "${MONITORING_ENABLED:-true}" != "false" ]; then
		compose_up_collector
	fi
}
