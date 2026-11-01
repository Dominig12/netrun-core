# Проверка первого пакета

Дата: 2026-11-01. Локально: Windows 10 x64, SDK 10.0.403, Release/net10.0.

| Проверка | Результат |
|---|---|
| `dotnet restore Netrun.sln --locked-mode` | Успешно, тестовые зависимости соответствуют lockfile |
| `dotnet build Netrun.sln -c Release --no-restore` | Успешно, 0 warnings / 0 errors |
| `dotnet test Netrun.sln -c Release --no-build` | 75 passed, 0 failed, 0 skipped |
| `dotnet format whitespace Netrun.sln --no-restore --verify-no-changes` | Успешно |
| `dotnet run --project src/Netrun.Host -c Release --no-build -- replay fixtures/mirror-lifecycle.jsonl` | Exit 0; applied, applied, duplicate, applied; ACK 1,2,2,3 |

Проверены malformed/fragmented/truncated frames, размер до allocation, строгая
схема и Unicode, строковые ulong, whole-batch validation, ordered staging,
версии/поколения/tombstones, epoch/time/sequence fences, bounded replay/capacity,
публичное расширение новым типом записи и защита от reentrancy.

Проект не подключён к DM. Паритет графа/маршрутов/транспорта, production IPC,
производительность и Linux runtime пока не проверены. CI для Windows/Linux
подготовлен, но удалённый прогон ещё не выполнялся. Исходный checkout станции
не изменялся; его DM-тесты в этой задаче не запускались.
