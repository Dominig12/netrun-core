# Подготовка сборки и развёртывания

Штатные Windows build/run команды станции автоматически собирают и подключают
Core. Для Linux подготовлены два согласованных Docker target и Compose launcher.
Точное описание находится в [DM integration bundle](../integration/dm/README.md)
и [инструкции станции](../integration/dm/files/tools/netrun/EXTERNAL_CORE.md).
Существующий production сервер эти изменения автоматически не развёртывают.

## Штатный launcher станции

Рядом с checkout станции находится `netrun-core`; другой путь задаётся
`NETRUN_CORE_SOURCE`. В Windows обычные `Build.bat` и `RUN_SERVER.bat` используют
Juke targets, которые публикуют Host и x86 DLL из одного immutable source context.
Перед запуском проверяются source digest и artifact hashes. Launcher создаёт
ignored token file, ждёт listener Host и запускает DreamDaemon; завершение одного
процесса закрывает принадлежащую launcher пару. По умолчанию действует `external`.

В Linux из корня станции:

```sh
export NETRUN_CORE_SOURCE=/reviewed/checkouts/netrun-core
export NETRUN_CONFIG_DIRECTORY=/reviewed/station/config
node tools/netrun/docker-lifecycle.mjs build
node tools/netrun/docker-lifecycle.mjs up --no-build
```

Buildx получает минимальный named context с точными bytes исходников и SHA-256
manifest. Образы `netrun-station` и `netrun-host` помечаются одним digest; launcher
проверяет его перед запуском. Станция и Host делят loopback namespace и file secret;
наружу публикуется только игровой порт. Станция ждёт Host, а attached Compose
останавливает пару при выходе любого контейнера. `down` сохраняет data volume.

BYOND runtime остаётся в Xenial; TGUI/DM собираются в отдельном pinned Node 24
Bookworm stage. .NET 10 работает в поддерживаемом runtime image, а bridge строится
для x86/glibc 2.17. Такой layout не требует запускать Node/.NET внутри Xenial.
Docker Engine и рабочий WSL локально отсутствуют; Docker build/up и Linux BYOND
не выполнены. Unit tests launcher не заменяют эти runtime проверки.

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

Это development toolchain, не установка production-зависимостей. Подготовка этого
пакета выполнялась в Windows; Docker CLI недоступен. Docker build/Compose запуск
и Linux ctypes smoke здесь не выполнялись.
Удалённые GitHub workflows также не считаются пройденными до реального CI run.
