# Нативный bridge для BYOND 516

`native/netrun_bridge.cpp` — отдельный portable C++17 transport bridge с legacy
string ABI BYOND. В нём нет маршрутизации, DM references, сервисных эффектов или
автоматического retry. Один worker владеет сокетом; вызовы DM копируют аргументы
и используют только try-lock, не ожидая mutex, свободной очереди или ответа сети.

## ABI и владение памятью

Пять exports имеют сигнатуру `extern "C" char* name(unsigned int argc, char** argv)`
с Windows cdecl. Все аргументы передаются строками. Это проверено по установленной
справке BYOND 516.1688 (`help/ref/info.html`, раздел call_ext).

Входные строки ограниченно копируются до возврата вызова; worker не сохраняет
borrowed argv. Текст проверяется как UTF-8 без замены повреждённых символов.
JSON-тело непрозрачно для моста: его схему, handshake и аутентификацию проверяет Host.
На socket wire добавляется 4-byte unsigned big-endian длина тела. Бинарный header
не проходит через C strings, где нулевой байт завершает строку.

Возвращается JSON в thread-local буфере библиотеки, действительном до следующего
вызова на том же потоке. BYOND копирует его при возврате. Исключения C++ не
пересекают ABI; fallback — фиксированный `internal_error`, без payload или секретов.

## Операции

| Export | Аргументы | Результат |
|---|---|---|
| `netrun_open` | host, port, max_frames, max_queued_bytes, max_frame_bytes | `{state,code}` |
| `netrun_try_enqueue` | непрозрачное UTF-8 JSON body | `{state,code}` |
| `netrun_poll` | max_frames, max_bytes | `{frames:[body strings],state,code}` |
| `netrun_status` | нет | `{state,code,outbound_frames,outbound_bytes,inbound_frames,inbound_bytes}` |
| `netrun_close` | нет | `{state,code}` |

Все числовые аргументы — canonical ASCII строки без ведущих нулей или знака.
Точный `"0"` разрешён только для отключённых connection quotas.
Пределы:

- host — только `127.0.0.1`; DNS, внешние адреса и IPv6 пока не разрешены;
- port — 1..65535;
- connection max_frames — `0` без квоты либо положительное значение до `size_t` maximum;
- max_queued_bytes — `0` без квоты либо 1048576..`size_t` maximum, отдельно для каждого направления;
- poll max_frames — 1..128, размер одной порции ABI, не connection quota;
- max_frame_bytes — 1..1048576;
- poll max_bytes — 128..8388608, предел **полного сериализованного ответа**.

`state`: `connecting`, `connected`, `disconnected`, `closing`, `closed`.
Успешный open возвращает `accepted`, а не подтверждение соединения. Успешный
enqueue означает копирование в очередь, а не application ACK.
`would_block` означает, что квота или try-lock сейчас недоступны; повтор должен
кооперативно уступать CPU, не создавая busy loop в игровом потоке.

Poll возвращает исходные тела как JSON strings. Он предварительно считает
escaping и полный размер ответа. Если первый кадр не помещается, возвращает
`would_block` с пустым массивом и оставляет кадр в очереди. После успешной
сериализации целого ответа удаляются только возвращённые кадры. Физические байты
и форма вложенного JSON не интерпретируются мостом.

При занятости status-lock возвращается короткий `{state,code:"would_block"}` без
счётчиков. Другие безопасные коды: `invalid_arguments`, `already_open`, `closed`,
`frame_size`, `worker_start_failed`, `connect_failed`, `io_error`, `peer_closed`,
`invalid_utf8`, `internal_error`.

## Очереди и жизненный цикл

Квоты frames/bytes включают исходящий кадр в partial write и входящий кадр после
резервирования объявленного размера, до завершения чтения. Размер входного тела
проверяется после header **до allocation**. При заполнении входной очереди worker
приостанавливает чтение тела и применяет TCP backpressure. Завершённые входные
кадры остаются доступны poll после disconnect; незавершённое тело не публикуется.

Connect/read/write выполняются только worker, с partial I/O и select до 10 ms.
TCP_NODELAY исключает ожидание delayed ACK; header и body отправляются одним
wire buffer без отдельного маленького TCP-пакета под четырёхбайтовый header.
Connect timeout — 10 секунд физического времени. Close помечает остановку и
возвращает немедленно; shutdown/close сокета выполняет worker. На DM-потоке нет
join. Следующий open запрещён, пока прежний worker не подтвердил завершение;
после disconnect требуется явный close. Автоматической пересылки старых кадров
на новое соединение нет. Судьбу принятых, но не подтверждённых запросов определяет
Host/DM request lifecycle, включая неопределённый исход эффекта.

