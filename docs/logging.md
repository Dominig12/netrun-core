# Журналирование и постоянная телеметрия Host

`serve` пишет JSONL в stderr. Один readiness-текст
`NETRUN Host listening on loopback:PORT` остаётся в stdout: startup tools
продолжают находить его без изменения контракта. Фиксированные коды ошибок
запуска (`invalid_port`, `session_token_missing`, `io_error`,
`file_access_denied`, `invalid_arguments`) также остаются в stderr для
совместимости; JSONL collector должен принимать только строки с JSON-объектом.
Режимы offline replay и framed используют свои прежние потоки.

Для постоянной истории задайте существующий каталог и файл:

```powershell
$env:NETRUN_LOG_FILE = 'C:\netrun\logs\host.jsonl'
$env:NETRUN_LOG_LEVEL = 'info'
dotnet run --project src/Netrun.Host -- serve
```

В Docker передайте эти переменные процессу Host и смонтируйте каталог журналов
в volume. Секреты задаются прежними `NETRUN_SESSION_TOKEN[_FILE]` и в журнал
не попадают. Не направляйте stdout в файл JSONL: там находится readiness.

| Переменная | По умолчанию | Назначение |
| --- | --- | --- |
| `NETRUN_LOG_LEVEL` | `info` | `debug`, `info`/`information`, `warning`, `error`, `none` |
| `NETRUN_LOG_FILE` | отсутствует | Дополнительный JSONL-файл в append-режиме |
| `NETRUN_LOG_CAPACITY` | `4096` | 1–1 000 000 ожидающих записей, отдельно от игровых очередей |
| `NETRUN_LOG_MAX_FILE_BYTES` | `16777216` | Мягкий предел активного файла, ротация перед следующей записью |
| `NETRUN_LOG_RETAINED_FILES` | `4` | 1–32 архива `.1` ... `.N`, кроме активного файла |
| `NETRUN_LOG_TELEMETRY_INTERVAL_MS` | `2000` с файлом, иначе `0` | 0 отключает постоянные снимки, 1–3 600 000 задаёт интервал |

Неизвестные уровни, отрицательные и некорректные числа завершают запуск с
`invalid_arguments`; текст неверного значения не выводится. Каталог файла
создаёт deployment. Недоступный файл при запуске вызывает обычную ошибку
доступа/I/O. Отказ sink во время работы отключает только этот sink, остальные
продолжают работать. Архивы относятся к настроенному файлу; не размещайте
посторонние файлы по именам `host.jsonl.1` ... `.N`. Размер истории ограничен
активным файлом и N архивами; единичная JSONL-запись сохраняется целиком даже
при размере больше мягкого предела. Один путь журнала предназначен одному
процессу Host.

Каждая запись содержит UTC `timestamp`, `level`, `event`, `process_id`,
`dropped_total`, `sink_failures`. После аутентификации добавляются
`connection_id`, `world_epoch`, `engine_epoch`, `binding_epoch`. Эпохи
совпадают с framed output и DM monitor, поэтому `binding_epoch` связывает
логи DM и Host. `connection_id` создаётся Host и связывает handshake с
жизненным циклом соединения. Неудачный handshake не экспортирует входные
эпохи или token.

На уровне `debug` события `command_applied`, `invocation_issued`,
`request_completed`, `projection_issued` содержат подходящие
`input_sequence`, `output_sequence`, `request_id`, `invocation_id`. Sequence
в этих полях записываются десятичными строками без потери uint64-точности.
`command_rejected` и неуспешный/неизвестный terminal outcome видны уже на
`warning`. `host_started`, `host_stopped`, `connection_accepted`,
`connection_closed`, `session_started`, `session_disconnected`,
`runtime_state` видны на `info`, `protocol_error` и
`authentication_rejected` — на `warning`. Секрет handshake, payload,
authorization/native context, raw JSON и exception messages не входят в
контракт записи.

`runtime_metrics` содержит агрегированные количества узлов, сервисов,
запросов, поисков, проекций, событий, размеры ingress/egress и replay journal,
ACK backlog, managed memory, последние и максимальные затраты owner.
Снимки снимает единственный runtime owner, без `Pump`, продвижения игрового
времени или записи в protocol journal. Они работают при выключенном live
DM monitor и не создают сетевых ответов. Это постоянная история и формат
экспорта для внешнего collector; отдельный server/dashboard не требуется
для чтения JSONL.

`runtime_pressure` (`warning`) появляется на переходе к >=4096 кадрам в
ingress/egress или unacked output, либо >=64 MiB replay journal.
`runtime_recovered` появляется после возврата всех показателей ниже порогов.
Проверка делается на частоте постоянных снимков и отключена вместе с ними.
Эти события можно направить в alert collector; Host сам не отправляет
внешние уведомления. Дополнительно `runtime_state` фиксирует смену
синхронизации/ошибки топологии/communications без повторения на каждом pulse.

Диагностика использует отдельную bounded очередь и `TryWrite`. Медленный
диск/stderr теряет новые записи, увеличивая `dropped_total`, и не задерживает
runtime/reader/writer. `sink_failures` считает отключённые устройства; оба
счётчика доступны также через `HostLog`. При остановке ожидается не более
двух секунд flush, после чего worker отменяется. Процесс не ждёт
некооперативное устройство бесконечно; последние записи могут потеряться.

Проверки: `HostLogTests` покрывают работающий TCP при зависшем sink,
ограниченный shutdown, корреляцию и отсутствие секретов, постоянные снимки
без negotiated monitor, отказ stderr с сохранением файла и bounded ротацию.
