# Боевой owner Core

`CombatSimulation` владеет encounter, порядком ходов, AP, решениями, cooldown, подготовкой,
щитом и оглушением. `EntityWorld` остаётся единственным registry целостности, residence,
персоны, trace и lifecycle. Runtime не принимает внешние HP/status snapshots для боя:
каждый `CombatActorFact` строится из `_entities.Read(exact ActorKey)`.

## Вход и очередь

`ConfigureActorCombat(requestKey, actor, installedProgramIds)` — trusted inventory bootstrap,
содержащий существующие executable. Выбор `ProfileId` не выдаёт loadout. Неизвестная программа
отклоняется до замены предыдущего состава.

`SubmitCombatIntent(requestKey, intent)` ставит закрытый typed intent в общую FIFO рядом с
entity/module/storage задачами. `Apply` возвращает admission сразу; сам бой выполняется в Core
Pump. Сначала завершаются effects предыдущего решения, затем допускается следующий FIFO head.
World/output pressure сохраняет один pending scalar effect и стабильный `combat.effects` serial:
возобновление не повторяет `CombatSimulation.Apply` и уже применённый damage.

Wire kinds: `combat_actor_configure` и `combat_intent`. Внутренний `intent.kind` — один из
`admit`, `join`, `pass`, `use_program`, `resolve_cast`, `cancel_cast`, `spend_ap`, `resolve_action`.
Все nullable поля обязательны в wire shape; unknown/duplicate поля и неизвестные виды запрещены.
Actor/node generations и producer counters передаются canonical decimal strings.

## Мутации и проекции

Damage/Heal/Trace/Reveal/Shield/Stun превращаются в известные `EntityOperation`. Damage уже
учитывает боевой shield factor, поэтому применяется через `DamageEntity(..., IgnoreShield: true)`.
Shield/Stun применяются в порядке scalar effects: соседний статус берётся у текущего EntityWorld,
а обновляемый — из конкретного effect. Это сохраняет правильный prefix даже при queued ClearShield
и промежуточном HP refresh. Turn-owned сроки хранятся как `long.MaxValue` в Combat и явные flags
в actor read-model; обычный lifecycle timer их не снимает.

Потребитель получает:

- `CombatIntentCompleted` — semantic outcome исходного FIFO запроса и актуальные immutable views;
- `CombatStateChanged` — очередь, AP, decision/join tokens, cooldown и cast проекции;
- `CombatNativeEffect` — только consumption projection или физический biofeedback после Core mutation.

DM не считает урон, turn/AP, cooldown, shield или AI по этим сообщениям. Native adapter проверяет
поколение, residence, node и `EffectId`, затем обновляет физическое представление или выполняет
необходимый BYOND body/organ feedback. Неизвестный native outcome не повторяется автоматически.

Consumable удаляет одну установленную копию `programId` в Core до native notice. Bootstrap списка
ID сохраняет число копий; полное сопоставление `ExecutableId` с persisted file UID должно идти
через authoritative storage owner. Само получение native notice не создаёт новый executable.

## Контакт и движение

Пока допускается same-node local contact. Runtime повторно проверяет точные actor/node поколения
и актуальный Core graph/availability перед intent и delayed damage. Remote breach/siege contact
отклоняется до отдельного Core contact lease. Pending native move отзывает надёжный старый residence:
источник не начинает новый бой, target не получает local hit через неопределённый переход.

После raw ACL, adjacency, expected revision, cooldown и stun проверки движение резервирует один AP
через `SpendCombatAp(1, deferred: true)`. Native ожидание завершает FIFO задачу и освобождает worker;
точный result приходит новой задачей. Success/failure ACK снимает deferral один раз без возврата AP.
Успешный переход сохраняет оставшиеся AP до старого decision deadline при вступлении в новый бой.

Refresh actor facts и combat timers используют общий work budget. Незавершённый refresh cursor
продолжается при росте simulation clock; законченный sweep даёт timer работу перед новым sweep.
Финальный contact check выполняется непосредственно перед authoritative entity mutation.

Проверки: `CombatRuntimeTests` покрывает enqueue/FIFO ordering, replay/conflict, explicit inventory,
single shield scaling, output pressure, consumption, actor generations, strict wire codec,
movement AP/deferral/carried AP и отсутствие refresh starvation.
