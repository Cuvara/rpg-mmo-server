# Deploy Module — Agent Instructions

**Role**: DevOps Engineer (`agent-devops`)
**Module**: `deploy/` (no Go module — infrastructure configs)
**Depends on**: All backend modules (build artifacts)

## Responsibilities

### 1. Docker
- Dockerfile for each binary: nakama (plugin), gateway (Go), gameserver-dotnet (C# NativeAOT)
- Multi-stage builds (builder + runtime)
- Minimal runtime images (distroless or alpine)
- Go binaries share a base image; C# gameserver uses .NET 10 SDK for NativeAOT publish

### 2. Kubernetes / k3s Manifests
- k3s cluster setup scripts
- Namespace organization: `rpg-meta`, `rpg-realtime`, `rpg-data`
- Deployments: Nakama, Gateway, Redis
- StatefulSets: PostgreSQL (meta + game state)
- Services, ConfigMaps, Secrets

### 3. Agones Game Server Configuration
- Fleet definitions for Map Servers and Dungeon Servers
- FleetAutoscaler policies (buffer-based)
- GameServer spec: ports, health check, resource limits
- Allocation policy for dungeon instances

### 4. Database
- PostgreSQL initialization scripts
- Migration runner (shared module migrations)
- Backup scripts (pg_dump for dev/alpha, WAL archiving for beta+)
- Two database setup: meta DB + game state DB

### 5. Redis
- Redis deployment with persistence (AOF + RDB)
- Redis Streams consumer group initialization
- Sentinel config for growth tier
- Key eviction policies

### 6. CI/CD (GitHub Actions)
- Build pipeline: lint -> test -> build -> push images
- `ci-dotnet.yml`: C# gameserver build + test pipeline (dotnet build/test)
- Deploy pipeline: per-tier deployment
- Migration pipeline: run DB migrations safely
- Proto generation pipeline

### 7. Monitoring & Alerting
- Prometheus scrape configs (Nakama, Gateway, GameServer, Redis, PostgreSQL)
- Grafana dashboards: CCU, match count, RPC latency, tick performance
- Alert rules: error rate, high latency, disk usage, crash restart
- Uptime Robot health endpoints

### 8. Tier-Specific Configs (Drawio Page 10)
- **Dev/Alpha ($40-60)**: 1 VPS all-in-one, pg_dump daily, < 200 CCU
- **Beta ($80-150)**: 2 VPS (app + DB), CDN, Grafana, 200-500 CCU
- **Soft Launch ($200-400)**: 3 VPS, Redis dedicated, 500-2000 CCU
- **Growth ($400-1000+)**: Multi-node k3s, managed DB optional, 2000-5000+ CCU

## Key Design Constraints
- All infrastructure = open-source, $0 license
- k3s over full K8s (~500MB vs 2GB+ control plane)
- Must work on single VPS for dev tier
- Configs parameterized per tier (kustomize or Helm values)

## Documentation Requirements
- `docs/README.md` — Infrastructure overview, tier descriptions, quick start
- `docs/SETUP.md` — Step-by-step cluster setup per tier
- `docs/RUNBOOK.md` — Deploy, rollback, scale, backup/restore, incident response
- `docs/MONITORING.md` — Dashboard guide, alert meanings, response procedures
- `CHANGELOG.md` — Every infra change logged

## File Structure Target
```
deploy/
  CLAUDE.md
  CHANGELOG.md
  docs/
    README.md
    SETUP.md
    RUNBOOK.md
    MONITORING.md
  docker/
    Dockerfile.nakama
    Dockerfile.gateway
    Dockerfile.gameserver-dotnet
  k3s/
    setup.sh             # k3s installation script
    namespaces.yaml
  k8s/
    base/                # Base manifests
      nakama/
      gateway/
      redis/
      postgresql/
    overlays/            # Per-tier overrides
      dev/
      beta/
      launch/
      growth/
  agones/
    fleet-map.yaml
    fleet-dungeon.yaml
    autoscaler.yaml
    allocation.yaml
  db/
    init-meta.sql
    init-gamestate.sql
    backup.sh
    restore.sh
  redis/
    redis.conf
    init-streams.sh
  ci/
    .github/
      workflows/
        build.yml
        deploy.yml
        migrate.yml
        proto-gen.yml
  monitoring/
    prometheus/
      scrape-config.yaml
      alert-rules.yaml
    grafana/
      dashboards/
        ccu.json
        rpc-latency.json
        tick-perf.json
    uptime/
      health-endpoints.yaml
```
