# Перенос сущностей NETRUN

Документ фиксирует контракты и границы переноса существующей механики. Наличие этого документа
не означает, что перечисленные DM-кластеры уже перенесены. Исходное поведение задают
`MOLOT-BlueMoon-Station/net-feature/modular_bluemoon/code/modules/netrun/` и его unit tests.

## Владельцы состояния

| Кластер DM | Состояние и вычисления, принадлежащие Core | Что остаётся native-прокси |
| --- | --- | --- |
| `entity/entity.dm` | residence, integrity, shield/stun, hop cooldown, host loss/ejection decision | camera mob, `forceMove`, perspective/vision, chat, ghostize, neural body effects |
| `entity/navigation.dm` | `path_to` BFS, destination/path, operation generation, pursuit/replanning, threats | проверка актуального node/host и native ACL перед физическим переносом, notification |
| `entity/bot_brain.dm` | goal queue (до четырёх), memory, blacklist 32, visited 64, enemies 16, planning, target/program ranking, retreat | delivered native command acknowledgement, rendering/log text |
| `entity/bot_goals.dm`, `bot_programs.dm` | archetype planning, travel/guard/repair/salvage/recall, expiry | native execution of already-authorized ordinary commands and real file transfer |
| `entity/ice.dm`, `ice_agents.dm` | ordered lifecycle/availability/cast/local-combat/trace/pursuit policies, cooldowns, aggro, dormant/reactive state, patrol | physical deployment, final native access/presence checks, body/organ feedback |
| `entity/turn_combat.dm`, `turn_fortress.dm` | per-node encounter, participants, decision generation/deadline, AP/cooldowns, carried AP, stun immunity, shields, idle closure | current physical contact/breach/fortress lease validation, native turret side effects |
| `entity/combat.dm`, `combatant.dm`, `core/programs.dm` | legal abstract contacts, program admission/status/damage, program catalog, cooldowns, read models | native principal/file ownership proof, raw client-ref resolution, world effects |
| `entity/persona.dm`, `factions.dm` | presentation patch validation/style, faction relations and membership, visitor pool selection | mind/principal lifetime mapping, BYOND name filtering, UI authorization |
| `entity/behaviors.dm`, `modules.dm`, `patterns.dm`, `actors.dm` | deterministic ordered module host, capability dependencies, temperament/cooperation | signal-to-fact adapters, mob/console representation lifecycle |
| `entity/hacking_agents.dm`, `visitor_navigation.dm`, `police.dm` | frontier selection, hack/visitor goals, incidents, jurisdiction and assignment | existing native breach/command delivery adapters until the underlying breach model migrates |
| `entity/spawner.dm`, `station_invasions.dm` | population census/caps, waves/objectives, spawn and despawn decisions | allocating mobs/datums/ghost roles and applying native machinery effects |
| `entity/file_exchange.dm`, `recycling*.dm`, `salvage.dm` | inventory metadata, fit/ranking/recycle decisions, one-shot transfer state | actual native file ownership/transfer and resulting station research/resource mutation |
| `entity/dive_link.dm`, `audio.dm` | logical return-link state and communication decisions | carbon body/implant/mind, organs, speech/intercom, observers and camera sources |

Read-only network projections do not make the DM entity layer pure: `navigation.path_to` still performs
a complete BFS and `bot_brain.find_nearby_node` a 48-node BFS on the game thread. Both belong in Core.

## Existing semantics that must survive

- One actor owns at most one navigation operation, one ordinary command and one pending cast.
  Cancellation increments an operation generation; delayed replies cannot revive an old operation.
- NPCs never bypass traversal or command ACLs. GUEST/keys permit ordinary travel; fortress gates
  still require USER. Every delivered hop/command revalidates the current physical generation.
- NPC thinking is suspended while possessed by a player, stunned, returning or awaiting its turn.
  Completion and deadlines wake work; idle guard and command waits do not poll every think interval.
- Bot archetypes include raider/scavenger, custodian/police/recycler and owned
  sentinel/interceptor/mender/surveyor/collector/hacker/breacher. Visitor factions have finite
  survey/salvage/sabotage objectives and depart through an actual reachable relay.
- Combat is local to an encounter, not a global clock: 3 AP, 120 ds decision deadline, 3 quiet rounds
  close the encounter. A stale decision token cannot spend a newer turn. Joining accepts an
  encounter token or a previous decision from that same encounter, and does not recruit a target.
- Leaving an unfinished encounter carries remaining AP (or zero for a non-current participant)
  until the old deadline. Movement cannot refill AP. Deferred ICE casts hold the turn until resolve
  or cancellation. A timed-out cast cannot execute later.
