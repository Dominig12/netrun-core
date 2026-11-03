# Архитектурные решения автономного ядра

Актуальная согласованная граница всего переноса: [полное владение NETRUN в Core](ownership.md).
Она включает сущности, права, файлы, хранилища, сетевые объекты, FIFO и моды. Описанная ниже
ранняя интеграция топологии не означает завершённого переноса остальных production DM подсистем.

Дата: 2026-11-02. Реализованы mirror, синхронизация, модули сетевой симуляции,
DM façade и автоматические build/launch paths. Результаты проверок и ограничения
окружения приведены в [verification.md](verification.md).

## Направление зависимостей

```text
Host → Protocol → Core
  └─────────────→ Core
Tests → Host, Protocol, Core
```

Core содержит доменную модель, правила публикации фактов и сетевую симуляцию.
JSON, файлы, потоки ввода-вывода и CLI расположены снаружи. Topology, Routing,
Transport, Discovery и Projection используют доменные представления и игровое
время; не обращаются к DM-объектам и не выполняют физические эффекты.

В первой реализации нет фонового mutable-доступа к mirror. Это позволяет
обрабатывать причинный порядок без locks и не выдавать частично применённые
пакеты. Параллельные расчёты потребуют отдельных неизменяемых snapshots с
проверкой версии перед публикацией. Read-only wrapper над словарём сам по себе
такой snapshot не создаёт.

## Расширение реализованной модели

`IWorldMirror` — граница применения и чтения. Данные — неизменяемые records.
`WorldMirror` закрыт от наследования: протокол публикации и инварианты нельзя
случайно переопределить. Поведение расширяется композицией обработчиков, а не
вмешательством наследника во внутренние словари.

`NodeChangeHandler<TChange>` нормализует собственный тип входа в полное
`NodeMutation`. Он детерминирован, не хранит ссылки на mutable-мир, не выполняет
I/O и не изменяет mirror. Некорректный результат повторно отклоняется ядром.
Ошибка самого доверенного обработчика — ошибка программирования; ядро не
превращает произвольное исключение расширения в успешное применение.

Нормализация выполняется до dedup. Fingerprint описывает нормализованные
эффекты вместе с epochs, sequence, временем и порядком. Два C#-контракта с
одинаковым полным эффектом эквивалентны для этого модуля. Обработчик не должен
иметь скрытых эффектов за пределами `NodeMutation`. Изменение конфигурации
обработчиков требует новой сессии.
Повторный вход в Apply запрещён и защищён guard, в том числе при исключении
обработчика. Это не синхронизация потоков: single-owner остаётся обязательным.

Эта точка расширения предназначена для фактов узлов. Сектора и сервисы имеют
собственные таблицы фактов с общей атомарной публикацией; запросы обслуживаются
Transport и не кодируются как фальшивые узлы. `INetworkWorldView` даёт единый
read-only контракт потребителям фактов. `IRouteProvider`/`IRouteCostPolicy`
отделяют transport от поиска маршрута и правил стоимости.

## Стоимость и память

- Валидация, fingerprint и staging масштабируются с числом записей/байтов пакета.
  Полный словарь мира перед каждым delta не копируется и не сканируется.
- Обращение к ID/обработчику — словарь; публикация касается только изменённых ID.
- Replay хранит только 32-байтовые SHA-256 fingerprints и sequence в FIFO,
  а не payload или весь журнал входов. Вытеснение — O(1).
- Tombstones входят в общий лимит ID и не удаляются по TTL: забытое удаление
  могло бы воскресить старое поколение. Достижение лимита требует новой сессии
  с будущим resync; без согласованного watermark «чистить старое» нельзя.
- На пакет выделяются ограниченный массив нормализованных изменений, staging
  и буфер fingerprint. Это прозрачный исходный вариант; оптимизация allocations
  допустима после измерений, без изменения replay-семантики.

Атомарность означает отсутствие публикации при ожидаемых validation/capacity
ошибках. OOM/авария процесса не являются поддерживаемым механизмом rollback.

## SDK и среда

.NET 10 LTS выбран потому, что поддержка .NET 8 заканчивается 2026-11-10.
Источник: [официальная политика Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy).
SDK 10.0.403 зафиксирован для воспроизводимости, TFM `net10.0`.
Production-зависимостей NuGet нет; тестовые зависимости зафиксированы lockfile.
Публичный API компилируется с XML-документацией и warnings-as-errors; правила,
единицы и ownership комментируются и у внутренних полей/методов.

