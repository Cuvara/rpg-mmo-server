# Local dev backing stack (PostgreSQL + Redis + Nakama with our Go plugin).
# Run from backend/deploy/.
#
#   make plugin   # compile nakama.so into ./modules
#   make up       # start stack
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
		--target runtime \
		-t $(IMAGE) \
		$(CONTEXT)

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

.PHONY: console
console: ## Print the console URL + default credentials
	@echo "http://localhost:7351  user=admin  password=password"

# ------------------------------------------------------------- monitoring
# Prometheus + Grafana live behind the `monitoring` compose profile, so the
# default `make up` stack is untouched. They can be started/stopped
# independently of it — `monitoring-down` only removes these two services.

.PHONY: monitoring-up
monitoring-up: ## Start prometheus + grafana (monitoring profile)
	$(COMPOSE) --profile monitoring up -d prometheus grafana
	@echo "Grafana:    http://localhost:$${GRAFANA_PORT:-3000}  (anonymous viewer; admin / $${GRAFANA_ADMIN_PASSWORD:-localdev} to edit)"
	@echo "Prometheus: http://localhost:$${PROMETHEUS_PORT:-9090}/targets"

.PHONY: monitoring-down
monitoring-down: ## Stop prometheus + grafana only (keeps their volumes)
	$(COMPOSE) --profile monitoring rm -sf prometheus grafana

.PHONY: monitoring-logs
monitoring-logs: ## Tail prometheus + grafana logs
	$(COMPOSE) --profile monitoring logs -f --tail=100 prometheus grafana

.PHONY: monitoring-targets
monitoring-targets: ## Print scrape target health (job / instance / state)
	@curl -fsS http://localhost:$${PROMETHEUS_PORT:-9090}/api/v1/targets \
		| grep -oE '"(job|instance|health)":"[^"]*"' \
		| paste - - -
