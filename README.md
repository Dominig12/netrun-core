# NETRUN Core

Самостоятельная C# библиотека NETRUN и отдельный Host для BYOND.
Целевая граница — полное владение системой в Core: сущности, права, топология,
файлы, хранилища, объекты и FIFO задачи, с расширениями/модами. DM передаёт факты
и намерения, отображает проекции и выполняет native действия. Точные границы
и оставшаяся интеграция станции описаны в [ownership](docs/ownership.md).

Реализованы mirror узлов/секторов/сервисов, snapshot с catch-up, пространственная
топология, routing и source trees, communications overlay, bounded transport,
invocation lifecycle, discovery, постраничные проекции, авторизованный loopback
Host с output replay/ACK и неблокирующий native мост. Точный статус игровой
интеграции приведён в [implementation-status](docs/implementation-status.md).
Станция использует внешний Core по умолчанию; обычные Windows build/run команды
и Linux Docker launcher описаны в [инструкции интеграции](integration/dm/README.md).

## Сборка и проверка

Нужен .NET SDK **10.0.403** из `global.json`. В Windows portable bootstrap
сам получает SDK и Zig с проверкой archive hashes; глобальная установка и
пользовательские пути Codex не требуются. Production проекты используют
стандартную библиотеку; NuGet-зависимости есть только у тестов.

```powershell
$tools = ./scripts/ensure-toolchains.ps1 -Tool all
& $tools.dotnet restore Netrun.sln --locked-mode
& $tools.dotnet test Netrun.sln -c Release --no-restore
./scripts/build-native.ps1 -Target all
```

При уже установленном совместимом SDK доступны обычные команды:

```powershell
dotnet restore Netrun.sln --locked-mode
dotnet build Netrun.sln -c Release --no-restore
dotnet test Netrun.sln -c Release --no-build
dotnet format whitespace Netrun.sln --no-restore --verify-no-changes
dotnet run --project src/Netrun.Host -c Release --no-build -- replay fixtures/mirror-lifecycle.jsonl
dotnet run --project src/Netrun.Host -c Release --no-build -- replay-sync fixtures/snapshot-churn.jsonl
```

Для запуска Host задайте `NETRUN_SESSION_TOKEN_FILE` с путём к локальному секрету,
затем выполните `dotnet run --project src/Netrun.Host -c Release -- serve 18462`.
Токен не передаётся аргументом CLI и не добавляется в репозиторий. Host слушает
только IPv4 loopback. DreamDaemon загружает отдельную x86 DLL/SO; Host работает
обычным процессом Windows или Linux.

## Архитектура и расширение

| Модуль | Ответственность |
|---|---|
| `Netrun.Core` | Предметная модель без зависимости от JSON, IPC или BYOND ABI |
| `Topology` | Пространственные индексы, binding policies, immutable graph publications |
| `Routing`, `Communication` | Взвешенные маршруты, source trees, availability и radio edges |
| `Transport` | Очереди, задержки, отмена, безопасное coalescing и invocation lifecycle |
| `Discovery`, `Projection` | Асинхронные bounded выборки сервисов, графа и маршрутов |
| `Runtime` | Последовательный владелец симуляции, логическое время и общий бюджет |
| `Access`, `Entities` | Raw credential authority, registry процессов, navigation, combat и bot policy |
| `Storage` | Принадлежащие Core файлы/носители, capabilities и атомарные операции |
| `Scheduling`, `Plugins` | Общая FIFO очередь, явная загрузка DLL и изолированные mod sessions |
| `Netrun.Protocol` | Строгая allowlist схема, framing и immutable payload codec |
| `Netrun.Host` | Composition, локальная сессия, replay/ACK и автономные runners |
| `native` | Неблокирующая транспортная граница BYOND |

Горячие пути используют индексы и bounded caches. Relay backbone представлен
неявным bucket без квадратичной материализации clique. Изменение питания
обновляет availability отдельно от геометрии. Новые обработчики узлов реализуют
`NodeChangeHandler<TChange>`, стоимости маршрутов — `IRouteCostPolicy`.
Binding поведение задаётся явной policy. Подключаемые расширения оформляются как
плагины `INetrunPlugin` и задаются через `RuntimeOptions.Plugins` — см.
[плагины](docs/plugins.md). XML-комментарии публичных методов и
полей проверяются сборкой production проектов.

Mutable runtime имеет одного последовательного владельца. Публикации графа и
значения неизменяемы; live view mirror не является thread-safe snapshot.
Подключение C# обработчика само по себе не расширяет wire allowlist и не
разрешает новые игровые эффекты.

## Документация

- [Архитектура](docs/architecture.md) и [состояние реализации](docs/implementation-status.md).
- [Плагины](docs/plugins.md) — расширение библиотеки политиками маршрутизации.
- [Runtime протокол](docs/protocol-runtime.md), [базовая схема](docs/protocol.md)
  и [snapshot/catch-up](docs/synchronization.md).
- [DM адаптер](docs/external-dm.md), [native мост](docs/native.md)
  и [развёртывание](docs/deployment.md).
- [Правила DM](docs/audit.md) и [проверки](docs/verification.md).
- [Исходный проектный документ](docs/CSHARP_CORE_DESIGN.md) — архивная концепция.

Linux BYOND x86 и полный Compose проверены реальным запуском; оставшиеся ошибки
карты/погружения и recovery перечислены в [verification.md](docs/verification.md).
Полный игровой цикл и end-to-end нагрузка ещё требуют проверки.
