# DM-клиент внешнего ядра

Интеграция станции находится в `modular_bluemoon/code/modules/netrun/integration/`:
`external_core.dm` экспортирует исходные факты и ведёт протокол, а
`external_effects.dm` удерживает исходные machine messages и исполняет разрешённую
операции `message` и заменяемого состояния `state`. C# выбирает маршрут и ведёт transport lifecycle; native
`matches`, `accepts` и `receive` остаются окончательной проверкой игрового действия.

## Режим и конфигурация

Режим читается через `world.GetConfig("env", name)` до регистрации первого узла
и фиксируется на раунд. Endpoint и token file перечитываются при bounded recovery.

| Переменная | Значение |
| --- | --- |
| `NETRUN_CORE_MODE` | Только `external` (значение по умолчанию) |
| `NETRUN_CORE_PORT` | Локальный TCP-порт, по умолчанию `18462` |
| `NETRUN_CORE_LIBRARY` | Windows x86 `netrun_bridge.dll`; Linux x86 `./libnetrun_bridge.so` |
| `NETRUN_CORE_TOKEN_FILE` | Путь к ignored/mounted файлу с общим session token |

Production default действует и при прямом запуске DreamDaemon. Значения `dm` и
`shadow` отклоняются с `invalid_mode` до чтения token/library/port. Нативные
построители topology, APC mesh/binding, Dijkstra и machine transport удалены.
Machine dispatch передаёт ownership сообщения внешнему executor после готовности
mirror и graph. Недоступность ядра завершает сообщение отказом.
Первый успешный handshake с опубликованным graph за binding отмечается фиксированным
`NETRUN_CORE_READY external graph_published`; marker не содержит секретов.

Файл секрета ограничен 1024 байтами. После удаления внешних пробельных символов
token должен содержать 32..256 printable ASCII символов без пробелов. Сам token
не передаётся в `world.params`, CLI или логах. Host использует тот же файл через
`NETRUN_SESSION_TOKEN_FILE`; подробности размещения приведены в
[deployment.md](deployment.md). Bridge допускает только IPv4 `127.0.0.1`.

Пример окружения локального DreamDaemon:

```powershell
$env:NETRUN_CORE_MODE = 'external'
$env:NETRUN_CORE_PORT = '18462'
$env:NETRUN_CORE_LIBRARY = 'C:/artifacts/netrun_bridge.dll'
$env:NETRUN_CORE_TOKEN_FILE = 'C:/private/netrun/session-token.txt'
```

Здесь требуется x86 bridge: разрядность библиотеки совпадает с DreamDaemon, а не
с отдельным .NET Host. Производственный сервер эти команды не запускают.

## Facts и барьер готовности

Клиент отправляет `hello` protocol 1.3 с `world_epoch`, новым `binding_epoch` и
token. Host назначает `engine_epoch` и подтверждает checksum version 3. Token
удаляется из состояния клиента после успешного handshake.

Snapshot последовательно обходит живые реестры узлов и сервисов: до 32 записей и
до 32 посещений реестра за проход. Никакие graph/routing/discovery методы для
экспорта не вызываются. Экспортируются raw nodes, ordered sector levels и
service family/tag/owner/operation contract. Physical position может быть `null`.
Только настоящий hub/relay рекламирует optional `communication`: native switches,
object.z, направленные configured links и frequency filter. Экспорт использует
только существующие components. Непредставимый filter снимает capability целиком,
чтобы усечённый пустой filter не разрешил все частоты. Native setters вызывают
`netrun_external_ports_changed()` после изменения фактов.
Opaque IDs назначаются монотонно; удалённый и вновь созданный компонент получает
новый ID, даже если прежний адрес совпадает. Поколение каждого такого ID — `"1"`.

Изменения во время сканирования попадают в журнал: максимум 4096 записей и
1 MiB DM-символов. Удаления не теряются при уплотнении. Overflow, конфликт или
потеря соединения останавливает сессию вместо продолжения с неполными фактами.
После snapshot commit отправляется catchup с непрерывным journal sequence и
точным завершающим boundary. Старый journal revision не может откатить более
новый snapshot revision. Случай удаления/замены объекта во время сканирования
проверяется интеграционным harness.

Checksum 3 не требует бинарных NUL в DM:

```text
chain = lowercase_sha256_utf8("NETRUN-SNAPSHOT-3")
chunk_digest = lowercase_sha256_utf8(exact_changes_json)
chain = lowercase_sha256_utf8(chain + "|" + chunk_index_decimal + "|" + record_count_decimal + "|" + chunk_digest)
```

`changes` сериализуется один раз и сохраняется неизменным до enqueue; Host
хэширует точный raw UTF-8 JSON фрагмент. Порядок и пробелы не канонизируются.
Input sequence, output sequence, chunk index и journal sequence независимы.
Все wire-счётчики — canonical decimal strings, арифметика не использует DM float.

`runtime_result.applied_sequence` означает применение control/staging, пока
`sync_state` ещё не `ready`. Клиент разрешает dispatch только когда одновременно
`sync_state == "ready"`, `topology_pending == false` и `graph_version != "0"`.
Facts и snapshot всегда отправляются до logical requests/results/cancel.

## Native ownership и защита от повтора

В executor находятся максимум 2048 исходных message contexts, 2048 команд и
4096 invocation ledger записей. Context сохраняет исходное сообщение, payload
JSON/hash, захваченные service/node поколения, tag, weakrefs endpoints и
пользователя. Producer `dm` назначает ID вида `dm:1`; native context непрозрачен
и не содержит BYOND `REF`.

