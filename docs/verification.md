# Проверка Core и интеграции станции

Дата: 2026-11-02. Локально Windows x64, .NET SDK 10.0.403,
BYOND 516.1688 x86, Release/net10.0. Ниже приведены локальные результаты;
удалённый статус конкретной ревизии доступен в [GitHub Actions](https://github.com/Dominig12/netrun-core/actions).

| Проверка | Результат |
|---|---|
| `dotnet test Netrun.sln -c Release --no-restore` | 512 passed, 0 failed, 0 skipped; production проекты собраны |
| `dotnet format whitespace Netrun.sln --no-restore --verify-no-changes` | Успешно |
| Windows native ABI/socket/BYOND suite | 13 passed, включая x86 BYOND и active-worker unload/shutdown regressions |
| Node build/run/runner suites | 22 passed |
| Station hermetic DM regression | 91 passed: external, recovery и transport; compile 0 errors / 0 warnings, runtime errors не найдены |
| Managed Core build | Host publish + Windows x86 bridge из одного immutable context; source/artifact hashes проверены |
| Published Host lifecycle probe | Host готов до TCP child; listener и принадлежащие launcher процессы закрылись после выхода child |
| Tiny-world BYOND → bridge → published Host E2E | PASS: snapshot/catchup/churn, native receive один раз, terminal callback один раз, unknown invocation без ACK |
| Shell syntax container scripts | Успешно (`bash -n`) |
| Полный station-code live smoke | PASS, `core-live-3a41eaa8e954`: discovery/delivery/graph/tree/movement, DM builders не вызывались, runtime errors 0, DreamDaemon exit 0 |
| Docker build/Compose/Linux BYOND runtime | Не выполнены: Docker Engine и рабочий WSL отсутствуют; тестового Linux-хоста нет |

C# suite покрывает atomic mirror/snapshot/catch-up, checksum golden vectors,
malformed/fragmented/truncated frames, строгую Unicode/JSON схему, поколения и
replay fences; topology, binding, weighted routes, source trees, availability,
communications, transport ordering/coalescing/deadlines/cancellation, invocation
lifecycle и output replay. Discovery использует source reachability без построения
полного пути для каждого кандидата. GraphSync тестирует paged snapshot/delta,
application ACK, отмену и устойчивость к endpoint churn.

91 station tests проверяют causal command prefixes, bounded coalescing и frames,
sector resync, recovery/backoff, native identity/permissions/final gate после yield,
invocation dedup/retirement, query dispatch, graph/source-tree façade, точечную
инвалидацию UI, а также прежние transport contracts. Диагностический `dm` режим
используется только изолированными regression fixtures; live smoke выбирает
штатный `external` и настоящий subsystem.

Повторить из checkout станции:

```sh
node --test tools/netrun/core-lifecycle.test.mjs tools/netrun/docker-lifecycle.test.mjs tools/netrun/run-live-core-test.test.mjs tools/netrun/run-tests.test.mjs
node tools/netrun/core-lifecycle.mjs build
node tools/netrun/probe-host-lifecycle.mjs
node tools/netrun/run-live-core-test.mjs --no-build
```

Обычный DM runner выбирает tests по `--filter`; дополнительный live runner
компилирует полный station code с `NETRUN_CORE_LIVE_TEST`, запускает реальную пару
Host/DreamDaemon на hermetic map и ограничивает время/логи. Он проверяет discovery,
доставку, graph/source-tree import, movement recovery и неизменность счётчиков
DM topology/APC/tree builders. Это настоящий IPC/runtime тест, но не полный
production раунд и не измерение нагрузки с игроками.

Synthetic C# benchmark для 1k/5k/10k узлов сохранён в [baseline.md](baseline.md)
и [raw JSON](baseline-results.json). Его timings не доказывают end-to-end speedup
в игре. Linux ABI/runtime, Docker build/up и production нагрузка остаются
отдельными необходимыми проверками на соответствующем окружении. Текущий пакет
GitHub workflows ещё не считается прошедшим до его публикации и реального run.
