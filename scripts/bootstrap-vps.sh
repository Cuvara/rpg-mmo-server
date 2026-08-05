#!/usr/bin/env bash
#
# bootstrap-vps.sh — one-command preparation of a fresh Ubuntu 22.04/24.04 VPS
# so it can receive deploys from this repository's CD pipeline.
#
# It performs, idempotently:
#   1. Docker CE + compose plugin from the official apt repository
#   2. A deploy user and the deploy directory ($RPG_DEPLOY_DIR, default /opt/rpg-mmo)
#   3. A GitHub Actions self-hosted runner, installed as a systemd service
#   4. A ufw firewall policy: SSH + game ports open, Grafana DENIED by default
#
# Everything is safe to re-run: each step detects the work it already did.
#
# Usage (as root, or with sudo):
#   sudo RUNNER_TOKEN=XXXX ./scripts/bootstrap-vps.sh --labels staging
#   sudo ./scripts/bootstrap-vps.sh --dry-run --runner-token XXXX   # print, execute nothing
#
# Every flag has an environment-variable equivalent; the flag wins.
#
#   flag                  env                 default
#   --runner-token        RUNNER_TOKEN        (required unless --skip-runner)
#   --labels              RUNNER_LABELS       staging
#   --repo-url            REPO_URL            https://github.com/dycuong03/rpg-mmo-server
#   --runner-name         RUNNER_NAME         rpg-<labels>-<hostname>
#   --runner-version      RUNNER_VERSION      2.321.0
#   --deploy-user         DEPLOY_USER         rpg
#   --deploy-dir          RPG_DEPLOY_DIR      /opt/rpg-mmo
#   --gateway-port        GATEWAY_PORT        8000
#   --gameserver-port     GAMESERVER_PORT     9200
#   --ssh-port            SSH_PORT            22
#   --grafana-port        GRAFANA_PORT        3000
#   --admin-ip            ADMIN_IP            (empty — Grafana stays closed)
#   --skip-docker / --skip-runner / --skip-firewall / --skip-user
#   --dry-run             DRY_RUN=1           print every action, change nothing
#
# The runner token is a *registration* token from
# GitHub -> Settings -> Actions -> Runners -> New self-hosted runner. It expires
# after one hour and is single-use; it is never written to disk by this script.
#
set -euo pipefail

# ------------------------------------------------------------------ defaults
RUNNER_TOKEN="${RUNNER_TOKEN:-}"
RUNNER_LABELS="${RUNNER_LABELS:-staging}"
REPO_URL="${REPO_URL:-https://github.com/dycuong03/rpg-mmo-server}"
RUNNER_NAME="${RUNNER_NAME:-}"
RUNNER_VERSION="${RUNNER_VERSION:-2.321.0}"
DEPLOY_USER="${DEPLOY_USER:-rpg}"
DEPLOY_DIR="${RPG_DEPLOY_DIR:-/opt/rpg-mmo}"
RUNNER_DIR="${RUNNER_DIR:-/opt/actions-runner}"
GATEWAY_PORT="${GATEWAY_PORT:-8000}"
GAMESERVER_PORT="${GAMESERVER_PORT:-9200}"
SSH_PORT="${SSH_PORT:-22}"
GRAFANA_PORT="${GRAFANA_PORT:-3000}"
ADMIN_IP="${ADMIN_IP:-}"
DRY_RUN="${DRY_RUN:-0}"
SKIP_DOCKER=0
SKIP_RUNNER=0
SKIP_FIREWALL=0
SKIP_USER=0

# ------------------------------------------------------------------ plumbing
step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '\033[1;33m    WARN: %s\033[0m\n' "$*" >&2; }
fail() {
	printf '\033[1;31mFAIL: %s\033[0m\n' "$*" >&2
	exit 1
}

# run CMD... — execute, or just print it under --dry-run.
run() {
	if [ "$DRY_RUN" = "1" ]; then
		printf '    [dry-run] %s\n' "$*"
		return 0
	fi
	"$@"
}

