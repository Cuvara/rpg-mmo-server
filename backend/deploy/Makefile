# Local dev stack. Run from backend/deploy/.
#
# WHOLE BACKEND, ONE COMMAND:
#   make flow-up     # build every image + start postgres, redis, nakama,
#                    # gateway and the C# game server, correctly wired
#   make flow-check  # prove the full client flow through it (smoketest)
#   make flow-down   # stop everything (keeps data volumes)
#
# Backing services only (gateway/gameserver run on the host with go run /
# dotnet run — the interactive-dev path):
#   make plugin   # compile nakama.so into ./modules
#   make up       # start postgres + postgres-game + redis + nakama
#   make logs     # tail logs
#   make down     # stop stack (keeps data volumes)
#   make reset    # stop + delete data volumes + delete plugin

SHELL := /bin/bash
COMPOSE ?= docker compose
# Build context is backend/ — nakama module needs ../shared (replace directive).
CONTEXT := ..
DOCKERFILE := nakama-plugin.Dockerfile
NAKAMA_VERSION ?= 3.40.0
IMAGE ?= rpg-mmo/nakama:$(NAKAMA_VERSION)
# Stamped into org.opencontainers.image.revision so the image can be tied to the
# commit whose plugin it carries. Until this existed the image inherited Heroic
# Labs' revision from the base and reported THEIR sha as its own.
GIT_REVISION ?= $(shell git rev-parse HEAD 2>/dev/null || echo unknown)

.DEFAULT_GOAL := help

.PHONY: help
help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

.PHONY: plugin
plugin: ## Build nakama.so into ./modules (mounted by docker-compose)
	mkdir -p modules
	DOCKER_BUILDKIT=1 docker build \
		-f $(DOCKERFILE) \
		--build-arg NAKAMA_VERSION=$(NAKAMA_VERSION) \
		--target export \
		--output type=local,dest=modules \
		$(CONTEXT)
	@ls -lh modules/nakama.so

.PHONY: image
image: ## Build a nakama image with the plugin baked in (for k3s/CI)
	docker build \
		-f $(DOCKERFILE) \
		--build-arg NAKAMA_VERSION=$(NAKAMA_VERSION) \
		--build-arg GIT_REVISION=$(GIT_REVISION) \
		--target runtime \
		-t $(IMAGE) \
		$(CONTEXT)

# ------------------------------------------------------------- whole stack
# The whole-stack targets are thin wrappers around ./stack.sh, which is the
# real entry point: `make` is not installed on every dev box here (it is absent
# from the WSL distro this project is developed on), so the one-command path
# must not depend on it. Anything these targets do, `./stack.sh <cmd>` does.
#
# SCRATCH=1 runs an ISOLATED second stack (own compose project, own container
# names, offset published ports) so it can coexist with one already running.
STACK := ./stack.sh
ifeq ($(SCRATCH),1)
STACK_FLAGS := --scratch
else
STACK_FLAGS :=
endif

.PHONY: flow-up
flow-up: ## ONE COMMAND: build everything and run the whole backend
	$(STACK) up $(STACK_FLAGS)

.PHONY: flow-down
flow-down: ## Stop the whole stack (keeps data volumes)
	$(STACK) down $(STACK_FLAGS)

.PHONY: flow-reset
flow-reset: ## Stop the whole stack and wipe its data volumes
	$(STACK) down --wipe $(STACK_FLAGS)

.PHONY: flow-ps
flow-ps: ## Show whole-stack service status
	$(STACK) ps $(STACK_FLAGS)

.PHONY: flow-logs
flow-logs: ## Tail gateway + gameserver logs
	$(STACK) logs $(STACK_FLAGS)

.PHONY: flow-check
flow-check: ## Prove the full client flow end to end (nakama -> gateway -> gameserver)
	$(STACK) check $(STACK_FLAGS)

.PHONY: flow-health
flow-health: ## Probe every published health endpoint + the server registry
	$(STACK) health $(STACK_FLAGS)

.PHONY: up
up: ## Start postgres + redis + nakama in the background
	$(COMPOSE) up -d
	@echo "Console: http://localhost:7351 (admin / password)"

.PHONY: down
down: ## Stop the stack, keep the data volumes
	$(COMPOSE) down

.PHONY: reset
reset: ## Stop the stack, wipe postgres+redis volumes and the built plugin
	$(COMPOSE) down -v
	rm -f modules/nakama.so

.PHONY: logs
logs: ## Tail all logs
	$(COMPOSE) logs -f --tail=100

.PHONY: logs-nakama
logs-nakama: ## Tail nakama logs only
	$(COMPOSE) logs -f --tail=100 nakama

.PHONY: ps
ps: ## Show service status
	$(COMPOSE) ps

.PHONY: psql
psql: ## Open a psql shell on the meta DB
	$(COMPOSE) exec postgres psql -U $${POSTGRES_USER:-nakama} -d $${POSTGRES_DB:-nakama}

.PHONY: psql-game
psql-game: ## Open a psql shell on the game state DB (player_states)
	$(COMPOSE) exec postgres-game psql -U $${POSTGRES_GAME_USER:-game} -d $${POSTGRES_GAME_DB:-gamestate}

.PHONY: redis-cli
redis-cli: ## Open a redis-cli shell (auth applied if REDIS_PASSWORD is set)
	$(COMPOSE) exec redis sh -c 'redis-cli $${REDIS_PASSWORD:+-a "$${REDIS_PASSWORD}"} --no-auth-warning'

.PHONY: health
health: ## Curl the Nakama healthcheck + ping Redis
	curl -fsS http://localhost:7350/healthcheck && echo " nakama OK"
	$(COMPOSE) exec -T redis sh -c 'redis-cli $${REDIS_PASSWORD:+-a "$${REDIS_PASSWORD}"} --no-auth-warning ping'

.PHONY: monitoring-up
monitoring-up: ## Start the otel-lgtm observability stack (Grafana+Prometheus+Loki+Tempo)
	$(COMPOSE) --profile monitoring up -d
	@echo "Grafana:    http://localhost:$${GRAFANA_PORT:-3000}  ($${GRAFANA_USER:-admin} / $${GRAFANA_ADMIN_PASSWORD:-localdev})"
	@echo "Prometheus: http://localhost:$${PROMETHEUS_PORT:-9090}/targets"
	@echo "OTLP:       grpc :$${OTLP_GRPC_PORT:-4317}  http :$${OTLP_HTTP_PORT:-4318}"

.PHONY: monitoring-down
monitoring-down: ## Stop the observability stack (keeps the lgtm data volume)
	$(COMPOSE) --profile monitoring stop lgtm
	$(COMPOSE) --profile monitoring rm -f lgtm

.PHONY: monitoring-logs
monitoring-logs: ## Tail otel-lgtm logs
	$(COMPOSE) --profile monitoring logs -f --tail=100 lgtm

.PHONY: monitoring-targets
monitoring-targets: ## Show Prometheus scrape target health (job -> up/down)
	@curl -fsS http://localhost:$${PROMETHEUS_PORT:-9090}/api/v1/targets \
		| tr ',' '\n' | grep -E '"(job|health)"'

.PHONY: console
console: ## Print the console URL + default credentials
	@echo "http://localhost:7351  user=admin  password=password"