Semantic `would_block` потребляет outer input sequence, но следующая попытка
сохраняет request ID и исходную команду. Generic `message` объявляется mutating,
`delivery_only`, ordered; coalescing выключен. Перед native receive executor
повторно проверяет epochs, context/request ID, service/node identity, captured
tag, неизменность payload, deadline, текущие `matches` и `accepts`. Имена proc и
typepath из входного сообщения не исполняются.

Заменяемые snapshots явно возвращают `coalescing_key()` и используют отдельный
ordered/idempotent `state` contract. `request_submit` передаёт этот key и optional
положительную `state_revision`. Research native revision 0 отображается в wire
revision 1. C# сохраняет первый request ID и due, заменяя payload и opaque contexts
при новой либо равной revision. DM переносит актуальный message, user weakref и
payload proof в сохранённый context; superseded request освобождает свой slot
без второго callback. Более старая revision оставляет актуальное состояние.
Полные research snapshots и compact state используют разные keys. Обычные команды
сохраняют mutating `message` contract и отдельные outcomes.

Перед native callbacks и повторно после них проверяются текущий binding,
service/node identities, ACK всех известных infrastructure facts, exact endpoint
node revisions и C# `infra_stamp`/`av_infra_stamp`. Старый invocation из output
backlog не получает authority после изменения transit infrastructure. Leaf churn
сам по себе infrastructure stamp не меняет. Native callbacks могут yield; после
них также повторяется deadline check.

DM ограничивает исходный JSON размером 128 KiB. Структуру payload и общий
NetworkValue budget проверяет Host до admission: depth 32, 4096 values, 1024
entries/container, UTF-16 text/key limits и conservative JSON estimate 256 KiB.
Недопустимый native payload получает semantic admission rejection своего message;
связь других запросов сохраняется. Communication traffic разрешён только для
native telecomms/communications message с allowlisted service families; ordinary
messages не получают общий обход через telecomms.

Запись `executing` создаётся до вызова native handler и до output ACK. Повтор
одного invocation с тем же fingerprint не вызывает handler второй раз;
известный результат можно отправить повторно. Конфликт fingerprint или
неизвестный mutating output не подтверждается и закрывает сессию. Если handler
yield, исходное сообщение остаётся живо до его возврата. Исключение после
возможного изменения мира классифицируется как `outcome_unknown`.

`delivered` либо `failed` вызывается один раз. Потеря соединения завершает
не начатые native действия известным отказом, а начатые — неизвестным исходом.
Executing callbacks удерживаются до возврата и не отправляют старый результат
в новый binding. Автоматического повторения mutating actions нет.

Живой subsystem после closed/resync повторно читает тот же token file с физическим
backoff 1, 2, 4, 8, 16, 30 секунд. Client и effects сохраняются, world epoch остаётся
прежним, а binding и полный snapshot создаются заново. Queued mutating messages
не переносятся; old executing callbacks остаются disconnected до своего возврата.
Backoff сбрасывается только после ready. Изменение известного connected stack,
его порядка или overlap Z отзывает сессию до публикации несовместимого sector fact;
bounded Z→sector index очищается при close.

Active invocation освобождается после terminal context и ACK известного результата,
поэтому постоянная нагрузка не требует общего idle-окна. Executing и unACKed
result pointers остаются закреплены. Отдельный FIFO до 4096 terminal fingerprints
подтверждает поздние точные повторы без второго effect; конфликт или уже вытесненный
неизвестный ID отвергается. Консервативный producer watermark передвигается только
при отсутствии всех pending contexts. TTL не используется. При достижении предела
новые действия явно отклоняются.
Пока есть pending transport, пустые logical frames передают актуальный
`round(world.time)` для due/deadline. Физический idle poll игровое время не двигает.

## Точки расширения и проверки

`/datum/netrun_external_core` предоставляет `open`, `tick`, `poll`,
`process_frame`, `enqueue`, `close`, `node_key`, `service_key`, `resolve_node` и
`resolve_service`. Base effects предоставляет `prepare_input`, `observe_result`,
`process_result` и `disconnected`. Machine executor добавляет `dispatch`,
`cancel` и `queued_commands`; logical команда отправляется после своего captured
source-fact prefix, поэтому более новые несвязанные изменения не требуют idle-окна.

`code/modules/unit_tests/netrun_external_core.dm` содержит hermetic cases для
fake IPC при настоящем native dispatcher, decimal counters, semantic retry, nested payload shape,
identity/tag checks, invocation dedup, native exception, disconnect во время
yield, watermark pressure, отказ удалённых режимов, state coalescing и logical clock. Запуск в checkout станции:

```sh
node tools/netrun/run-tests.mjs --filter external_core
```

`tests/native/external_harness.dm` — изолированный tiny world, подключающий те же
production client/effects исходники. Локальный Windows BYOND 516.1688 реально
прошёл bridge → Host → checksum-3 snapshot/catchup с source churn → graph-ready →
request admission → единственный native receive → invocation result → единственный
terminal callback. Неизвестная invocation проверена без output ACK. Этот прогон
не запускал station world или производственную инфраструктуру.

Воспроизводимый runner использует существующие SDK/BYOND/native artifacts и
создаёт secret в temp directory вне checkout:

```sh
python tests/native/test_external_dm.py
# Уже проверенный опубликованный пакет можно указать явно:
python tests/native/test_external_dm.py --host-dll PATH/Netrun.Host.dll --library PATH/netrun_bridge.dll
```

Default station Build/Run подключает managed Host/native lifecycle из
`tools/netrun/core-lifecycle.mjs`; Linux Docker использует два образа с одним
проверенным Core source digest и общей network namespace. Подробные команды
находятся в station `tools/netrun/EXTERNAL_CORE.md`.

Windows ABI/socket suite описан в [native.md](native.md). Linux x86 библиотека
cross-built; запуск именно Linux BYOND остаётся отдельной проверкой окружения.