# run_sh 'shell snippet' — same, for anything needing a pipe or redirect.
run_sh() {
	if [ "$DRY_RUN" = "1" ]; then
		printf '    [dry-run] sh -c %s\n' "$1"
		return 0
	fi
	sh -c "$1"
}

usage() {
	sed -n '3,42p' "$0" | sed 's/^# \{0,1\}//'
	exit "${1:-0}"
}

need_arg() {
	[ -n "${2:-}" ] || fail "flag $1 needs a value"
}

# ------------------------------------------------------------- arg parsing
parse_args() {
	while [ $# -gt 0 ]; do
		case "$1" in
		--runner-token)
			need_arg "$1" "${2:-}"
			RUNNER_TOKEN="$2"
			shift 2
			;;
		--labels)
			need_arg "$1" "${2:-}"
			RUNNER_LABELS="$2"
			shift 2
			;;
		--repo-url)
			need_arg "$1" "${2:-}"
			REPO_URL="$2"
			shift 2
			;;
		--runner-name)
			need_arg "$1" "${2:-}"
			RUNNER_NAME="$2"
			shift 2
			;;
		--runner-version)
			need_arg "$1" "${2:-}"
			RUNNER_VERSION="$2"
			shift 2
			;;
		--deploy-user)
			need_arg "$1" "${2:-}"
			DEPLOY_USER="$2"
			shift 2
			;;
		--deploy-dir)
			need_arg "$1" "${2:-}"
			DEPLOY_DIR="$2"
			shift 2
			;;
		--gateway-port)
			need_arg "$1" "${2:-}"
			GATEWAY_PORT="$2"
			shift 2
			;;
		--gameserver-port)
			need_arg "$1" "${2:-}"
			GAMESERVER_PORT="$2"
			shift 2
			;;
		--ssh-port)
			need_arg "$1" "${2:-}"
			SSH_PORT="$2"
			shift 2
			;;
		--grafana-port)
			need_arg "$1" "${2:-}"
			GRAFANA_PORT="$2"
			shift 2
			;;
		--admin-ip)
			need_arg "$1" "${2:-}"
			ADMIN_IP="$2"
			shift 2
			;;
		--skip-docker) SKIP_DOCKER=1 && shift ;;
		--skip-runner) SKIP_RUNNER=1 && shift ;;
		--skip-firewall) SKIP_FIREWALL=1 && shift ;;
		--skip-user) SKIP_USER=1 && shift ;;
		--dry-run) DRY_RUN=1 && shift ;;
		-h | --help) usage 0 ;;
		*) fail "unknown argument '$1' (try --help)" ;;
		esac
	done

	[ -n "$RUNNER_NAME" ] || RUNNER_NAME="rpg-${RUNNER_LABELS%%,*}-$(hostname -s 2>/dev/null || echo vps)"

	case "$GATEWAY_PORT$GAMESERVER_PORT$SSH_PORT$GRAFANA_PORT" in
	*[!0-9]*) fail "ports must be numeric (got gateway=$GATEWAY_PORT gameserver=$GAMESERVER_PORT ssh=$SSH_PORT grafana=$GRAFANA_PORT)" ;;
	esac
}

