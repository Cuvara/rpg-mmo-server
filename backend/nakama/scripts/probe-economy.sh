#!/usr/bin/env bash
# Live probe of the economy RPC boundary after a deploy (audit 2026-09-07
# F01/F02/F06/F07). Dependency-free: bash + curl + python3.
#
# Creates a throwaway device account, then proves against the RUNNING Nakama:
#   F01  reward_kills / reward_kill / submit_kill reject a client session (403 server-only)
#   F06  http_key grant -> status=granted; replay of the same batch_id -> replayed=true,
#        wallet unchanged; kills=1001 -> code 11; missing batch_id -> rejected
#   F02  a client cannot write kills_alltime directly; the server-side score is 3
#
# Env:  NAKAMA_URL (default http://localhost:7350)
#       NAKAMA_SERVER_KEY (client socket.server_key, default defaultkey)
#       NAKAMA_HTTP_KEY (runtime.http_key, default defaulthttpkey)
#       NAKAMA_CONTAINER (optional; if set and docker is available, tails leaderboard log lines)
# Exit code 0 iff every check passed. Leaves one probe account and 30 gold behind.
set -u
N=${NAKAMA_URL:-http://localhost:7350}
SKEY=${NAKAMA_SERVER_KEY:-defaultkey}
HKEY=${NAKAMA_HTTP_KEY:-defaulthttpkey}
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
OUT="$TMP/body"

pass=0; fail=0
ok(){ echo "  PASS  $1"; pass=$((pass+1)); }
ko(){ echo "  FAIL  $1"; fail=$((fail+1)); }
# j EXPR: print a Python expression evaluated against the JSON on stdin as d.
j(){ python3 -c 'import sys,json
d=json.load(sys.stdin)
try: print(eval("d"+sys.argv[1]))
except Exception as e: print("ERR",e)' "$1"; }

# call RPC "Header: value" RAW_JSON -> prints http code; body in $OUT.
# Nakama's REST endpoint wants the payload as a JSON *string*.
call(){
  curl -s -o "$OUT" -w '%{http_code}' -H "$2" -H 'Content-Type: application/json' \
    -X POST "$N/v2/rpc/$1" \
    --data-binary "$(python3 -c 'import json,sys;print(json.dumps(sys.argv[1]))' "$3")"
}
# call_hk RPC RAW_JSON -> server-to-server call; &unwrap sends/receives the bare payload.
call_hk(){
  curl -s -o "$OUT" -w '%{http_code}' -H 'Content-Type: application/json' \
    -X POST "$N/v2/rpc/$1?http_key=$HKEY&unwrap" --data-binary "$2"
}
wallet(){ curl -s -H "Authorization: Bearer $TOK" "$N/v2/account" | j "['wallet']"; }

echo "== probe account"
DEV="probe-$(date +%s)"
curl -s -u "$SKEY:" -X POST "$N/v2/account/authenticate/device?create=true&username=probe$RANDOM" \
  -d "{\"id\":\"$DEV-xxxxxxxx\"}" > "$TMP/auth.json"
TOK=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("token",""))' "$TMP/auth.json")
if [ -z "$TOK" ]; then ko "device auth: $(cat "$TMP/auth.json")"; echo "probe: $pass passed, $fail failed"; exit 1; fi
UID_=$(python3 -c 'import base64,json,sys
t=sys.argv[1].split(".")[1]; t+="="*(-len(t)%4)
print(json.loads(base64.urlsafe_b64decode(t))["uid"])' "$TOK")
ok "device auth, user=$UID_"

B1="probe-batch-$(date +%s%N)"
body="{\"user_id\":\"$UID_\",\"kills\":3,\"map_id\":\"map_01\",\"batch_id\":\"$B1\"}"

echo "== F01 client session must be rejected"
c=$(call reward_kills "Authorization: Bearer $TOK" "$body")
[ "$c" = 403 ] && grep -q 'server-only' "$OUT" && ok "reward_kills via session -> 403 server-only" || ko "reward_kills via session -> $c $(cat "$OUT")"
c=$(call reward_kill "Authorization: Bearer $TOK" "{\"user_id\":\"$UID_\",\"map_id\":\"map_01\"}")
[ "$c" = 403 ] && ok "reward_kill via session -> 403" || ko "reward_kill via session -> $c $(cat "$OUT")"
c=$(call submit_kill "Authorization: Bearer $TOK" "{\"user_id\":\"$UID_\"}")
[ "$c" = 403 ] && ok "submit_kill via session -> 403" || ko "submit_kill via session -> $c $(cat "$OUT")"

echo "== F06 server grant, replay, wallet"
w0=$(wallet)
c=$(call_hk reward_kills "$body"); r1=$(cat "$OUT"); echo "     grant: $c $r1"
st=$(echo "$r1" | j "['status']"); rp=$(echo "$r1" | j "['replayed']"); g1=$(echo "$r1" | j "['gold']")
[ "$c" = 200 ] && [ "$st" = granted ] && [ "$rp" = False ] && ok "first grant status=granted replayed=false gold=$g1" || ko "first grant: $c $r1"
w1=$(wallet); echo "     wallet before=$w0 after=$w1"
c=$(call_hk reward_kills "$body"); r2=$(cat "$OUT"); echo "     replay: $c $r2"
rp=$(echo "$r2" | j "['replayed']"); g2=$(echo "$r2" | j "['gold']")
[ "$c" = 200 ] && [ "$rp" = True ] && [ "$g2" = "$g1" ] && ok "replay same batch_id -> replayed=true gold=$g2" || ko "replay: $c $r2"
w2=$(wallet)
[ "$w1" = "$w2" ] && ok "wallet unchanged by replay ($w2)" || ko "wallet moved on replay: $w1 -> $w2"
c=$(call_hk reward_kills "{\"user_id\":\"$UID_\",\"kills\":1001,\"map_id\":\"map_01\",\"batch_id\":\"$B1-big\"}")
[ "$c" != 200 ] && grep -q '"code":11' "$OUT" && ok "kills=1001 rejected with code 11 ($c)" || ko "kills=1001: $c $(cat "$OUT")"
c=$(call_hk reward_kills "{\"user_id\":\"$UID_\",\"kills\":1,\"map_id\":\"map_01\"}")
[ "$c" != 200 ] && ok "missing batch_id rejected ($c)" || ko "missing batch_id accepted: $(cat "$OUT")"

echo "== F02 leaderboard authoritative"
c=$(curl -s -o "$OUT" -w '%{http_code}' -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
  -X POST "$N/v2/leaderboard/kills_alltime" -d '{"score":"999"}')
[ "$c" != 200 ] && ok "client record write rejected ($c $(head -c 80 "$OUT"))" || ko "client wrote leaderboard directly: $(cat "$OUT")"
c=$(curl -s -o "$OUT" -w '%{http_code}' -H "Authorization: Bearer $TOK" "$N/v2/leaderboard/kills_alltime?owner_ids=$UID_")
sc=$(j "['owner_records'][0]['score']" < "$OUT")
[ "$sc" = 3 ] && ok "server-side score for probe user = 3" || ko "score = $sc ($c $(head -c 120 "$OUT"))"

if [ -n "${NAKAMA_CONTAINER:-}" ] && command -v docker >/dev/null 2>&1; then
  echo "== nakama startup path ($NAKAMA_CONTAINER)"
  docker logs "$NAKAMA_CONTAINER" 2>&1 | grep -iE 'leaderboard|LEADERBOARD_MIGRATE|recreate|authoritative' | tail -4 | cut -c1-160
fi

echo; echo "probe: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
