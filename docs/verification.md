# Проверка автономного ядра и синхронизации

Дата: 2026-11-01. Локально: Windows 10 x64, SDK 10.0.403, Release/net10.0.

| Проверка | Результат |
|---|---|
| `dotnet restore Netrun.sln --locked-mode` | Успешно, тестовые зависимости соответствуют lockfile |
| `dotnet build Netrun.sln -c Release --no-restore` | Успешно, 0 warnings / 0 errors |
| `dotnet test Netrun.sln -c Release --no-build` | 160 passed, 0 failed, 0 skipped |
| `dotnet format whitespace Netrun.sln --no-restore --verify-no-changes` | Успешно |
| `dotnet run --project src/Netrun.Host -c Release --no-build -- replay fixtures/mirror-lifecycle.jsonl` | Exit 0; applied, applied, duplicate, applied; ACK 1,2,2,3 |
| `dotnet run --project src/Netrun.Host -c Release --no-build -- replay-sync fixtures/snapshot-churn.jsonl` | Exit 0; snapshot/chunk/duplicate/commit/catch-up/ready/delta; final ACK 6, 1 live + 1 tombstone |

Проверены malformed/fragmented/truncated frames, размер до allocation, строгая
схема и Unicode, строковые ulong, whole-batch validation, ordered staging,
версии/поколения/tombstones, epoch/time/sequence fences, bounded replay/capacity,
публичное расширение новым типом записи и защита от reentrancy.
Добавлены portable checksum golden vectors, snapshot/catch-up barriers,
непубликация staging при invalid/hash/count/chunk/journal ошибках,
delete/recreate во время обхода, deterministic churn на 100 узлах,
и отказ Host на EOF до Ready. Первый опубликованный пакет содержал 75 тестов.

Проект не подключён к DM. Паритет графа/маршрутов/транспорта, production IPC,
производительность и production runtime пока не проверены. Первый пакет прошёл
[GitHub CI на Windows/Linux](https://github.com/Dominig12/netrun-core/actions/runs/41337821910).
Для snapshot-пакета локальные результаты приведены выше; удалённый статус
проверяется после публикации в Actions. Исходный checkout станции
не изменялся; его DM-тесты в этой задаче не запускались.