При штатном shutdown адаптер вызывает close и ограниченно ожидает `closed`.
Перед первым worker библиотека один раз закрепляет собственный модуль до конца
процесса: Windows `GetModuleHandleExW(FROM_ADDRESS | PIN)`, Linux
`dladdr` + `dlopen(RTLD_NOW | RTLD_NOLOAD | RTLD_NODELETE)` с одной resident reference.
Поэтому преждевременный `FreeLibrary`/`dlclose` со стороны BYOND не выгружает код
или TLS активного worker. Singleton также сохраняется на время процесса.
Замена загруженного bridge требует перезапуска процесса; экстренное завершение
не обещает drain. Linux loader API требует линковки `-ldl`.
Контракт loader проверен по [Microsoft GetModuleHandleExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw),
[Linux dlopen](https://man7.org/linux/man-pages/man3/dlopen.3.html) и
[Linux dladdr](https://man7.org/linux/man-pages/man3/dladdr.3.html).

## Локальная сборка

```powershell
./scripts/build-native.ps1 -Target all
python tests/native/test_bridge.py
```

На Linux x64 реальный socket/ABI и isolated `dlclose` regression запускаются так:

```sh
mkdir -p native/bin
g++ -std=c++17 -shared -fPIC -O2 -Wall -Wextra -Werror -fvisibility=hidden \
  native/netrun_bridge.cpp -pthread -ldl -Wl,--version-script=native/netrun_bridge.map \
  -o native/bin/libnetrun_bridge-x64.so
python tests/native/test_bridge_linux.py --library native/bin/libnetrun_bridge-x64.so
```

Скрипт принимает `-ZigPath`/`NETRUN_ZIG`, проверяет версию в PATH либо автоматически
получает portable Zig 0.17.0. Cache находится в `%LOCALAPPDATA%/NETRUN/toolchains`,
другой абсолютный путь задаёт `NETRUN_TOOLCHAIN_CACHE`. Глобальная установка и
изменение PATH не выполняются. Bootstrap проверяет SHA-256 перед распаковкой,
использует file lock и публикует готовый cache атомарно. Источник версии:

- официальный manifest: `https://ziglang.org/download/index.json`;
- архив: `zig-x86_64-windows-0.17.0.zip`, 100268137 bytes;
- SHA-256: `b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428`;
- архив скачивается по HTTPS; при отказе основного сервера используются mirrors
  из опубликованного Zig списка с тем же закреплённым hash. При недоступности всех
  источников bootstrap завершается ошибкой, готовые инструменты сохраняются.

SDK и cache находятся вне репозитория. `native/bin/`, Python pycache и готовые
бинарники исключены из source control.

Выходы сборки:

| Файл | Назначение |
|---|---|
| `native/bin/netrun_bridge.dll` | Windows x86 для настоящего DreamDaemon 516 |
| `native/bin/netrun_bridge-x64.dll` | Windows x64 для Python ctypes socket tests |
| `native/bin/libnetrun_bridge.so` | Linux x86, glibc baseline 2.17; требует отдельного Linux runtime-прогона |

Минимальный `tests/native/bridge_harness.dm` компилируется в отдельном временном
каталоге. Он не включает станцию, реально загружает x86 DLL через call_ext,
передаёт кадр loopback echo-server и проверяет корректное закрытие. Python suite
также проверяет fragmented/concatenated frames, escaping-aware poll budget,
ограничение входной очереди, oversized header, некорректный UTF-8 и bounded close.

Локальная Windows проверка 2026-11-03: свежие x86/x64 DLL собраны, 16/16 native
тестов прошли без пропусков. Проверены canonical zero quotas, 160 buffered frames
и более 1 MiB без искусственной квоты, отдельные finite quotas, настоящие BYOND
x86 open/echo/shutdown и module lifetime. Tiny E2E использует production DM
exporter/effects и verbatim monitoring receiver: реальная idle telemetry не
меняет application sequence/ACK. Проверка ELF в этом наборе читает export table
готового Linux файла и не доказывает Linux runtime.

Историческая проверка 2026-11-02: все три targets собраны с
`-Wall -Wextra -Werror`; 13 Python/native проверок прошли, включая настоящий
BYOND 516.1688 x86 smoke и production client/effects shutdown при живом worker.
До module pinning последний сценарий и isolated Windows `FreeLibrary` давали
`0xC0000005`; после исправления оба завершаются с exit0.
PE/ELF таблицы содержат ровно пять объявленных exports. Linux x86 проверен
кросс-сборкой и чтением ELF. В [Linux x64 CI](https://github.com/Dominig12/netrun-core/actions/runs/41394652977)
прошли все пять настоящих ABI/socket тестов, включая isolated `dlclose` regression.
Это не заменяет запуск x86 bridge внутри целевого Linux BYOND.

Сборка bridge и автономные socket/ABI tests не доказывают end-to-end интеграцию
игры. Перед deployment остаются проверка целевой ОС/архитектуры, способ доставки
библиотеки, supervisor Host, session secret и политика восстановления DM-адаптера.