- Turn cooldowns use the actor's own-turn counter. Stun skips the configured number of turns and
  grants immunity until a real turn has completed. A weaker shield cannot replace or extend a
  stronger active shield; shield duration also uses the actor's own turns.
- Remote countertrace contacts belong to the exact breach and original source residence. A new
  breach cannot inherit a pending attack; raw object references do not prove contact.
- NPCs retain a valid combat target; affordable program choice ranks effective benefit per AP and
  discounts consumables by 0.8. Low-health retreat/shield/repair and proactive stun keep their
  current priority. ICE uses its existing damage factor and minimum telegraph.
- Native cleanup must cancel outstanding commands, casts, navigation and subscriptions. Recall
  and relaunch must not repeat a one-shot device mutation or duplicate loot.
- Personal factions belong to the real principal across devices; cosmetic persona changes grant
  neither routing permissions nor machine authority.

## Input and output contract

Use the existing ordered `RuntimeInput` sequence, simulation time and replay ledger. Actor messages
must be first-class typed commands, not arbitrary plugin JSON or raw DM references.

1. `ActorKey(id, generation)` names a physical native representation. `principal` and `session` are
   separate opaque identities. Reuse of a native id requires a higher generation.
2. Native facts carry a monotone revision and immutable host `NodeKey`, possession/online/lifetime,
   principal, authorization/inventory metadata and observable native capabilities. Core owns its
   simulation values; native upserts must not overwrite damage/AP/goals with stale projections.
3. Player and NPC intents carry caller identity, request serial, operation generation and optional
   encounter/decision token. Public DM procs can retain their names as enqueue facades, but a
   synchronous `TRUE` means admitted for delivery, not completed.
4. An emitted native effect carries binding epoch, unique effect id, exact actor/node generations,
   expected native revision and bounded typed arguments. DM validates these again immediately
   before execution and replies with `effect_result`. Native ACL failures are facts; the library
   does not invent authority or retry a mutation blindly.
5. At most one unresolved mutating effect per actor. Movement/command/recall does not advance to
   the next step until the matching effect result. Late, repeated and old-generation results are
   harmless. A timed-out uncertain mutation is never resubmitted under a new id automatically.
6. UI queries return versioned actor/combat/navigation/module read models. Existing fields in
   `entity_data`, `combat_payload`, navigation payload and contact cards are projected from those
   models. A UI refresh never runs planning/BFS/target selection in DM.
7. Rebind clears effect queues and republishes native actor facts in the same snapshot/catch-up
   discipline as topology. Effects from an old binding cannot reach replacement objects.

The actor runtime has a single mutable owner. Expensive searches are resumable bounded Pump work,
with immutable graph/native snapshots for optional parallel computation. Worker results must carry
the graph/native revisions and be discarded after invalidation. Socket reader/writer independence
must remain intact; no native callback waits for a simulation result.

## Implemented first seam: navigation and access

`Entities/EntityNavigationBroker` performs the former entity `path_to` BFS in bounded round-robin
steps and emits paged `EntityNavigationPage` read models. It retains the existing minimum-hop
choice, supports the implicit relay bucket without materializing its clique, checks exact node
generations and the same availability/backbone overlay as routing, and revokes pending results
after graph/availability changes. Request replay barriers survive history eviction. A request has
at most one unread output page and releases its workspace on cancellation/terminal generation.

The production request carries `EntityAccessSnapshot`, containing raw native policies, credential
codes, real key-file stamps and original acquired-grant scopes. It does **not** accept a DM-computed
allowed-node list. `Access/EntityAccessAuthority` indexes these facts incrementally and owns the
rules for legitimate node/network levels, scoped grants, traversal and fortress friendship.
Its compatibility rules preserve the original differences between null/empty ANY requirements,
native ALL+ANY object requirements, ID guest access and travel permission, ordinary LAN keys and
acquired GUEST, unsigned direct/residency grants, original scoped epochs and private/station/rogue
gate ownership. Native physical owner relations remain raw adapter facts; their ROOT effect is
computed by Core.

DM `integration/external_entity_access.dm` supplies a cooperative raw-fact cursor and explicit
physical owner/credential adapters. It exports key hashes and original scope stamps, uses weak
opaque identity maps, and aborts a pending capture after binding/fact/inventory-source changes.
The final native action must still compare the actor revision and current physical generations.

