#!/usr/bin/env bash
# Team setup: GameServer Go→C# migration
# Creates tmux session with 4 Claude Code agents in separate panels
#
# Usage: bash scripts/team-gameserver-migration.sh
#
# Roles:
#   Panel 0 (top-left):     Team Lead - orchestrates, verifies flow
#   Panel 1 (top-right):    Integration Engineer - wire protocol interop tests
#   Panel 2 (bottom-left):  DevOps - deploy C# server, Docker, k3s
#   Panel 3 (bottom-right): Cleanup - remove Go gameserver, update refs

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

SESSION="gs-migration"
BRANCH="feat/gameserver-dotnet"

# Kill existing session if any
tmux kill-session -t "$SESSION" 2>/dev/null || true

# Create session with first pane
tmux new-session -d -s "$SESSION" -x 200 -y 50

# Split into 4 panes (2x2 grid)
tmux split-window -h -t "$SESSION"
tmux split-window -v -t "$SESSION:0.0"
tmux split-window -v -t "$SESSION:0.1"

# --- Panel 0: Team Lead ---
tmux send-keys -t "$SESSION:0.0" "claude --print '
You are the TEAM LEAD for migrating the game server from Go to C#.
Branch: $BRANCH (already pushed, CI passing).

Your responsibilities:
1. Coordinate with other agents (they run in parallel tmux panels)
2. Verify the FULL FLOW works: Gateway (Go) -> GameServer (C# .NET 10) -> Client
3. Run integration tests between Go gateway and C# gameserver
4. Verify wire protocol compatibility (4-byte BE length + JSON)
5. After all tests pass: remove Go gameserver module, update CI, update docs
6. Create final PR when everything is confirmed working

Start by:
- Check current state of backend/gameserver-dotnet/ (build, test)
- Check backend/integration_test/ for existing tests
- Plan what integration tests need updating for C# server
- Coordinate tasks

Do NOT proceed with Go gameserver removal until integration tests pass.
'" Enter

# --- Panel 1: Integration Engineer ---
tmux send-keys -t "$SESSION:0.2" "claude --print '
You are the INTEGRATION ENGINEER. Branch: $BRANCH.

Your task: Make integration tests work with the C# .NET 10 game server.

Current integration tests are in backend/integration_test/ and test
Go gateway + Go gameserver together. You need to:

1. Read existing integration tests to understand what they test
2. Update or create new integration tests that spin up:
   - Go gateway (existing)
   - C# gameserver (new, via dotnet run)
3. Verify wire protocol compatibility:
   - Client -> Gateway: MsgAuth, MsgEnterWorld
   - Gateway -> Client: MsgAuthResp, MsgEnterWorldResp (with C# server addr)
   - Client -> C# GameServer: MsgJoinToken
   - C# GameServer -> Client: MsgJoinTokenResp
   - Client -> C# GameServer: MsgInput (movement, attack)
   - C# GameServer -> Client: MsgSnapshot

Key files:
- backend/integration_test/integration_test.go
- backend/gameserver-dotnet/GameServer/Net/WireProtocol.cs
- backend/shared/messages/codec.go (Go wire protocol)

Test the EXACT same scenarios as existing Go integration tests.
'" Enter

# --- Panel 2: DevOps ---
tmux send-keys -t "$SESSION:0.1" "claude --print '
You are the DEVOPS ENGINEER. Branch: $BRANCH.

Your task: Ensure C# gameserver deploys correctly.

1. Test Docker build locally:
   cd backend && docker build -f deploy/docker/Dockerfile.gameserver-dotnet -t rpg-mmo/gameserver-dotnet:dev .

2. Test running the container:
   docker run --rm -p 9000:9000 rpg-mmo/gameserver-dotnet:dev --addr=:9000 --map-id=map_01

3. Update deploy scripts if needed:
   - backend/deploy/docker-compose.yml (add gameserver-dotnet service)
   - scripts/deploy-local.sh (update to use C# server)
   - scripts/build-all.sh (add dotnet build)

4. Verify Agones fleet manifest:
   - backend/deploy/agones/fleet-map-dotnet-dev.yaml

5. Update CI/CD pipeline if needed:
   - .github/workflows/cd.yml (add C# gameserver build+push)

Report when Docker image builds and runs successfully.
'" Enter

# --- Panel 3: Cleanup ---
tmux send-keys -t "$SESSION:0.3" "claude --print '
You are the CLEANUP ENGINEER. Branch: $BRANCH.

WAIT for Team Lead confirmation before starting. Do NOT remove anything
until integration tests are confirmed passing.

When given the go-ahead, your tasks:
1. Remove backend/gameserver/ (Go gameserver module)
2. Update backend/TEAM.md (remove Go gameserver references)
3. Update .github/workflows/ci.yml (remove test-gameserver job)
4. Update .github/workflows/cd.yml if applicable
5. Update root CLAUDE.md (gameserver section -> C# .NET 10)
6. Update backend/deploy/ configs to only reference C# gameserver
7. Remove backend/deploy/docker/Dockerfile.gameserver (Go version)
8. Update backend/integration_test/ go.mod (remove gameserver dependency)
9. Run all CI checks to verify nothing breaks
10. Commit with: feat(migration): remove Go gameserver, C# is now primary

DO NOT start until explicitly told the integration tests pass.
'" Enter

# Select first pane (Team Lead)
tmux select-pane -t "$SESSION:0.0"

# Attach
tmux attach-session -t "$SESSION"
