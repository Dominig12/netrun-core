# Исходный DM: подтверждённые правила и baseline

Аудит 2026-11-01, upstream `Dominig12/MOLOT-BlueMoon-Station`,
HEAD `5035c8ecab3a9d3a739c8152fd772f5543084169`. Пути `core/...` и `integration/...`
ниже — относительно `modular_bluemoon/code/modules/netrun/`.
Это ограниченный read-only аудит, не полный call graph и не подтверждение parity.

## Правила для будущего переноса

| Область | Подтверждённое поведение | Источник |
|---|---|---|
| Геометрия | Chebyshev по XY и расстоянию между позициями Z в connected-level list; sector=min connected Z | `core/topology.dm:2–18` |
| Привязка | APC своей base area приоритетнее более близкого чужого; равенства — по адресу | `core/topology.dm:192–258` |
| Стабильность привязки | Валидный APC сохраняется при движении в base area и появлении более близкого APC | `core/topology.dm:261–303` |
| Соседи | NETRUN_APC_NEIGHBORS=3; комментарий про two nearest устарел | `code/__DEFINES/netrun.dm:119`, `core/apc_spatial_index.dm:68` |
| Связность | Дедуп ближайших рёбер + дополнительные минимальные мосты между компонентами | `core/topology.dm:439–490`, `core/apc_bridges.dm:34–78` |
| Стоимость | Физическое ребро max(1,ceil(distance/15)) ds; relay — 3 ds | `core/topology.dm:364–371`, `code/__DEFINES/netrun.dm:103–107` |
| Равные пути | Dijkstra не заменяет уже найденный равный путь; heap tie-break — адрес | `core/routes.dm:170–183` |
| Машинные маршруты | Оба endpoints online; промежуточные forwarders проверяются | `core/machine_routes.dm:105–155,195–217` |
| Задержка | clamp(latency*0.02,0,0.25 seconds); меньше tick_lag → асинхронная очередь без timer tick | `core/machine_transport.dm:169–177` |
| Изменение сети в полёте | На delivery снова вызывается route(); разрешён новый альтернативный путь | `core/machine_transport.dm:203`, `core/machine_routes.dm:158–161` |
| Coalescing | Только opt-in replaceable state; identity сервисов, binding/tag и contract key входят в ключ | `core/machine_transport.dm:53–84` |
| Payload | Форма вложенных positional/associative списков сохраняется | `core/machine_transport.dm:5–15` |

Моделирование по каждому исходному hop и fail при любой смене трассы не были бы
автоматически совместимы с текущим агрегированным транспортом. Эти правила нужно
воспроизвести прежде, чем заявлять полноценный C# transport.

## Сервисы и синхронные потребители

Семьи: atmos, research server/client, telecomms origin/relay/receiver,
camera stream/viewer, PDA messenger, communications gateway/console, security events/monitor.
Базовая проверка existence/online — `core/service_registry.dm:103–104`;
перед эффектом `matches`, `accepts`, `receive` — `core/machine_transport.dm:274–281`.

Доменные права проверяют сами адаптеры: camera networks и can_use
(`integration/machine_cameras.dm:12–21`), доступность программы PDA и её identity
(`integration/machine_pda.dm:35–48`), research authority/ownership/binding generation
(`integration/machine_research.dm:431,695`), atmos command/tag и native receive_signal
с восстановлением user_ref (`integration/machine_atmos.dm:385–396`).
Одного объявления service_id недостаточно для допуска операции. Полная
классификация read-only/idempotent/mutating и контракты отмены ещё не выполнены.

Синхронные точки: `core/machine_routes.dm:123`, `core/service_registry.dm:142–169`,
`core/routes.dm:505,541,583,618`, `core/map_view.dm:416`, `core/console_ui.dm:1076`,
`core/console.dm:251,282,578`, `core/cli.dm:171`, `machines/windows.dm:276,278`.
Другие группы: bots/brain/navigation/ICE, telecomms и research integration.
До live-cutover нужен полный inventory и cached/pending/callback API.

## Среда, runner и профили

В upstream `dependencies.sh`: BYOND 516.1688, rust_g 6.2.0. Это не подтверждает
готовность ABI или наличие пригодного неблокирующего IPC-моста.

```powershell
node tools/netrun/run-tests.mjs --list
node tools/netrun/run-tests.mjs --filter topology
node tools/netrun/run-tests.mjs --filter routes
node tools/netrun/run-tests.mjs --filter transport
node tools/netrun/run-tests.mjs --peace-map
```

Команды запускаются в upstream; runner использует свежий unit_tests.json и общий
`tmp/netrun-tests.lock`. Не запускать несколько DM-миров в одном checkout.
В первом C# пакете DM-код не менялся; эти тесты и parity здесь не запускались.

Найден исторический capture `tmp/netrun-peace-profile-20261031-e/`:
`run-metadata.json`, `source.diff`, `report/summary.md`, `report/interval.json`,
`native-interval.csv`, `validation/full-suite-results.json`.
База capture — `f8dec791...` с отдельным diff hash `e8b8cba9...`, не текущий HEAD.
Отчёт заявляет 641/641 tests, 300 s warm-up + 60 s sample, execute 49.08 ms/s,
5535 cache hits и ноль новых pair/tree solves. Это исторические утверждения,
не проверки текущей задачи. Фон без игроков и изменяющийся состав трафика
не дают контролируемого p95/p99 DM/IPC baseline. Сырые capture и payload сюда
не копируются.

Для release gate нужны одинаковый world/event trace, размеры сети и churn,
сценарии polling/outage/host slow-down, DM total/p95/p99 по фазам обмена,
суммарные CPU/allocations/RSS и end-to-end задержки. Численные пороги и production
OS/RID ещё не согласованы. Придумывать их без свежих измерений нельзя.