`integration/external_entities.dm` is an explicit opt-in adapter: `client.entity_proxy(actor, TRUE)`
creates a weak native proxy, `proxy.register()` registers its initial Core definition, and
`proxy.submit_action(kind, data_json, access_json, expected_revision)` uses the existing auxiliary
transport and shared FIFO task admission. `EntityChanged` updates a projection; `EntityNativeEffect`
alone may move the native representation. The native worker compares exact binding/generations,
residence, physical online state, the acknowledged infrastructure fence and a freshly exported raw
access hash before calling `enter_node`, then sends a distinct effect-result task. It never calls a
DM traversal solver. Duplicate/old effects cannot repeat a native action, including after bounded
outcome-history eviction. Disconnect invalidates these proxies without selecting a local fallback.

The adapter maintains an O(1) count of registered or creation-pending native actors. Once transient
task consumers retire, that demand keeps the existing transport clock alive with empty time-only
inputs when BYOND simulation time advances and no facts/commands are queued. Repeated registration,
teardown and disconnect cannot double-count a representation. This initial implementation may tick
an otherwise idle resident; Core owns all actual stun/shield/lifetime deadline calculations. It does
not schedule actor simulation in DM or rescan the weak proxy map on every tick.

The adapter does not automatically register production entities or disable their existing DM
brains/combat/navigation. Initial registration must happen before authoritative handoff; complete
recovery of existing HP/status and typed death/loot effects require additional migration work.
Consequently the original DM BFS and actor simulation are still present in the production path
until those ownership changes are wired and verified together.

This seam does not complete entity migration. Bot archetype planning, ICE/casts, encounter/AP
ownership, file transfer and remaining command authority still need their existing mechanics
ported. The full raw snapshot is an initial transport surface; follow-up work should store policy
and actor-credential updates in the authoritative runtime catalog so a navigation intent contains
only actor identity/revision and destination instead of repeating station policies.

## Integration points

- `Runtime/RuntimeModel.cs`: typed commands/results and options.
- `Runtime/RuntimeFingerprint.cs`: validate and fingerprint every actor command field before Apply;
  duplicate external sequences keep the existing semantic-equality behavior.
- `Runtime/NetworkRuntime.cs`: own the actor subsystem, apply commands once, Pump it fairly,
  dequeue actor effects/read-model events and dispose/invalidate on binding faults.
- `Protocol/RuntimeJson.cs`: explicit command allowlist, decode/validate/write and round-trip tests.
- `Host/RuntimeOutputJson.cs`: bounded actor event encoding; same output ACK/replay mechanism.
- DM `integration/external_core.dm`, `external_effects.dm`: fact journal/snapshot, effect dispatch,
  native result and lifecycle cleanup. No second actor solver/fallback in DM.

## Suggested implementation split

`src/Netrun.Core/Entities` contains the engine-independent model and simulation, navigation,
combat controller and NPC policies. It consumes typed facts and emits typed effects; it does not
reference Protocol, Host, BYOND, native objects, wall clocks or I/O. The root integration owns
Runtime/Protocol/Host event plumbing and DM proxies. Tests for pure mechanics live in
`tests/Netrun.Tests/EntitySimulationTests.cs`; codec/runtime/DM tests belong with their adapters.

Preserving every archetype requires porting its existing policy and capabilities. A generic
"attack nearest and patrol" substitute does not complete this migration. Keep an explicit list of
unsupported archetypes/capabilities until their real implementation and tests land; never silently
map them to a different bot.

## Verification

Core tests: stale generations/revisions, effect ack dedup and uncertain timeout, graph changes
during navigation, possession suspension, authorization revocation, bounded fairness, no AP refill
through movement, stale input/join token, deferred cast cancellation, own-turn cooldowns,
anti-chain-stun immunity, stronger shield preservation, all archetype planning, recall/file/loot
exactly-once and visitor relay exit.

Integration tests: actor snapshot/catch-up/reconnect; codec round trips and rejected unknown kinds;
native host destruction/replacement while effects are queued; actual physical ACL checks at delivery;
UI read-model versioning and unchanged interaction behavior. Run existing DM entity/ICE/combat/bot
unit tests against proxies and current async native bridge. A full station/load run is required to
claim deployment or end-to-end performance verification.

## Plugin worktree review

The pre-existing uncommitted plugin implementation exposes `INetrunPlugin.Configure` and one
`IRouteCostPolicy` contribution through RuntimeOptions. It is useful composition infrastructure but
does not yet provide actor modules, command/fact registration, assembly loading, author templates
or plugin deployment. `INodeChangeHandler` is still not routed through NetworkRuntime; extending
it also requires updating synchronization frame validation rather than merely passing an array.
The registry should be frozen after configuration and diagnostic lists exposed immutably.
