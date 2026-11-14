-- ADR-33 S1: register a server only if servers:id:{id} is absent or names the same game.
-- KEYS[1] servers:id:{server_id}; KEYS[2] (optional) the map index key.
-- ARGV[1] default game id; ARGV[2] ttl in ms; ARGV[3..10] server_id, map_id, addr,
-- transport, capacity, player_count, identity_key, game_id.
if redis.call('EXISTS', KEYS[1]) == 1 then
	local current = redis.call('HGET', KEYS[1], 'game_id')
	if not current or current == '' then
		current = ARGV[1]
	end
	if current ~= ARGV[10] then
		return 0
	end
end
redis.call('HSET', KEYS[1], 'server_id', ARGV[3], 'map_id', ARGV[4], 'addr', ARGV[5], 'transport', ARGV[6], 'capacity', ARGV[7], 'player_count', ARGV[8], 'identity_key', ARGV[9], 'game_id', ARGV[10])
redis.call('PEXPIRE', KEYS[1], ARGV[2])
if #KEYS > 1 then
	redis.call('SADD', KEYS[2], ARGV[3])
end
return 1
