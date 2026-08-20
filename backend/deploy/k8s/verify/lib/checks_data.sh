#!/usr/bin/env bash
# Layer 2 -- data tier reachability.
#
# The exec prefixes are configuration, not constants: today the data tier runs
# in docker compose ("docker exec rpg-redis"), tomorrow in the cluster
# ("kubectl --context ... exec -n rpg-data sts/redis --"). Only the prefix
# changes; the assertions do not.

# --- Postgres (meta, owned by Nakama) -----------------------------------
check_pg_meta() {
  if [ -z "${VERIFY_PG_META_EXEC:-}" ]; then
    skip "no meta-Postgres exec prefix (VERIFY_PG_META_EXEC empty) -- meta DB UNVERIFIED"
    return
  fi
  local out
  out=$($VERIFY_PG_META_EXEC psql -U "$VERIFY_PG_META_USER" -d "$VERIFY_PG_META_DB" -tAc \
    "select current_database()||'|'||(select count(*) from information_schema.tables where table_schema='public')" 2>&1)
  if [ $? -ne 0 ] || [ -z "$out" ]; then
    fail "meta Postgres did not answer" "a psql connection as $VERIFY_PG_META_USER to $VERIFY_PG_META_DB" \
      "$(echo "$out" | tail -2)" "$VERIFY_PG_META_EXEC (container/pod up? credentials?)"
    return
  fi
  local db="${out%%|*}" tables="${out##*|}"
  if [ "$db" != "$VERIFY_PG_META_DB" ]; then
    fail "meta Postgres served the wrong database" "$VERIFY_PG_META_DB" "$db" "the DSN / POSTGRES_DB of that instance"
    return
  fi
  # Nakama creates its own schema on first boot; an empty public schema means
  # this is a fresh volume Nakama has never migrated, which looks identical to
  # "healthy" from a PING.
  if [ "$tables" -lt 5 ]; then
    fail "meta Postgres has no Nakama schema" "public schema with Nakama's tables (>=5)" \
      "$tables table(s) in public" "nakama container logs -- did its migration run?"
    return
  fi
  pass "database=$db public_tables=$tables"
}

# --- Postgres (game state, written only by the game server) --------------
check_pg_game() {
  if [ -z "${VERIFY_PG_GAME_EXEC:-}" ]; then
    skip "no game-Postgres exec prefix (VERIFY_PG_GAME_EXEC empty) -- game-state DB UNVERIFIED"
    return
  fi
  local out
  out=$($VERIFY_PG_GAME_EXEC psql -U "$VERIFY_PG_GAME_USER" -d "$VERIFY_PG_GAME_DB" -tAc \
    "select current_database()||'|'||coalesce((select max(version)::text from schema_migrations),'none')||'|'||(select count(*) from information_schema.tables where table_schema='public' and table_name='player_states')" 2>&1)
  if [ $? -ne 0 ] || [ -z "$out" ]; then
    fail "game Postgres did not answer" "a psql connection as $VERIFY_PG_GAME_USER to $VERIFY_PG_GAME_DB" \
      "$(echo "$out" | tail -2)" "$VERIFY_PG_GAME_EXEC; GAME_DB_URL on the game server"
    return
  fi
  local db ver tbl
  IFS='|' read -r db ver tbl <<<"$out"
  if [ "$db" != "$VERIFY_PG_GAME_DB" ]; then
    fail "game Postgres served the wrong database" "$VERIFY_PG_GAME_DB" "$db" "the GAME_DB_URL the game server is given"
    return
  fi
  if [ "$ver" != "${VERIFY_GAME_MIGRATION:-1}" ]; then
    fail "game schema is at the wrong migration version" "schema_migrations max version = ${VERIFY_GAME_MIGRATION:-1}" \
      "$ver" "backend/gameserver-dotnet migrations; smoketest --expect-migration-version"
    return
  fi
  if [ "$tbl" != "1" ]; then
    fail "player_states table missing" "one public.player_states table" "$tbl" "the game server's migration run"
    return
  fi
  pass "database=$db schema_migrations=$ver player_states=present"
}

# --- Redis ---------------------------------------------------------------
check_redis_ping() {
  if [ -z "${VERIFY_REDIS_EXEC:-}" ]; then
    skip "no Redis exec prefix (VERIFY_REDIS_EXEC empty) -- Redis UNVERIFIED"
    return
  fi
  local out
  out=$($VERIFY_REDIS_EXEC redis-cli ${VERIFY_REDIS_AUTH:+-a "$VERIFY_REDIS_AUTH"} PING 2>&1 | tr -d '\r')
  if [[ "$out" != *PONG* ]]; then
    fail "Redis did not answer PING" "PONG" "$out" "$VERIFY_REDIS_EXEC; REDIS_ADDR/REDIS_PASSWORD on gateway + gameserver"
    return
  fi
  pass "PING -> PONG"
}

