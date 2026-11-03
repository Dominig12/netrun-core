# Сетевые файлы и носители

`StorageWorld` — единственный владелец содержимого сетевых файлов, UID,
носителей, вложенной памяти `.bot`, ACL и file capabilities. Он не обращается
к физической файловой системе ОС, BYOND objects или native callbacks. Shard,
local memory, server disk, session mount и salvage имеют отдельные `StoreKey`.
Физический `/obj` остаётся native-представлением носителя; сетевое содержимое
выдаётся как immutable read model.

В `NetworkRuntime` файловые команды используют тот же `CoreTaskQueue`, что
actor intent и запросы модов. `Apply` только проверяет causal envelope и
принимает задачу. Copy/move, merge grants, упаковка и восстановление выполняются
в последующем `Pump`. Если output заполнен, текущий FIFO head сохраняет уже
вычисленный результат и продолжает передавать проекции без повторной мутации.
Неизвестный native ответ не требуется для завершения обычной сетевой файловой
операции.

## Контракт

| Команда | Wire kind | Назначение |
| --- | --- | --- |
| `RegisterStorage` | `storage_register` | Trusted bootstrap/create/update носителя |
| `RemoveStorage` | `storage_remove` | Trusted факт удаления exact generation носителя |
| `SeedStorageFile` | `storage_seed` | Trusted bootstrap payload из мира/validated Core-модуля |
| `SubmitStorageOperation` | `storage_operation` | Пользовательская операция с actor identity и необязательной expected revision |

Register/seed/remove являются control plane доверенного native adapter.
UI adapter не должен проксировать их из произвольного пользовательского JSON.
У пользовательского `storage_operation` закрытый набор действий:
`create`, `transfer`, `delete`, `edit_text`, `rename`, `pack_agent`,
`restore_agent`, `offer`, `respond_offer`, `read`. Wire-команды для
произвольной выдачи grants/прав, вызова DM proc, исполнения кода или изменения
физической инфраструктуры отсутствуют.

Envelope повторяет существующий runtime v1.3: `request_id` — canonical
producer/serial, поколения и revision — десятичные строки UInt64. Store/file
keys имеют поля `id`, `generation`; actor key имеет `actor_id`, `generation`.
Typed DTO используют snake_case. Неизвестные и повторные поля, отсутствующие
обязательные constructor arguments, неизвестные action и неканонические
counters отклоняются до enqueue. `NetworkValue` хранит bounded immutable
metadata фотографии, research binding или сохранённых persona/behavior агента.

`StorageOperationCompleted` (`storage_completed`) содержит только scalar
`StorageResult`: success/code, file/store key, revision, offer ID.
`StorageChangedProjection` (`storage_changed`) содержит только store key,
revision, список заменённых acquired-key UID и tombstone `removed`.
Выходные события не включают содержимое, фото или recursive дерево файлов.
`CoreTaskCompleted` остаётся общей границей FIFO dispatch.

`NetworkRuntime.ReadStorage(StoreKey)` — trusted library API для immutable
проекции. Перед пользовательским чтением Core проверяет
`SubmitStorageOperation(ReadStorage)`. Большие UI/remote query требуют
отдельной paged проекции; whole `StorageView` не помещается в произвольный
framed output. Сам вызов `ReadStorage` не меняет store revision, не удаляет
файлы и не выполняет consolidation.

## Владение и capacity

Capacity уже учитывает `NETRUN_STORAGE_SCALE = 8`. Штатные значения:
AI — 384, server — 256, terminal — 192, gateway — 128, APC — 96,
остальные endpoint — 64. Явная capacity, включая 0, сохраняется как есть.
Уменьшение ёмкости и map preload могут оставить диск переполненным; новые
записи не вытесняют имеющиеся файлы.

`TextSize` считает Unicode code points: `max(1, ceil(characters / 500))`.
Пользовательский текст ограничен 10000 символами; редактирование заранее
проверяет `used - old_size + new_size`. Имя проходит те же преобразования,
что `netrun_file_name`: HTML удаляется, newline/tab становятся пробелами,
запрещённые filename-знаки удаляются, остаются первые 64 символа.