# ------------------------------------------------------------------ preflight
preflight() {
	step "Preflight"
	if [ "$DRY_RUN" != "1" ] && [ "$(id -u)" != "0" ]; then
		fail "must run as root (use sudo). Add --dry-run to preview without root."
	fi
	if [ -r /etc/os-release ]; then
		# shellcheck disable=SC1091
		. /etc/os-release
		info "os: ${PRETTY_NAME:-unknown}"
		case "${VERSION_ID:-}" in
		22.04 | 24.04) ;;
		*) warn "tested on Ubuntu 22.04/24.04 — '${VERSION_ID:-?}' may need adjustments" ;;
		esac
	else
		warn "/etc/os-release missing — cannot verify the distribution"
	fi
	if [ "$SKIP_RUNNER" = "0" ] && [ -z "$RUNNER_TOKEN" ]; then
		fail "RUNNER_TOKEN (or --runner-token) is required; pass --skip-runner to prepare the box without a runner"
	fi
	info "deploy dir:    $DEPLOY_DIR"
	info "deploy user:   $DEPLOY_USER"
	info "runner:        $RUNNER_NAME  labels=[self-hosted,$RUNNER_LABELS]  repo=$REPO_URL"
	info "ports:         ssh/$SSH_PORT gateway/$GATEWAY_PORT gameserver/$GAMESERVER_PORT grafana/$GRAFANA_PORT (denied)"
	[ "$DRY_RUN" = "1" ] && info "MODE: dry-run — nothing will be changed"
	return 0
}

# ------------------------------------------------------------------- docker
install_docker() {
	step "Docker CE + compose plugin"
	if [ "$SKIP_DOCKER" = "1" ]; then
		info "skipped (--skip-docker)"
		return 0
	fi
	if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
		info "already installed: $(docker --version), $(docker compose version)"
		run systemctl enable --now docker
		return 0
	fi

	run apt-get update -qq
	run apt-get install -y -qq ca-certificates curl gnupg
	run install -m 0755 -d /etc/apt/keyrings
	# Official Docker apt repo. The keyring is re-downloaded on every run (cheap,
	# and keeps a truncated/corrupt key from wedging the box permanently).
	run_sh "curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc"
	run chmod a+r /etc/apt/keyrings/docker.asc
	run_sh "printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \"\$(dpkg --print-architecture)\" \"\$(. /etc/os-release && echo \"\${UBUNTU_CODENAME:-\$VERSION_CODENAME}\")\" > /etc/apt/sources.list.d/docker.list"
	run apt-get update -qq
	run apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
	run systemctl enable --now docker
	info "installed"
}

# --------------------------------------------------------- user + deploy dir
create_user() {
	step "Deploy user + directory"
	if [ "$SKIP_USER" = "1" ]; then
		info "skipped (--skip-user)"
		return 0
	fi
	if id -u "$DEPLOY_USER" >/dev/null 2>&1; then
		info "user '$DEPLOY_USER' exists"
	else
		info "creating user '$DEPLOY_USER'"
		run useradd --create-home --shell /bin/bash "$DEPLOY_USER"
	fi
	# The deploy job runs `docker compose` as the runner user.
	run usermod -aG docker "$DEPLOY_USER"
	run mkdir -p "$DEPLOY_DIR"/{bin,deploy,scripts,run,logs}
	run chown -R "$DEPLOY_USER:$DEPLOY_USER" "$DEPLOY_DIR"
	run chmod 0755 "$DEPLOY_DIR"
	info "deploy dir ready: $DEPLOY_DIR (owner $DEPLOY_USER, in group docker)"
}

