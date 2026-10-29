-- KEYS[1] = vehicle:{id}
-- Replaces the hash with an absorbing tombstone {deleted = 1}. Spec §6, ADR-0004.
-- Vehicle ids are never reused, so a delete is terminal and needs no version.
if redis.call('HGET', KEYS[1], 'deleted') == '1' then return 0 end
redis.call('DEL', KEYS[1])
redis.call('HSET', KEYS[1], 'deleted', '1')
return 1