# ADR-4: this Redis holds the server registry and the event stream. It is not a
# cache and nothing in it is regenerable, so an eviction policy other than
# noeviction silently drops live server registrations under memory pressure --
# which presents as "the map has no server" long after the cause.
check_redis_noeviction() {
  if [ -z "${VERIFY_REDIS_EXEC:-}" ]; then
    skip "no Redis exec prefix (VERIFY_REDIS_EXEC empty) -- eviction policy UNVERIFIED"
    return
  fi
  local out policy
  out=$($VERIFY_REDIS_EXEC redis-cli ${VERIFY_REDIS_AUTH:+-a "$VERIFY_REDIS_AUTH"} CONFIG GET maxmemory-policy 2>&1 | tr -d '\r')
  policy=$(echo "$out" | tail -1)
  if [ "$policy" != "noeviction" ]; then
    fail "Redis may evict registry and event data" "maxmemory-policy = noeviction (ADR-4)" \
      "maxmemory-policy = ${policy:-<no answer>}" \
      "the redis config/args in the compose file or the cluster ConfigMap; backend/docs/ARCHITECTURE-DECISIONS.md ADR-4"
    return
  fi
  pass "maxmemory-policy=noeviction (ADR-4 satisfied)"
}

# --- Nakama --------------------------------------------------------------
check_nakama_health() {
  local code
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "${VERIFY_NAKAMA_URL%/}/healthcheck" 2>&1)
  if [ "$code" != "200" ]; then
    fail "Nakama /healthcheck did not return 200" "200" "${code:-<no response>}" \
      "docker logs rpg-nakama / kubectl logs deploy/nakama; VERIFY_NAKAMA_URL=$VERIFY_NAKAMA_URL"
    return
  fi
  pass "GET ${VERIFY_NAKAMA_URL%/}/healthcheck -> 200"
}

# Process liveness is not plugin liveness: Nakama serves /healthcheck perfectly
# with its Go plugin unloaded, and the first symptom is every client failing to
# get a gateway token. This asserts the RPC exists AND that the token it signs
# verifies under the same secret the gateway uses.
check_nakama_plugin() {
  local out
  out=$(run_probe token 2>&1)
  if [[ "$out" != *"RESULT=ok"* ]]; then
    fail "gateway_token RPC did not return a locally verifiable token" \
      "RESULT=ok with a user_id, and a JWT that verifies under JWT_SECRET" \
      "$(echo "$out" | head -3)" \
      "nakama logs for plugin load errors; JWT_SECRET on Nakama vs the gateway"
    return
  fi
  pass "${out##*RESULT=ok }" 
}

register data.pg_meta       2 "meta Postgres serves Nakama's schema" \
  "the meta instance answers, on the expected database, with a populated public schema" \
  "nothing about the CONTENT being correct, and nothing about Nakama's own connection to it" \
  check_pg_meta
register data.pg_game       2 "game Postgres at the expected schema version" \
  "the game-state instance answers on the expected database, schema_migrations is at the expected version and player_states exists" \
  "nothing about rows being written -- flow.smoke proves that" \
  check_pg_game
register data.redis_ping    2 "Redis answers" \
  "the Redis the gateway and game servers share is reachable and accepting commands" \
  "nothing about its contents or its durability configuration" \
  check_redis_ping
register data.redis_policy  2 "Redis will not evict registry data (ADR-4)" \
  "maxmemory-policy is noeviction, so registry entries and stream data cannot be dropped under memory pressure" \
  "nothing about maxmemory itself, nor about persistence (RDB/AOF)" \
  check_redis_noeviction
register data.nakama_health 2 "Nakama answers /healthcheck" \
  "the Nakama process is up and serving HTTP" \
  "NOTHING about the Go plugin -- Nakama returns 200 with no plugin loaded" \
  check_nakama_health
register data.nakama_plugin 2 "Nakama Go plugin is loaded and signing" \
  "the gateway_token RPC exists, returns a signed token, and that token verifies locally under the shared JWT_SECRET" \
  "nothing about the gateway accepting it -- flow.smoke and refusal.* prove that hop" \
  check_nakama_plugin
