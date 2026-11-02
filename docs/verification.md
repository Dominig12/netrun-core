# Проверка Core и интеграции станции

Дата: 2026-11-02. Локально Windows x64, .NET SDK 10.0.403,
BYOND 516.1688 x86, Release/net10.0. Ниже приведены локальные результаты;
удалённый статус конкретной ревизии доступен в [GitHub Actions](https://github.com/Dominig12/netrun-core/actions).

| Проверка | Результат |
|---|---|
| `dotnet test tests/Netrun.Tests/Netrun.Tests.csproj -c Release --no-restore` | 512 passed, 0 failed, 0 skipped; production проекты собраны |
| Windows native ABI/socket/BYOND suite | 13 passed, включая x86 BYOND и active-worker unload/shutdown regressions |
| Node build/source/download/Docker/runner suites | 41 passed |
| NetrunTerminal frontend | 327 passed в 45 suites |
| Station hermetic DM regression | Все 682 selected Netrun tests passed; compile 0 errors / 0 warnings, runtime errors 0 |
| DreamChecker suite 1.11 / DME include order | 0 diagnostics / успешно |
| Integration bundle | 29 новых файлов, 81 hooks; состав, SHA-256, LF и применение patch к baseline через отдельный Git index проверены |
| Managed Core build | Host publish + Windows x86 bridge из одного immutable context; source/artifact hashes проверены |
| Published Host lifecycle probe | Host готов до TCP child; listener и принадлежащие launcher процессы закрылись после выхода child |
| Tiny-world BYOND → bridge → published Host E2E | PASS: snapshot/catchup/churn, native receive один раз, terminal callback один раз, unknown invocation без ACK |
| Shell syntax container scripts | Успешно (`bash -n`) |
| Station-code live smoke | `core-live-d1fa9344f5de`: discovery/native delivery, graph/tree, movement, state и карта импланта прошли; проверка переноса сознания падает при регистрации нового endpoint резидента |
| Docker build/Compose/Linux BYOND runtime | Полная сборка прошла; Linux BYOND x86 → bridge → Host публикует graph и native projection. Пользователь подтвердил загрузку TGUI. Пустая карта сущности и recovery под нагрузкой ещё исследуются |

C# suite покрывает atomic mirror/snapshot/catch-up, checksum golden vectors,
malformed/fragmented/truncated frames, строгую Unicode/JSON схему, поколения и
replay fences; topology, binding, weighted routes, source trees, availability,
communications, transport ordering/coalescing/deadlines/cancellation, invocation
lifecycle и output replay. Discovery использует source reachability без построения
полного пути для каждого кандидата. GraphSync тестирует paged snapshot/delta,
application ACK, отмену и устойчивость к endpoint churn.

682 station tests проверяют causal command prefixes, bounded coalescing и frames,
sector resync, recovery/backoff, native identity/permissions/final gate после yield,
invocation dedup/retirement, query dispatch, graph/source-tree façade, точечную
инвалидацию UI, прежние gameplay/transport contracts, регистрацию атмосферы,
research authority, fortress, импланты/MOD и движение между LAN. Fixtures
импортируют заданные path rows и IPC results через production decoder; отдельного
DM engine нет. Удаление вложенных scenes вне порядка проверяет восстановление
графа, client, areas и z-level traits. Регрессии дробных cumulative costs сохраняют
валидный C# путь при округлении DM float и отзывают его при смене native revision;
GC probes проверяют освобождение завершённых discovery/wake queries.
Live smoke выбирает штатный `external`
и настоящий subsystem.

Повторить из checkout станции:

```sh
node --test tools/netrun/*.test.mjs
node tools/netrun/run-tests.mjs
node tools/netrun/core-lifecycle.mjs build
node tools/netrun/probe-host-lifecycle.mjs
node tools/netrun/run-live-core-test.mjs --no-build
```

Обычный DM runner выбирает tests по `--filter`; дополнительный live runner
компилирует полный station code с `NETRUN_CORE_LIVE_TEST`, запускает реальную пару
Host/DreamDaemon на hermetic map и ограничивает время/логи. Он проверяет discovery,
доставку, graph/source-tree import, movement recovery и idempotent `state`.
DM topology/APC/tree builders удалены. Это настоящий IPC/runtime тест, но не полный
production раунд и не измерение нагрузки с игроками.

Это checkpoint для проверки на другой машине, а не завершённая интеграция.
В Docker зафиксированы startup `source_journal_overflow` и последующий
`native_result_pressure` с восстановлением binding. После регистрации нового
endpoint DM временно блокирует route authority; немедленный check возвратной
связи резидента приводит к отказу погружения. Также найден отсутствующий runtime
`lobby_html.txt` со стилями лобби; он добавлен в deploy, новый образ ещё требуется.

Synthetic C# benchmark для 1k/5k/10k узлов сохранён в [baseline.md](baseline.md)
и [raw JSON](baseline-results.json). Его timings не доказывают end-to-end speedup
в игре. Переход к длительной работе полного Compose без resync и production
нагрузка остаются отдельными проверками.

Опубликованный код `d81fa13a11e5e4cb95ccb7999465b2c7698bf067` прошёл
[CI на Windows и Ubuntu](https://github.com/Dominig12/netrun-core/actions/runs/41394652983):
restore, build, tests, format и оба replay runners. В
[Native and container](https://github.com/Dominig12/netrun-core/actions/runs/41394652977)
успешны пять настоящих Linux x64 ABI/socket тестов, включая active-worker
`dlclose`, и сборка standalone Docker Host image. Это подтверждённые удалённые
проверки, дополнительные к локальным результатам выше; полный Compose станции
они не запускают.

Интеграция станции опубликована коммитом
[`bc5915e8718b2af4336886e23099e84a1e1351ae`](https://github.com/Dominig12/MOLOT-BlueMoon-Station/commit/bc5915e8718b2af4336886e23099e84a1e1351ae).
Её общий [CI Suite](https://github.com/Dominig12/MOLOT-BlueMoon-Station/actions/runs/41394855038)
запущен вручную для `net-feature`; его окончательный статус проверяется отдельно.
