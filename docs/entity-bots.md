# Владение ботами

`Entities/Bots/BotEngine` переносит состояние `bot_brain.dm`, цели `bot_goals.dm`, профильное
планирование и temperament из DM в Core. Health, shield, stun и residence принадлежат общему
`EntityWorld`; brain получает его immutable `EntityView` и не заводит вторую HP-модель.
Метаданные 17 штатных профилей сохраняют исходные loadout, объём памяти, интервалы, фракцию,
персону и visitor lifetime. Файлы loadout не создаются одним выбором profile: executable/cargo
facts отражают только действительно существующий inventory.

## Контракт владельцев

- `Upsert(BotFact)` принимает возрастающий snapshot revision конкретного `BotKey` и `ActorKey`.
- `SetOrder(key, order, nowDs)` принимает уже аутентифицированный owner-приказ.
- `Wake(key)` добавляет одну FIFO task; повторные сигналы коалесцируются.
- `Advance(nowDs, workBudget)` обрабатывает admitted FIFO tasks. Ожидающие goals держат состояние,
  освобождают worker и ставят один заменяемый simulation deadline; completion создаёт новую task.
- `TryDequeueEffect` передаёт одно typed решение с action serial, actor revision и residence revision.
- `ApplyResult` принимает только собственный ACK. `queued`/`invoked` не равны terminal completion.
- `Query` возвращает immutable goal/state read-model для UI, не вызывая новый think.

`BotContext` собирается **внутри Core** из world graph, authority, inventory и combat owners.
`CanTraverse`, source-tree cost, frontier и hostility являются проекциями этих владельцев:
DM не должен рассчитывать их или выбирать цели. DM передаёт сырые физические facts, запросы
игрока и результаты выданных native effects. `BotNavigate` поступает в общий Core navigation,
`BotCombat` — в `CombatSimulation`, остальные typed decisions — в соответствующие Core owners
либо native adapter. Конечный native adapter проверяет source node, поколения и causal stamps.

На brain допускается один outstanding effect. Загрузка новых facts не заменяет его identity.
После terminal movement/cargo/combat/security effect brain ждёт более свежий Core snapshot,
чтобы старый read-model не породил повторную mutation. После `uncertain` автоматического
повтора нет. Истёкший одноразовый `command` становится `guard`; recall/relaunch не сохраняет его.

## Сохранённые правила

Dispatcher сначала проверяет goal expiry, затем orphan/lifetime, stun, background hack,
combat, pending recall и обычные цели. Сроки абсолютны в simulation DS; stun/чужой ход их
не продлевают. Guard на посту и signal-waiting hack спят до события или точного срока.

Patrol хранит itinerary с lazy Fisher–Yates по 24 candidates; tactical BFS ограничен 48
вершинами; salvage/server выбор — 24 sparse slots, выход — 16 relay, hacker — 12 candidates.
Память посещений/blacklist/enemies/warnings ограничена 64/32/16/16 записями. PRNG хранится
на brain и воспроизводится по BotKey, без системных часов и общего randomized hash.

Salvage сортируется по исходной ценности файла, затем по Core route cost; recycler соблюдает
`RecycleAfterDs`, не отнимает свежие трофеи у игроков и переносит только recovered cargo.
Survey и police reports сохраняют ответ собственной доставленной команды, без доступа к
приватному machine journal. Hack планирует последовательность network GUEST → node USER → hop,
а права получает только от обычного breach owner по фактическому исходу.

Combat policy удерживает законную цель, реагирует на настоящих атакующих, соблюдает собственный
ход/AP/cooldown/status hooks. Пороговые решения, выбор достаточного урона на AP и расходников
используют общие полиморфные `CombatProgram.BotValue`/`CanApply`, а применение — `CombatSimulation`.

Visitor objective приводит к реальному выходу через доступное relay. Первый lifetime expiry
даёт ровно 900 DS departure grace, затем процесс завершается. Поэтому общий lifecycle owner
должен передать bot expiry brain-владельцу до удаления actor. Сетевой выход не телепортирует
процесс при потере всех relay. Начатая sabotage-подготовка навсегда прерывается сменой residence,
stun, боем, потерей USER или отключением узла; один подготовленный effect сам не отключает APC.

Регрессии: `tests/Netrun.Tests/EntityBotTests.cs` (FIFO, causal ACK, watchdog, lifecycle,
loadout, salvage grace, honest breach, delivered reports и shared combat policy).
