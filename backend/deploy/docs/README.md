# Deploy — Infrastructure & DevOps

Infrastructure configs for RPG MMO backend. Supports 4 deployment tiers.

## Tiers

| Tier | Cost/mo | Setup | CCU |
|------|---------|-------|-----|
| Dev/Alpha | $40-60 | 1 VPS all-in-one | < 200 |
| Beta | $80-150 | 2 VPS (app + DB) | 200-500 |
| Soft Launch | $200-400 | 3 VPS separated | 500-2000 |
| Growth | $400-1000+ | Multi-node k3s | 2000-5000+ |

## Quick Start (Dev Tier)

```bash
# 1. Install k3s
./k3s/setup.sh

# 2. Deploy infrastructure
kubectl apply -k k8s/overlays/dev/

# 3. Initialize databases
kubectl exec -it postgresql-0 -- psql -f /init/init-meta.sql
kubectl exec -it postgresql-0 -- psql -f /init/init-gamestate.sql

# 4. Deploy Agones fleets
kubectl apply -f agones/
```

## Stack

All open-source, $0 license: k3s, Agones, PostgreSQL, Redis, Grafana, Prometheus.