# ------------------------------------------------------------------- runner
install_runner() {
	step "GitHub Actions runner"
	if [ "$SKIP_RUNNER" = "1" ]; then
		info "skipped (--skip-runner)"
		return 0
	fi
	if [ -f "$RUNNER_DIR/.runner" ]; then
		info "runner already configured at $RUNNER_DIR — leaving it alone"
		info "to re-register: cd $RUNNER_DIR && sudo ./svc.sh uninstall && sudo -u $DEPLOY_USER ./config.sh remove --token <TOKEN>"
		run systemctl start "actions.runner.$(echo "$REPO_URL" | sed 's#.*/##').$RUNNER_NAME.service" 2>/dev/null || true
		return 0
	fi

	local arch tarball url
	case "$(uname -m)" in
	x86_64) arch=x64 ;;
	aarch64 | arm64) arch=arm64 ;;
	*) fail "unsupported architecture $(uname -m)" ;;
	esac
	tarball="actions-runner-linux-${arch}-${RUNNER_VERSION}.tar.gz"
	url="https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/${tarball}"

	run mkdir -p "$RUNNER_DIR"
	run chown "$DEPLOY_USER:$DEPLOY_USER" "$RUNNER_DIR"
	info "downloading $url"
	run_sh "curl -fsSL -o '$RUNNER_DIR/$tarball' '$url'"
	run_sh "tar xzf '$RUNNER_DIR/$tarball' -C '$RUNNER_DIR'"
	run_sh "rm -f '$RUNNER_DIR/$tarball'"
	run chown -R "$DEPLOY_USER:$DEPLOY_USER" "$RUNNER_DIR"
	# The runner's own dependency installer (libicu etc.) must run as root.
	run_sh "'$RUNNER_DIR/bin/installdependencies.sh'"

	# config.sh refuses to run as root, hence the su. --unattended + --replace
	# make re-registration non-interactive. The token is passed as an argument
	# only to this one command and never persisted.
	info "registering runner '$RUNNER_NAME' with labels [self-hosted,$RUNNER_LABELS]"
	run_sh "cd '$RUNNER_DIR' && su '$DEPLOY_USER' -c './config.sh --unattended --replace --url \"$REPO_URL\" --token \"$RUNNER_TOKEN\" --name \"$RUNNER_NAME\" --labels \"$RUNNER_LABELS\" --work _work'"

	# svc.sh must run as root and takes the *service account* as its argument.
	run_sh "cd '$RUNNER_DIR' && ./svc.sh install '$DEPLOY_USER'"
	run_sh "cd '$RUNNER_DIR' && ./svc.sh start"
	run_sh "cd '$RUNNER_DIR' && ./svc.sh status" || warn "svc.sh status returned non-zero — check 'journalctl -u actions.runner.*'"
	info "runner installed as a systemd service (survives reboot and logout)"
}

# ----------------------------------------------------------------- firewall
configure_firewall() {
	step "Firewall (ufw)"
	if [ "$SKIP_FIREWALL" = "1" ]; then
		info "skipped (--skip-firewall)"
		return 0
	fi
	if ! command -v ufw >/dev/null 2>&1; then
		run apt-get install -y -qq ufw
	fi

	run ufw --force default deny incoming
	run ufw --force default allow outgoing
	run ufw allow "$SSH_PORT/tcp" comment 'ssh'
	# Gateway: TCP today, UDP reserved for the KCP transport (shared/transport
	# already speaks it; opening the port now avoids a second firewall change).
	run ufw allow "$GATEWAY_PORT/tcp" comment 'rpg gateway (tcp)'
	run ufw allow "$GATEWAY_PORT/udp" comment 'rpg gateway (kcp, future)'
	run ufw allow "$GAMESERVER_PORT/tcp" comment 'rpg gameserver (tcp)'
	run ufw allow "$GAMESERVER_PORT/udp" comment 'rpg gameserver (kcp, future)'

	# Grafana stays CLOSED. The bundled Prometheus (9090) and OTLP (4317/4318)
	# have no authentication at all and are bound to loopback by compose, so they
	# need no rule here either.
	run ufw deny "$GRAFANA_PORT/tcp" comment 'grafana (closed — use an ssh tunnel)'
	if [ -n "$ADMIN_IP" ]; then
		info "opening Grafana to $ADMIN_IP only (--admin-ip)"
		run ufw allow from "$ADMIN_IP" to any port "$GRAFANA_PORT" proto tcp comment 'grafana (admin ip)'
	else
		info "Grafana ${GRAFANA_PORT}/tcp DENIED. To reach it, prefer an SSH tunnel:"
		info "    ssh -L ${GRAFANA_PORT}:127.0.0.1:${GRAFANA_PORT} ${DEPLOY_USER}@<vps>"
		info "  or allow one admin IP:"
		info "    sudo ufw allow from <ADMIN_IP> to any port ${GRAFANA_PORT} proto tcp"
	fi

	run_sh "ufw --force enable"
	run ufw status verbose || true

	# Docker publishes ports by writing its own iptables rules in the DOCKER-USER
	# chain, which is traversed BEFORE ufw's INPUT rules — so a `ufw deny` does
	# not actually close a published container port. compose already binds
	# Grafana/Prometheus/OTLP per GRAFANA_BIND/PROMETHEUS_BIND/OTLP_BIND, so keep
	# those on 127.0.0.1; this rule is the belt to that suspenders.
	if command -v iptables >/dev/null 2>&1; then
		if [ -n "$ADMIN_IP" ]; then
			run_sh "iptables -C DOCKER-USER -p tcp --dport $GRAFANA_PORT ! -s $ADMIN_IP -j DROP 2>/dev/null || iptables -I DOCKER-USER -p tcp --dport $GRAFANA_PORT ! -s $ADMIN_IP -j DROP"
			info "DOCKER-USER: grafana/$GRAFANA_PORT dropped except from $ADMIN_IP"
		else
			run_sh "iptables -C DOCKER-USER -p tcp --dport $GRAFANA_PORT -j DROP 2>/dev/null || iptables -I DOCKER-USER -p tcp --dport $GRAFANA_PORT -j DROP"
			info "DOCKER-USER: grafana/$GRAFANA_PORT dropped (docker bypasses ufw)"
		fi
		warn "iptables rules are not persistent across reboot — install iptables-persistent, or keep GRAFANA_BIND=127.0.0.1 (recommended)"
	fi
}