Проверенная локальная среда: Windows 10 x64, SDK установлен в пользовательский
cache без изменения системного PATH. Host — отдельный процесс, поэтому
разрядность библиотеки не обязана совпадать с DreamDaemon, но ABI нативного моста
обязан совпадать. Целевое окружение — Linux Docker; локальное — Windows процессы.
CI описывает сборку на Windows и Linux; успешный локальный прогон на Windows
не означает, что GitHub CI уже выполнен.

## Что остаётся за пределами пакета

Loopback handshake, сервисные/секторные факты, output ACK/replay,
DM-экспортёр, native-мост, heartbeat, граф, маршруты и machine invocation
реализованы. Полный live cutover, Linux BYOND runtime и численное измерение
end-to-end стоимости требуют отдельной эксплуатационной проверки.

`WorldSynchronization` отделён от узлового хранилища и управляет snapshot,
checksum, catch-up и заменой опубликованной модели. Replay-журнал и правила
валидации вынесены в общие внутренние компоненты после появления второго
потребителя. Полный словарь не копируется на каждом chunk; commit сравнивает
инкрементальный digest за O(1). Барьер catch-up публикует mirror заменой ссылки.
См. [контракт синхронизации](synchronization.md).

Сохранён исходный `replay` для прямой проверки mirror; `replay-sync` требует
полного снимка. Автоматического fallback между контрактами нет. Восстановление
после `resync_required` создаёт новый binding и полный snapshot. Native ownership
barrier не переносит старые mutating requests в новую сессию.

## Сетевые вычисления и native façade

`NetworkTopology` публикует immutable graphs. Пространственный индекс и
binding policy выбирают APC; KD/Boruvka строят физический mesh без полного
попарного перебора. `NetworkAvailability` отделяет питание от геометрии.
`NetworkRouter` использует weighted Dijkstra и bounded pair cache,
`SourceRouteTreeProvider` — bounded source cache. Discovery запрашивает
reachability source tree без материализации пути для каждого кандидата.
Communication overlay учитывает directed ports, частоту и native relay flags;
обычный сетевой путь имеет приоритет.

`NetworkTransport` отвечает за очередь, simulation time, ordering, отмены,
backpressure и invocation lifecycle. `ServiceDiscovery`, `ProjectionBroker` и
`GraphSynchronizationBroker` делят с ним единый Runtime budget. Стратегии
стоимости, привязки и route providers расширяются через интерфейсы и композицию;
Core не хранит native references и не вызывает игровые процедуры.

DM экспортирует raw node/sector/service facts и принимает incremental graph
operations по 32 за страницу. ACKed baseline и один captured target исключают
перезапуск whole-graph snapshot при каждом движении leaf. Infrastructure stamps
отделяют общие forwarding изменения от локальных revisions source/target.
UI/машины читают импортированные bindings и predecessor trees; штатный external
режим не запускает DM topology builders, nearest-APC scan или Dijkstra.
Native authorization и физическое выполнение действия остаются в игре.


## Persistent Core-owned service watches

The authoritative C# service directory now accepts `service_watch` and `service_unwatch`
through the existing ordered RuntimeInput/ACK stream. A watch is keyed by an exact
source service generation, destination family and optional tag:

```json
{"kind":"service_watch","request_id":"watch:1",
 "source":{"service_id":"source-instance","generation":"1"},
 "destination_family":"atmos.controller","recipient_tag":null}
```

Once admitted, the Core emits asynchronous `service_watch_event` output entries
through the ordinary output replay journal, without a DM polling loop:

```json
{"binding_epoch":"binding","request_id":"watch:1","kind":"service_watch_event",
 "change":"added","family":"atmos.controller","tag":null,
 "service":{"service_id":"controller-instance","generation":"1"},
 "online":true,"directory_revision":"100"}
```

The initial snapshot consists of bounded `added` observations followed by one
`snapshot_end` event. Subsequent service fact mutations yield `added`,
`updated` or `removed` events. A tag or generation replacement emits removal
of the old identity before addition of the new one. `source_unavailable` and
`resync_required` terminate a watch; the native owner must subscribe again.
A watch has no fixed expiry and is cancelled explicitly using `service_unwatch`
with the original `request_id`. All notifications are bounded; overflow
produces `resync_required` rather than silently dropping changes.

These notifications report **catalog advertisements, not network reachability**.
They grant no native authority, do not call DM handlers, and do not replace live
routing, permissions, generation or operation-contract checks for
`request_submit -> service_invocation -> invocation_result -> request_completed`.
A service state change must be exported as a revised ServiceFact to generate an
`updated` event; unchanged re-advertisements are ignored.

The DM adapter retains only a weak native owner and delivers notifications outside
the frame decoder. This incrementally replaces repeated full discovery polling
with core-owned event subscriptions while the Core remains responsible for
network queries and route computation, and DM performs only native gameplay actions.
