-- KEYS[1] = vehicle:{id}
-- ARGV: version, state, plate, stateChangedAt
-- Returns 1 when applied, 0 when rejected (deleted, duplicate or stale). Spec §6, ADR-0002/0003.
local cur = redis.call('HMGET', KEYS[1], 'deleted', 'stateVersion')
if cur[1] == '1' then return 0 end
if cur[2] and tonumber(ARGV[1]) <= tonumber(cur[2]) then return 0 end
redis.call('HSET', KEYS[1],
  'state', ARGV[2],
  'plate', ARGV[3],
  'stateChangedAt', ARGV[4],
  'stateVersion', ARGV[1])
return 1