# ------------------------------------------------------------------ summary
print_summary() {
	step "Summary"
	cat <<EOF
    Docker         : $(command -v docker >/dev/null 2>&1 && docker --version 2>/dev/null || echo 'not installed (dry-run?)')
    Deploy user    : $DEPLOY_USER (member of 'docker')
    Deploy dir     : $DEPLOY_DIR
    Runner         : $RUNNER_NAME  labels=[self-hosted,$RUNNER_LABELS]  dir=$RUNNER_DIR
    Open ports     : ssh/$SSH_PORT, gateway $GATEWAY_PORT/tcp+udp, gameserver $GAMESERVER_PORT/tcp+udp
    Closed         : grafana/$GRAFANA_PORT${ADMIN_IP:+ (except $ADMIN_IP)}, prometheus/9090, otlp/4317-4318

    Next steps — all of them are GitHub-side; no code change is needed:

    1. Confirm the runner shows up Idle:
         $REPO_URL/settings/actions/runners

    2. Create/settle the GitHub Environment matching the runner label
       ('$RUNNER_LABELS') and set its secrets:
         JWT_SECRET, POSTGRES_PASSWORD, NAKAMA_CONSOLE_PASSWORD, GRAFANA_ADMIN_PASSWORD
       and its variables:
         RPG_DEPLOY_DIR=$DEPLOY_DIR
         DEPLOY_MODE=containers
         GATEWAY_CONTAINER_PORT=$GATEWAY_PORT
         GAMESERVER_CONTAINER_PORT=$GAMESERVER_PORT
         GAMESERVER_PUBLIC_ADDR=<public-host-or-ip>:$GAMESERVER_PORT
         GRAFANA_BIND=127.0.0.1
       (see backend/deploy/docs/CICD.md § "Moving to a VPS")

    3. Push the branch that maps to this environment, e.g.:
         git push origin staging
       or dispatch the CD workflow at
         $REPO_URL/actions/workflows/cd.yml

    4. Watch the run; the post-deploy smoke job is the end-to-end proof.
EOF
}

main() {
	parse_args "$@"
	preflight
	install_docker
	create_user
	install_runner
	configure_firewall
	print_summary
	step "bootstrap-vps.sh complete"
}

main "$@"
