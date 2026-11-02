# Численный C# baseline

Измерено локально 2 ноября 2026, 02:45 MSK (1 ноября 23:45 UTC).
Это standalone **C#-измерения**, а не полный DM → bridge → Host → native effect
цикл и не сравнение скорости с DM. Порогов «прошёл/не прошёл» по времени нет.

Исходники: [benchmark project](../benchmarks/Netrun.Benchmarks/Netrun.Benchmarks.csproj),
[Program.cs](../benchmarks/Netrun.Benchmarks/Program.cs).
Полные три samples каждой метрики и environment metadata сохранены в
[baseline-results.json](baseline-results.json). Проверки корректности fixture
завершают процесс ошибкой при missing route/tree/page/invocation/completion или
повторном source solve внутри одного discovery query.

## Окружение и воспроизведение

- Windows 10 Pro 10.0.19045, процесс x64.
- Intel Core i5-11500 @ 2.70 GHz, 6 cores / 12 logical processors.
- Physical memory по Windows: 17 022 644 224 bytes, примерно 15,85 GiB.
- .NET SDK 10.0.403; фактический runtime из отчёта: .NET 10.0.14.
- Workstation GC (`ServerGC=false`), latency mode `Interactive`.
- Release build; `Stopwatch.Frequency=10 000 000`.
- 2 warmup + 3 measured runs на каждую метрику; порядок размеров 1000 → 5000 → 10000
  в одном процессе. JIT-параметры проектом не переопределяются.
- Перед каждым measured sample вызывается полный GC, исключённый из таймера.
  Нет process affinity, изоляции фоновой нагрузки или изменения priority/power policy.

Из корня checkout, без новых NuGet packages:

```powershell
dotnet build benchmarks/Netrun.Benchmarks/Netrun.Benchmarks.csproj -c Release --nologo
dotnet run --project benchmarks/Netrun.Benchmarks/Netrun.Benchmarks.csproj -c Release --no-build -- --output docs/baseline-results.json
```

Фактически использован portable executable
`%USERPROFILE%/.codex/cache/netrun-dotnet/sdk/dotnet.exe`
с теми же аргументами. Build прошёл с 0 warnings / 0 errors; весь benchmark
завершился успешно. Инструмент находится вне `src`, поэтому не входит в source
hash production Host.

## Детерминированная synthetic station

Seed `0x4e455452` (`1313166418`), алгоритм xorshift32 явно реализован в исходнике.
Один сектор с native ordered Z stack `[5,1,9]`. APC размещены сеткой с шагом 96;
каждый имеет свою native supplied/base area. Endpoint назначается APC по modulo
и получает offset X/Y в диапазоне −12..12, Z того же APC. Все узлы online;
physical mesh связан, relay forwarding включён. Все 1000 pair queries обязаны
найти путь. Эта геометрия с native area на APC не покрывает произвольную станцию.

| Nodes | APC | Real relay flags | Physical edges | Snapshot operations | Snapshot pages ≤32 ops |
|---:|---:|---:|---:|---:|---:|
| 1 000 | 10 | 2 | 1 009 | 2 009 | 63 |
| 5 000 | 50 | 10 | 5 083 | 10 083 | 316 |
| 10 000 | 64 | 20 | 10 111 | 20 111 | 629 |

Implicit quantum clique не материализуется. Single-leaf delta во всех размерах
и всех запусках содержит ровно **2 operations / 1 page**: node revision и один
physical edge. Baseline сохраняется между delta samples, каждый batch полностью
подтверждается explicit application ACK.

## Результаты

Медианы wall time, миллисекунды. Числа для 1000 cached queries и 128 lifecycle
означают **всю группу**, не один запрос. Discovery означает один query на 1000
кандидатов, включая один cold source solve на графе указанного размера.

