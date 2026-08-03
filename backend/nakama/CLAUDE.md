# Nakama Module — Agent Instructions

**Role**: Nakama Engineer (`agent-nakama`)
**Module**: `github.com/duycuong/rpg-mmo/nakama`
**Depends on**: `shared`

## Responsibilities

This module contains Nakama Go runtime plugins. All code runs inside Nakama process as registered hooks and RPCs.

### 1. Authentication (Drawio Page 2)
- Device auth (first login — auto-create account)
- Email auth (link after device)
- Social auth (Google, Apple, Facebook — progressive)
- Session token (JWT) + refresh token flow
- Account creation and validation against PostgreSQL (meta)

### 2. Economy & Transactions (Drawio Page 5)
- `BuyItem` RPC: rate limit check -> idempotency guard -> BEGIN TX -> check balance -> deduct + add -> COMMIT
- Atomic transactions with idempotency_key guard
- Wallet operations: credit, debit, balance check
- Inventory: add item, remove item, list items
- XADD `inventory_changed` to Redis Streams after transaction
- Rollback on insufficient funds / out-of-stock

### 3. Reward Granting (Internal RPC)
- Internal RPC endpoint for GameServer to grant rewards
- Signed request verification (not exposed externally)
- Credit currency + items after combat/dungeon clear
- Submit scores to leaderboard

### 4. Leaderboard (Drawio Page 5)
- Nakama sorted set operations — O(log N) ranking
- Write score record (kills, level, clear_time)
- Query rankings (top N, player rank, around-me)
- Season management: archive + reset + reward distribution
- XADD `season_ended` to Redis Streams

### 5. Social Features (Drawio Page 7)
- **Party**: CreateParty (open=true, max=4), InvitePartyMember, AcceptPartyMember, SendPartyData (chat/ready check)
- **Friends**: AddFriends, AcceptFriend, ListFriends, BlockFriend
- **Chat**: JoinChat (room/group/DM), WriteChatMessage, broadcast to channel
- **Guild**: Nakama Groups API (create, join, promote, kick)
- **Presence**: StatusFollow, StatusUpdate for online/offline tracking

### 6. Matchmaking
- Queue management for dungeon/PvP matching
- Party-aware matching (match full parties or fill)
- Skill-based or level-based filtering

### 7. Notifications
- Push notifications for friend requests, party invites, rewards
- WebSocket real-time notifications
- Notification persistence and read tracking

## Key Design Constraints
- All code = Nakama Go plugin hooks (InitModule pattern)
- Economy = ALWAYS atomic DB transactions
- Rewards from GameServer = internal RPC only (signed, no external network)
- Rate limiting on all client-facing RPCs
- All storage uses Nakama Storage Engine or direct PostgreSQL

## Integration Points
- **With Gateway**: JWT shared secret (Gateway verifies locally, no roundtrip to Nakama)
- **With GameServer**: Internal RPC for reward granting (signed)
- **With Redis**: Streams for cross-service events (inventory_changed, season_ended)
- **With PostgreSQL (meta)**: accounts, storage, leaderboard data

## Documentation Requirements
- `docs/README.md` — Module overview, how to build plugin, how to deploy to Nakama
- `docs/API.md` — All RPCs with request/response, all hooks registered
- `docs/DESIGN.md` — Transaction safety, idempotency, rate limiting decisions
- `docs/RUNBOOK.md` — Deploy plugin, rollback, debug economy issues
- `CHANGELOG.md` — Every change logged

## File Structure Target
```
nakama/
  go.mod
  CLAUDE.md
  CHANGELOG.md
  docs/
    README.md
    API.md
    DESIGN.md
    RUNBOOK.md
  main.go              # InitModule — register all hooks/RPCs
  auth/
    auth.go            # device, email, social handlers
    session.go         # token management
  economy/
    transaction.go     # BuyItem, atomic TX
    wallet.go          # credit, debit, balance
    inventory.go       # item operations
    idempotency.go     # duplicate guard
  leaderboard/
    leaderboard.go     # write score, query ranking
    season.go          # archive, reset, rewards
  social/
    party.go           # Party API wrappers
    friends.go         # Friends operations
    chat.go            # Chat channel management
    guild.go           # Groups API
    presence.go        # Online status
  matchmaking/
    queue.go           # Matchmaking logic
  notification/
    notification.go    # Push + WS notifications
  internal/
    rpc_reward.go      # Internal RPC for GameServer rewards
    rpc_verify.go      # Signature verification
```
