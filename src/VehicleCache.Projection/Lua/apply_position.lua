-- KEYS[1] = vehicle:{id}
-- ARGV: recordedAtUs, positionId, lat, lng, recordedAt
-- Returns 1 when applied, 0 when rejected (deleted, duplicate or older (recordedAtUs, positionId)). Spec §6, ADR-0002/0003.
-- recordedAtUs (~1.8e15) and ids stay below 2^53, so Lua's double comparison is exact.
local cur = redis.call('HMGET', KEYS[1], 'deleted', 'recordedAtUs', 'positionId')
if cur[1] == '1' then return 0 end
if cur[2] then
  local newAt, newId = tonumber(ARGV[1]), tonumber(ARGV[2])
  local curAt, curId = tonumber(cur[2]), tonumber(cur[3])
  if newAt < curAt or (newAt == curAt and newId <= curId) then return 0 end
end
redis.call('HSET', KEYS[1],
  'lat', ARGV[3],
  'lng', ARGV[4],
  'recordedAt', ARGV[5],
  'recordedAtUs', ARGV[1],
  'positionId', ARGV[2])
return 1