Firmware manual имеет нулевой размер в исходном носителе. Copy создаёт
обычный text с реальной стоимостью содержимого, включая manual внутри
вложенного контейнера. Read-only source допускает COPY; MOVE/delete/write
не изменяют его. COPY на тот же writable диск допустим, MOVE на тот же
диск отклоняется.

Каждый файл принадлежит ровно одному корню носителя, вложенному `.bot` или
инертному offer. MOVE сохраняет оригинальные UID всего дерева. COPY выдаёт
новые UID каждому элементу; shared immutable payload безопасен, изменение
копии создаёт новое значение и не меняет оригинал. UID prefix связан с
engine/binding epoch. Удалённое поколение `StoreKey` не регистрируется
повторно, и его старые ACL не действуют на новый диск.

`.bot` содержит базовую оболочку и вложенные файлы; общий размер равен
`base_size + sum(child.size)` и учитывает вложенную память один раз.
`PackAgentMemory` переносит настоящие файлы после проверки полной capacity;
непустая сохранённая память не перезаписывается. `RestoreAgentMemory`
проверяет весь loadout и capabilities до удаления прежнего содержимого.
При отказе оба носителя, их revision и UID остаются прежними. `Children=null`
сохраняет обычный стартовый loadout; `Children=[]` явно заменяет его пустой
памятью. Persona/behavior metadata остаются частью оболочки для entity owner.

## Права и capabilities

Владение `StorageDefinition.Owner` даёт обычные read/write/delete/transfer
права точному actor generation. Trusted Core authority может вызвать `Grant`
для другого actor. Эти права должны поступать из Core ACL/native possession
facts, а не из пользовательского заявления. Сам известный file UID не даёт
доступа. Проверка маршрута, native possession и non-owner server ACL остаётся
за composition/authority adapter; storage boundary не выдумывает эти права.

Обычный пользователь создаёт только text. Создание executable/access/grants
и других специальных файлов требует отдельного trusted `CreateSpecial`,
который не входит в обычное владение. File capabilities проверяются отдельно
от ACL носителя, в том числе для каждого элемента вложенной памяти.

Private session snapshot не копируется и не переносится, хранится только
в exact bound store живой session и может быть отозван удалением из своего
read-only session mount. `CanUseFile` проверяет session liveness и ownership;
после завершения погружения snapshot не даёт capabilities.

Acquired grants консолидируются на каждом носителе отдельно с сохранением
его canonical UID. Слияние принимает только более сильные действующие
scope stamps. Копируются оригинальные generation/domain/epoch, они не
перепривязываются к текущей ноде/сети. `ReadCurrentGrants` фильтрует по
`StorageScopeFact`; смена поколения, домена или ключевого epoch отзывает
сохранённые grants. Чтение не запускает merge и не изменяет files/revision.

## Предложения и очередь

Offer создаёт независимый инертный snapshot и сохраняет оригинал отправителя.
Mailbox ограничен восемью предложениями, срок — 1200 ds, повтор отправки —
не раньше 20 ds. Полная память получателя не мешает предложению, если сам
файл укладывается в максимальную capacity. Принятие повторно проверяет
реальное место/capabilities; при отказе предложение остаётся доступным.

`NextExpiryDs` сообщает ближайший срок. Runtime назначает внутренний expiry
intent в хвост общей FIFO; отдельного DM/C# scheduler для mailbox нет.
Admission нового предложения освобождает уже истёкшие snapshots перед
проверкой восьми slots, даже если expiry intent ещё ожидает dispatch.

Replay ledger хранит SHA256 и scalar outcome. Повтор того же logical key
не создаёт вторую копию, конфликт содержимого отклоняется, забытый key
защищён producer watermark. Output pressure сохраняет непринятый producer
head. Hard `MaxFiles` capacity даёт terminal отказ, чтобы последующая FIFO
задача удаления могла освободить место. Default application limits не
ограничивают количество носителей/файлов; bounds tree depth/count относятся
к форме одного передаваемого определения.

`EntityStorageTests` и `StorageRuntimeTests` проверяют atomic capacity,
nested UID/capabilities, firmware copies, private sessions, scope revocation,
offers/replay, общий actor/storage FIFO, bounded output continuation и
отсутствие recursive payload в ответе Host.
