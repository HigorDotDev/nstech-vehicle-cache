---
name: valkey-conditional-write
description: The Vehicle Cache's Valkey write rules — hash layout per vehicle, the three Lua compare-and-set scripts (apply_state, apply_position, delete_vehicle), how to call them from StackExchange.Redis, and how to add a new field group without breaking convergence. Use when writing or changing Lua scripts, the cache writer, the cache schema or the reader contract.
---

# Valkey conditional writes

Layout and semantics are defined in `docs/02-spec.md` §5–§6 and ADR-0003/0004. This skill is the *how*.

## Reference scripts

```lua
-- apply_state.lua   KEYS[1]=vehicle:{id}  ARGV: version, state, plate, stateChangedAt
local cur = redis.call('HMGET', KEYS[1], 'deleted', 'stateVersion')
if cur[1] == '1' then return 0 end
if cur[2] and tonumber(ARGV[1]) <= tonumber(cur[2]) then return 0 end
redis.call('HSET', KEYS[1], 'state', ARGV[2], 'plate', ARGV[3], 'stateChangedAt', ARGV[4], 'stateVersion', ARGV[1])
return 1
```

```lua
-- apply_position.lua   KEYS[1]=vehicle:{id}  ARGV: recordedAtUs, positionId, lat, lng, recordedAt
local cur = redis.call('HMGET', KEYS[1], 'deleted', 'recordedAtUs', 'positionId')
if cur[1] == '1' then return 0 end
if cur[2] then
  local nr, np = tonumber(ARGV[1]), tonumber(ARGV[2])
  local r,  p  = tonumber(cur[2]),  tonumber(cur[3])
  if nr < r or (nr == r and np <= p) then return 0 end
end
redis.call('HSET', KEYS[1], 'lat', ARGV[3], 'lng', ARGV[4], 'recordedAt', ARGV[5],
           'recordedAtUs', ARGV[1], 'positionId', ARGV[2])
return 1
```

```lua
-- delete_vehicle.lua   KEYS[1]=vehicle:{id}
if redis.call('HGET', KEYS[1], 'deleted') == '1' then return 0 end
redis.call('DEL', KEYS[1])
redis.call('HSET', KEYS[1], 'deleted', '1')
return 1
```

`HMGET` returns `false` (Lua) for missing fields — `if cur[2] then` covers it. Numbers: Lua uses doubles; µs epoch (~1.8e15) and ids are < 2^53, so comparisons are exact. Pass numbers **as invariant-culture strings** (`ToString(CultureInfo.InvariantCulture)`), lat/lng with `"R"`/round-trip format so the dump is stable.

## Calling from C# (StackExchange.Redis)

- Load each script once: `LuaScript.Prepare(...)` + `.Load(server)` → `LoadedLuaScript`, or keep the SHA and call `db.ScriptEvaluateAsync(sha, keys, values)`; on `NOSCRIPT` reload and retry once (Valkey restart flushes the script cache).
- One `IConnectionMultiplexer` singleton. No `FireAndForget` — the result decides `Applied`/`Rejected` and the offset is stored only after the call returns.

## Adding a new field group (checklist)
1. Is the value derivable purely from event payloads? If not, it does not go in the cache.
2. Define its ordering key — a strict total order from the row (never `ts_ms`/offset).
3. Its script checks `deleted` first, then its own guard, and touches **only its own fields**.
4. Does `delete_vehicle` still wipe it? (`DEL` does.)
5. Add it to the reference projection in the convergence test (TB-10) and to the verifier oracle.
6. Update spec §5/§6 and the reader contract in the README in the same change.

## Anti-patterns
`HSET` from C# · read-compare-write across two round trips · `WATCH/MULTI` retry loops · `EXPIRE` on vehicle keys · storing processing timestamps · `DEL` without leaving the tombstone.
