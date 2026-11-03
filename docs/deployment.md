# Подготовка сборки и развёртывания

Штатные Windows build/run команды станции автоматически собирают и подключают
Core. Для Linux подготовлены два согласованных Docker target и Compose launcher.
Точное описание находится в [DM integration bundle](../integration/dm/README.md)
и [инструкции станции](../integration/dm/files/tools/netrun/EXTERNAL_CORE.md).
Существующий production сервер эти изменения автоматически не развёртывают.

## Штатный launcher станции

Отсутствующий соседний `netrun-core` launcher получает из публичного репозитория
автоматически по закреплённому commit SHA в `tools/netrun/core-source.json`.
Существующий checkout и его локальные изменения не обновляются автоматически;
другой путь задаётся `NETRUN_CORE_SOURCE`. В Windows обычные `Build.bat` и `RUN_SERVER.bat` используют
Juke targets, которые публикуют Host и x86 DLL из одного immutable source context.
Перед запуском проверяются source digest и artifact hashes. Launcher создаёт
ignored token file, ждёт listener Host и запускает DreamDaemon; завершение одного
процесса закрывает принадлежащую launcher пару. По умолчанию действует `external`.

На Windows portable SDK/Zig автоматически подготавливаются в
`%LOCALAPPDATA%/NETRUN/toolchains`, либо в `NETRUN_TOOLCHAIN_CACHE`.
Явные `NETRUN_DOTNET`/`NETRUN_ZIG` и совместимые инструменты из PATH имеют
приоритет. Официальные archives проверяются закреплёнными hash до распаковки;
глобальная установка и Codex-specific cache не используются.

В Linux либо Docker Desktop с Linux Engine из корня станции:

```sh
node tools/netrun/docker-lifecycle.mjs deploy
node tools/netrun/docker-lifecycle.mjs status
node tools/netrun/docker-lifecycle.mjs logs
```

Buildx получает минимальный named context с точными bytes исходников и SHA-256
manifest. Образы `netrun-station` и `netrun-host` помечаются одним digest; launcher
проверяет его перед запуском. Станция и Host делят loopback namespace и file secret;
наружу публикуется только игровой порт. Станция ждёт Host, а attached Compose
останавливает пару при выходе любого контейнера. `deploy` запускает в фоне;
`restart` пересоздаёт оба контейнера из сохранённых immutable image IDs.
`stop` останавливает контейнеры, `down` удаляет их; data volume сохраняется.
Config монтируется read-only в `/run/netrun/config-source`, затем entrypoint
копирует его в `/tgstation/config`. Token для BYOND копируется в приватный файл
`/run/netrun/session-token` с mode 0600; Host читает исходный Compose secret.
Это требуется в Docker Desktop: файлы Windows bind mounts имеют inode больше
32 бит, и BYOND x86 не может их читать, хотя 64-битный shell/Host может.
Named data volume использует native Docker filesystem и не копируется.
Для unattended smoke включите `RESUME_AFTER_INITIALIZATIONS` в test config:
обычный пустой мир засыпает после initialization до первого клиента.
По умолчанию опубликован только `127.0.0.1:1337`; для внешнего сервера задайте
`NETRUN_GAME_ADDRESS=0.0.0.0` перед deploy. Полная инструкция станции описывает
config, token, обновление, перенос данных и диагностические команды.

BYOND устанавливается из проверяемого SHA-256 архива закреплённой версии.
При обрывах крупных HTTPS responses загрузчик использует bounded HTTP ranges
с проверкой каждого диапазона и полной итоговой контрольной суммы.
Build и station runtime используют pinned Node 24 Bookworm: Node нужен также
работающему миру для штатного loadout crypto helper. Совместимые x86 OpenSSL
libraries поставляются вместе с собранным rust-g. .NET 10 работает в отдельном
поддерживаемом runtime image, bridge строится для x86/glibc 2.17.
Cargo для закреплённого NanoMap fallback и build caches подготавливаются автоматически.
Фактические результаты Windows/Docker проверок перечислены в [verification.md](verification.md).

## Отдельный Host

