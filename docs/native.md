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

Все числовые аргументы — canonical положительные ASCII строки без ведущих нулей.
Пределы:

- host — только `127.0.0.1`; DNS, внешние адреса и IPv6 пока не разрешены;
- port — 1..65535;
- connection max_frames и poll max_frames — 1..128;
- max_queued_bytes — 1048576..16777216, отдельно для каждого направления;
- max_frame_bytes — 1..1048576;
- poll max_bytes — 128..8388608, предел **полного сериализованного ответа**.

`state`: `connecting`, `connected`, `disconnected`, `closing`, `closed`.
Успешный open возвращает `accepted`, а не подтверждение соединения. Успешный
enqueue означает копирование в ограниченную очередь, а не application ACK.
`would_block` означает, что квота или try-lock сейчас недоступны; повтор должен
подчиняться внешнему bounded admission, а не busy loop в игровом потоке.

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

Connect/read/write выполняются только worker, с partial I/O и select до 50 ms.
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

Скрипт принимает `-ZigPath`, либо ищет portable `zig.exe` в пользовательском
`~/.codex/cache/netrun-zig`. Сам скрипт ничего не скачивает, не устанавливает
production dependencies и не меняет PATH. Для разработки использован Zig 0.17.0:

- официальный manifest: `https://ziglang.org/download/index.json`;
- архив: `zig-x86_64-windows-0.17.0.zip`, 100268137 bytes;
- SHA-256: `b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428`;
- архив получен через Fastly mirror из официального
  `https://ziglang.org/download/community-mirrors.txt` и проверен этим hash.

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

Локальная проверка 2026-11-02: все три targets собраны с
`-Wall -Wextra -Werror`; 13 Python/native проверок прошли, включая настоящий
BYOND 516.1688 x86 smoke и production client/effects shutdown при живом worker.
До module pinning последний сценарий и isolated Windows `FreeLibrary` давали
`0xC0000005`; после исправления оба завершаются с exit0.
PE/ELF таблицы содержат ровно пять объявленных exports. Linux проверен
кросс-сборкой и чтением ELF; добавленный isolated `dlclose` regression должен
выполняться Linux CI, локальный Linux runtime не запускался.

Сборка bridge и автономные socket/ABI tests не доказывают end-to-end интеграцию
игры. Перед deployment остаются проверка целевой ОС/архитектуры, способ доставки
библиотеки, supervisor Host, session secret и политика восстановления DM-адаптера.