| Метрика | 1 000 nodes | 5 000 nodes | 10 000 nodes |
|---|---:|---:|---:|
| Fresh topology builder + Build | 4,4667 | 19,2674 | 33,0647 |
| 1000 cached pair routes | 0,3306 | 0,0923 | 0,1017 |
| Uncached complete source tree | 1,1689 | 6,2584 | 6,3393 |
| Ordinary discovery, 1000 candidates | 2,1665 | 7,8378 | 8,0166 |
| Graph sync snapshot + all ACKs | 2,1903 | 11,7013 | 19,8948 |
| Single-leaf delta + ACK | 1,1110 | 7,2930 | 8,2535 |
| 128 Runtime + codec lifecycles | 13,1716 | 12,8178 | 7,6687 |

Медианы новых managed allocations, **bytes**:

| Метрика | 1 000 nodes | 5 000 nodes | 10 000 nodes |
|---|---:|---:|---:|
| Fresh topology builder + Build | 1 636 320 | 7 909 888 | 13 861 408 |
| 1000 cached pair routes | 240 | 240 | 240 |
| Uncached complete source tree | 393 464 | 1 798 904 | 3 757 400 |
| Ordinary discovery, 1000 candidates | 620 024 | 2 025 464 | 3 916 760 |
| Graph sync snapshot + all ACKs | 1 567 840 | 7 583 000 | 15 345 360 |
| Single-leaf delta + ACK | 1 208 | 1 168 | 1 136 |
| 128 Runtime + codec lifecycles | 4 185 128 | 3 992 616 | 3 976 232 |

Allocation counter — `GC.GetAllocatedBytesForCurrentThread`. Всё измеряемое
выполняется синхронно; это не retained memory, peak RSS, native allocation или
полная стоимость GC. Медиана времени и медиана allocations вычисляются отдельно.

Разброс заметен: например, source tree на 1000 nodes имеет sample 9,2464 мс при
медиане 1,1689 мс, а cached batch на 5000 — sample 6,7070 мс при медиане 0,0923 мс.
Немонотонные cached/lifecycle времена между размерами нельзя интерпретировать как
ускорение от увеличения станции: один процесс, JIT-прогрев и фоновая нагрузка
не изолированы. Три samples дают исходную точку для повторения, не статистическую
гарантию latency.

## Точный scope каждой метрики

- **Topology:** новый `NetworkTopology` и complete Build. Создание исходных facts
  исключено; initial native-area binding входит в измерение.
- **Cached pairs:** LRU capacity 1024; все 1000 exact pairs прогреты заранее,
  внутри measured operation проверяется отсутствие misses. Время заполнения
  кеша исключено.
- **Source tree:** один полный поиск, `CacheCapacity=0`; все N nodes должны
  присутствовать в результате. Топология уже построена.
- **Ordinary discovery:** отдельные 1000 authority services распределены по
  существующим узлам формулой `(i * 7919 + 37) % N`; все network-eligible.
  Каталог и граф подготовлены заранее. Перед каждым sample вне таймера создаются
  новые owner и пустой source-tree cache. В measured operation входят Submit,
  bounded candidate capture, один cold source solve, 1000 checks и dequeue всех
  16 страниц. Каждый запуск проверяет **1 CacheMiss + 999 CacheHits**, а не
  выводит число searches из времени. `IsReachable` читает tree entry без создания
  пути для каждого сервиса; native accepts/permissions здесь не моделируются.
- **Graph snapshot:** новый tracker/broker, capture/diff, pages ≤32, dequeue и
  немедленные application ACKs. Создание topology и JSON/network/native apply
  исключены.
- **Leaf delta:** persistent ACKed baseline; перемещение и topology rebuild
  выполняются **до** таймера. Измеряется bounded target comparison, two-op page
  и ACK. Малый wire diff не означает O(1) CPU: сравнение baseline/target остаётся
  линейным по graph rows, что видно по росту времени.
- **Runtime + codec:** initial full world snapshot исключён. В измерение входят
  128 serial Submit → Pump → ServiceInvocation → InvocationResult → Completion,
  JSON encode/decode обоих logical inputs и проверки terminal outcomes. Payload
  общий bounded immutable; services у одного APC, scaled machine delay нулевой.
  Host output JSON, TCP, framing, native bridge, BYOND scheduling, permissions и
  реальные machine handlers здесь отсутствуют.

Результаты не заменяют профилирование полного игрового цикла на фактической карте.