`Dockerfile` собирает Host в SDK 10.0.403 и переносит только publish output в
официальный runtime .NET 10. В финальном образе нет SDK, компилятора native-моста,
исходников станции или секретов. Процесс работает от стандартного непривилегированного
пользователя `app` (UID 1654 в официальных Linux .NET images).
Источники: [официальные .NET container images](https://github.com/dotnet/dotnet-docker),
[non-root user app](https://learn.microsoft.com/dotnet/core/docker/introduction).

```sh
docker build --tag netrun-host:local .
```

Host всегда слушает IPv4 `127.0.0.1:18462`. `EXPOSE` — только metadata образа;
публикация TCP-порта сама по себе не делает listener доступным вне его loopback.
Порт игрокам или внешней сети не открывается.

`NETRUN_SESSION_TOKEN_FILE` задаёт путь к файлу секрета; стандартное значение в
образе — `/run/secrets/netrun_session_token`. Файл содержит 32..256 printable ASCII
символов без пробелов; trailing newline удаляется reader. Значение токена не
передаётся через Docker build args, YAML, CLI arguments или логи.

Локальный запуск без контейнера использует тот же file contract:

```powershell
$env:NETRUN_SESSION_TOKEN_FILE = 'C:/private/netrun/session-token.txt'
dotnet run --project src/Netrun.Host -c Release -- serve 18462
```

Если уже задан `NETRUN_SESSION_TOKEN`, Host использует его приоритетно; для file-only
запуска прежнюю переменную нужно удалить из окружения процесса. Сам секрет в
репозиторий не добавляется.

## Ручной Compose пример для готового образа

В `compose.example.yml`:

- `byond` — существующий готовый образ станции, собранный с NETRUN адаптером;
- `netrun-host` использует `network_mode: service:byond`, поэтому оба процесса
  видят один и тот же `127.0.0.1`;
- только игровой порт 1337 публикуется наружу;
- `libnetrun_bridge.so` Linux **x86** монтируется read-only в `/tgstation/`;
- один session-token file доступен обоим участникам в `/run/secrets/`;
- приложение Host работает non-root с read-only filesystem и отдельным tmpfs.

Нужно задать только внешние пути и имя уже подготовленного образа:

```sh
export NETRUN_BYOND_IMAGE='your-registry/your-station:reviewed-version'
export NETRUN_BRIDGE_LINUX_X86='/absolute/artifacts/libnetrun_bridge.so'
export NETRUN_SESSION_TOKEN_FILE='/absolute/private/netrun-session-token.txt'
docker compose --file compose.example.yml config --quiet
# После отдельной проверки окружения:
# docker compose --file compose.example.yml up --build
```

Имена `your-registry/...` — placeholders, не готовые опубликованные образы.
Путь `/tgstation` соответствует рабочему каталогу текущего Dockerfile станции;
при другом layout измените target mount и native-конфигурацию согласованно.
Этот низкоуровневый пример нужен для уже собранного образа; штатный launcher выше
сам подготавливает согласованные images, bridge, режим и file secret.

Compose secrets из `file:` используют bind mount: `uid/gid/mode` не переназначают
владельца такого файла. Подготовьте host ownership/ACL так, чтобы файл читали
фактические UID обоих процессов, включая UID 1654 Host, сохраняя ограниченный
доступ к секрету. Не следует исправлять права запуском Host от root.
Источники: [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/),
[network_mode и права file secrets](https://docs.docker.com/reference/compose-file/services/).

Зависимость `service_started` гарантирует существование network namespace,
но не готовность Host к hello. Адаптер должен дождаться успешного соединения и
полного resync. После рестарта Host меняется engine epoch; новый binding и полный
snapshot обязательны. Автоматического повторения mutating invocation и
бесшовного fallback этот пример не обещает. SIGTERM отменяет listener и
классифицирует незавершённые native outcomes; аварийный kill не даёт drain.

## Native и CI

Windows bridge для DreamDaemon — x86; Python ctypes smoke загружает отдельный
x64 build. Linux .NET Host остаётся x64 и не загружает мост в свой процесс.
Сборка x86 `.so` сама по себе не доказывает совместимость с конкретным Linux BYOND.

`native-ci.yml` подготавливает три независимые проверки:

1. Windows: официальный portable Zig 0.17.0, проверка manifest и зафиксированного
   SHA-256, cross-build Windows x86/x64 и Linux x86, ABI/socket suite. BYOND-тест
   явно SKIP при отсутствии BYOND на hosted runner.
2. Linux: настоящий x64 ELF, построенный `g++`, загружается Python ctypes и
   проверяется loopback framed обменом, export table, oversized prefix и bounded close.
3. Docker: сборка multi-stage Host image без запуска серверной инфраструктуры.

Дополнительная Linux runtime-проверка запускается так:

```sh
mkdir -p native/bin
g++ -std=c++17 -shared -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden \
  native/netrun_bridge.cpp -pthread -ldl -Wl,--version-script=native/netrun_bridge.map \
  -o native/bin/libnetrun_bridge-x64.so
python tests/native/test_bridge_linux.py --library native/bin/libnetrun_bridge-x64.so
```

Это development toolchain. Сборку ELF, его runtime-проверку и полный запуск станции
учитывают отдельно; результаты и ограничения приведены в [verification.md](verification.md).


## Bounded concurrent sector topology and streaming backpressure

The topology builder builds each sector's spatial KD-tree independently and
connects APCs within independent sectors on up to four managed worker threads
(default `TopologyOptions.MaxParallelism = 4`; set to `1` to disable).
This is *parallel computation inside one Build*, not concurrent mutation of
`NetworkRuntime`: a single owner still publishes the final graph, and all
sector results are merged deterministically in their original order.
Small topologies remain on the serial path to avoid thread-pool overhead.
All service dispatch, invocation identity, and application ACK behavior is unchanged.

The deployed `Netrun.Host serve` executable now bounds both socket channels
to **64 frames** by default. When the DM peer is slower than the Host, a full
socket channel naturally propagates TCP backpressure instead of accumulating
unbounded `byte[]` frames. The default does not introduce application-level
input/output ACK credit or alter the previously negotiated protocol.

Environment variables for `serve` (positive integer 1..65536, or `0` to
disable) are:

| Setting | Default | Meaning |
| --- | ---: | --- |
| `NETRUN_HOST_QUEUE_CAPACITY` | 64 | Capacity of each of the two socket channels |
| `NETRUN_HOST_INPUT_WINDOW` | 0 | Optional cumulative input ACK credit |
| `NETRUN_HOST_OUTPUT_WINDOW` | 0 | Optional unACKed output credit |
| `NETRUN_HOST_EVENTS_PER_OUTPUT` | 0 | Optional events per output frame |
| `NETRUN_HOST_GRAPH_PAGE_WINDOW` | 0 | Optional graph application page credit |

**Only enable finite application windows after validating the corresponding
DM credit/retry handling.** Bounded socket queues are independent and safe by
default. These changes do not yet make the single-owner domain runtime or
service handlers run concurrently; future immutable-snapshot read workers
must commit through the ordered owner and discard stale revisions.
