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
