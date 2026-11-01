# NETRUN Core

Самостоятельное C#-ядро сетевой симуляции NETRUN для BYOND. Цель проекта — вынести
построение топологии, маршрутизацию и транспорт машин в отдельный процесс,
оставив авторизацию и физические эффекты в DM.

**Сейчас реализован первый автономный пакет:** строгие контракты ограниченного
подмножества фактов, бинарный framing, версионированный mirror узлов и offline
replay. Подключения к игре, графа, маршрутизации, доставки сервисов и native-моста
пока нет. Проект не готов для production или переключения live-владельца.

## Сборка и проверка

Нужен .NET SDK **10.0.403**, зафиксированный в `global.json`.
Ядро, протокол и Host используют только стандартную библиотеку; NuGet-зависимости
есть только у тестов. BYOND и checkout станции для этой сборки не требуются.

```powershell
dotnet restore Netrun.sln --locked-mode
dotnet build Netrun.sln -c Release --no-restore
dotnet test Netrun.sln -c Release --no-build
dotnet format whitespace Netrun.sln --no-restore --verify-no-changes
dotnet run --project src/Netrun.Host -c Release --no-build -- replay fixtures/mirror-lifecycle.jsonl
```

В текущем локальном окружении SDK установлен без изменения PATH:

```powershell
$netrunDotnet = "$env:USERPROFILE/.codex/cache/netrun-dotnet/sdk/dotnet.exe"
& $netrunDotnet test Netrun.sln -c Release
```

Fixture синтетическая: регистрация → удаление → повтор удаления → новое поколение.
Успешный replay возвращает exit code `0`; недопустимые данные — `2`, прерывание CLI —
`130`. Выход — JSONL с cumulative `applied_sequence` и количеством live/retired ID.
Входные пользовательские данные в диагностику не печатаются.

Режим `framed WORLD_EPOCH ENGINE_EPOCH BINDING_EPOCH` проверяет тот же pipeline
на stdin/stdout с 4-байтовыми big-endian длинами. Используйте бинарные потоки,
а не текстовый PowerShell pipeline. Это тестовый стенд с заданными epochs,
без handshake, сетевого endpoint и допуска к игровым операциям.

## Модули и расширение

| Проект | Ответственность |
|---|---|
| `Netrun.Core` | Типизированные факты, поколения, tombstones, атомарная публикация и ограниченный dedup |
| `Netrun.Protocol` | Строгий JSON codec и framing; зависит от доменных контрактов Core |
| `Netrun.Host` | Composition и автономные runner; зависит от Core и Protocol |
| `Netrun.Tests` | Контрактные, модульные и интеграционные проверки |

Core не зависит от сериализации, IPC, DM ABI или Host. Один последовательный
владелец вызывает `Apply`; `Nodes` — read-only **live view** для этого же потока,
а не thread-safe immutable snapshot.

Публичная граница чтения/применения — `IWorldMirror`. Новый тип записи **модуля
узлов** можно добавить наследованием от `WorldChange` и
`NodeChangeHandler<TChange>`, затем явно передать в `additionalHandlers`.
Обработчик без побочных эффектов превращает запись в `NodeMutation` с полным
фактом или tombstone. Центральные проверки поколений, версий, лимитов и публикации
сохраняются. Пример рабочего расширения — `ExtensibilityTests.cs`.

Регистрация фиксируется при создании сессии; lookup по точному типу выполняется
за O(1). Reflection, динамическая загрузка сборок и DI-container не нужны.
Новые доменные подсистемы будут отдельными модулями с собственными контрактами,
а не обработчиками, напрямую изменяющими внутренности mirror.

Сетевая схема намеренно остаётся allowlist. Регистрация C#-обработчика не
разрешает произвольный wire-kind: изменение схемы требует отдельной реализации
codec и контрактных тестов.

## Документация

- [Архитектура и границы первого пакета](docs/architecture.md).
- [Протокол разработки и лимиты](docs/protocol.md).
- [Проверенные правила DM и baseline](docs/audit.md).
- [Результаты локальной проверки](docs/verification.md).
- [Исходный проектный документ](docs/CSHARP_CORE_DESIGN.md) — архивная исходная
  концепция, не отчёт о реализованных возможностях.

Следующий пакет — полноценный snapshot/chunks/commit с проверкой целостности,
catch-up и сервисными/секторными фактами. После него — shadow-топология и
маршруты с проверкой паритета. Выбор production ОС/RID, ABI моста, авторизованного
IPC и численных release gates остаётся открытым; текущая сборка не доказывает
паритет или разгрузку DM.
