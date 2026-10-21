---
name: test-behavior
description: How to add or change a Given/When/Then behavior (TB-xx) in docs/03-test-behaviors.md for the Vehicle Cache and link it to an automated test at the right level (unit, Testcontainers integration, e2e). Use when adding a behavior, writing a test for one, or checking traceability.
---

# Add a test behavior

1. Next id: `grep -o "TB-[0-9]*" docs/03-test-behaviors.md | sort -u | tail -1`.
2. Write it in the doc **first**:
   ```markdown
   ### TB-NN — <observable behavior>
   - **Given** <cache/topic/DB state, concrete ids and values>
   - **When** <one event or action>
   - **Then** <exact cache result; script return value when relevant>
   - Automation: <U|I|E> `<TestClass>.<Test_method_name>`
   ```
3. Choose the **lowest level that can prove it**:
   - U — parsing/mapping only (fixtures, no I/O).
   - I — anything about ordering, idempotency, tombstones, convergence: must run the **real Lua** on Testcontainers Valkey.
   - E — only what needs the real pipeline: snapshot, restart, rewind, replay, oracle equality.
4. Test naming: `<Subject>_<condition>_<outcome>` plus `[Trait("TB", "NN")]`.
5. Concrete values (vehicle ids, timestamps) in the test must match the Given/When/Then text.
6. Update the traceability table at the bottom if it covers a brief requirement.
7. A behavior that will not be automated is marked `Automation: cut — <reason>` and listed in the PR description.

Sanity question for every new test: *would a naive "consume → HSET" worker fail it?* If not, it is not testing the hard part.
