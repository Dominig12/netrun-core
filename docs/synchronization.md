# Синхронизация узлов: snapshot и catch-up

Реализован автономный протокол **1.1** для согласованной публикации фактов узлов.
Он дополняет [узловой протокол 1.0](protocol.md), не подключая C# к игровому миру.
`WorldSynchronization` — последовательный владелец процесса синхронизации;
`WorldMirror` хранит факты и проверяет их поколения/версии.

`Ready` в этом документе означает только готовность **mirror фактов**. Граф,
маршруты, сервисные вызовы и право исполнять игровые эффекты ещё не реализованы.

## Жизненный цикл и публикация

```text
AwaitingSnapshot → Snapshotting → CatchingUp → Ready
                         ошибка целостности ↘ ResyncRequired
```

1. `snapshot_begin` фиксирует ID снимка и границу исходного журнала. Создаётся
   отдельный staging-mirror; читатели его не видят.
2. `snapshot_chunk` последовательно наполняет staging полными узловыми фактами
   или tombstones. Одновременно рассчитываются checksum и число записей.
3. `snapshot_commit` сверяет число chunks, число экспортированных записей и
   checksum. При совпадении начинается catch-up; **публикации ещё нет**.
4. `catch_up_delta` воспроизводит непрерывный журнал изменений после границы
   начала обхода, также только в staging.
5. `catch_up_complete` подтверждает точную воспроизведённую границу. Координатор
   заменяет опубликованный mirror целиком и переходит в `Ready`.
6. После `Ready` обычные `node_upsert`/`node_remove` применяются атомарными
   delta-пакетами к опубликованной модели.

Начать снимок можно только из `AwaitingSnapshot`, один раз в данной сессии.
Повторный begin в `Snapshotting`, `CatchingUp` или `Ready` не заменяет существующее
состояние: он получает `snapshot_state`. Для полного resync нужен новый binding
и новый экземпляр `WorldSynchronization`; повторное использование старого
координатора не является протоколом восстановления.

`Nodes` представляет read-only live-view опубликованного mirror для того же
потока, а не thread-safe immutable snapshot. До первого `catch_up_complete`
опубликованный mirror пуст. Входы применяются одним владельцем, повторный вход
в `Apply` запрещён.

## Счётчики и границы

Три последовательности имеют разный смысл и не заменяют друг друга:

| Поле | Значение |
|---|---|
| Envelope `sequence` | Все входные кадры, включая begin/chunk/commit/catch-up; начинается с `"1"` |
| `chunk_index` | Только chunks одного снимка: `"1"`, `"2"`, …; следующий chunk требует ровно предыдущий + 1 |
| `journal_sequence` | Граница журнала в begin или номер воспроизводимого journal delta; независима от input sequence |

Например, begin может иметь input sequence `"1"` и границу журнала `"40"`,
первый chunk — input sequence `"2"` и chunk index `"1"`, а первый catch-up delta
после commit — journal sequence `"41"`. Дельта с номером `"42"` вместо `"41"`
не может быть принята как «более свежая»: это потеря журнала.

`through_journal_sequence` в complete обязан совпадать с последним принятым
journal sequence. Если после границы begin изменений не было, complete повторяет
эту границу без catch-up delta. Переполнение ulong не оборачивается в ноль.

`AppliedSequence` — cumulative ACK **применения целого кадра координатором**.
До `Ready` это может означать применение control или изменений в staging,
а не публикацию фактов для читателя. Поля `NodeCount`/`TrackedCount` в результате
относятся к опубликованной модели; отдельное `StagedNodeCount` показывает размер
строящегося mirror. ACK получения байтов, ACK staging и разрешение игровых
эффектов нельзя считать одним событием.

## Управляющие записи 1.1

Envelope, UTF-8 framing, лимиты строк и строгая проверка полей сохранены из 1.0.
Новые управляющие записи требуют `protocol_major: 1`, `protocol_minor: 1`.
`BatchJson.Encode` выбирает minor 1 при наличии control; обычные узловые пакеты
сохраняют minor 0. Это автономное расширение схемы, пока без handshake и
capability negotiation.

**Один control занимает весь массив `entries` кадра.** Нельзя смешать begin с
chunk, node delta с commit или несколько controls. Вложенные `changes` содержат
только штатные `node_upsert`/`node_remove`, от 0 до 128 записей, без рекурсивных
controls и произвольных расширений типов.

| `kind` | Все обязательные поля помимо `kind` |
|---|---|
| `snapshot_begin` | `snapshot_id`, `journal_sequence` |
| `snapshot_chunk` | `snapshot_id`, `chunk_index`, `changes` |
| `snapshot_commit` | `snapshot_id`, `chunk_count`, `record_count`, `sha256` |
| `catch_up_delta` | `snapshot_id`, `journal_sequence`, `changes` |
| `catch_up_complete` | `snapshot_id`, `through_journal_sequence` |

`snapshot_id` — непустая непробельная строка до 128 UTF-16 code units без
управляющих символов и повреждённого Unicode. Он одинаков во всех controls
сессии. Все счётчики передаются каноническими десятичными **строками ulong**.
`journal_sequence`, `through_journal_sequence`, `chunk_count`, `record_count`
допускают `"0"`; chunk index, поколения и версии узлов — только положительные.
`sha256` содержит ровно 64 lowercase ASCII hex символа.

`record_count` считает все экспортированные записи chunks, включая tombstones,
повторные и устаревшие версии. Это не число уникальных ID и не число live-узлов.
Catch-up записи в checksum и record count снимка не входят.

## Межъязыковой canonical checksum

Снимок хэшируется инкрементально: **SHA-256 бинарного потока ниже**, а не JSON.
Порядок chunks и записей сохраняется. JSON whitespace, порядок свойств JSON
и экранирование не являются частью checksum. Никакой сортировки узлов или
канонизации JSON при commit не выполняется.

Условные обозначения:

- `u64be` — unsigned 64-bit integer, big endian, ровно 8 байтов;
- `u32be` — unsigned 32-bit integer, big endian, ровно 4 байта;
- `i32be` — signed 32-bit integer, big endian, ровно 4 байта;
- `string` — `u32be` длины **в байтах UTF-8**, затем эти байты без BOM;
- `byte` — ровно один байт.

Начало потока — ASCII `NETRUN-SNAPSHOT-1`, затем один нулевой байт, всего
18 байтов. Его hex-представление:

```text
4e455452554e2d534e415053484f542d3100
```

Для каждого chunk, в порядке поступления:

```text
u64be chunk_index
u32be число записей chunk
для каждой записи:
    byte kind: 1 для upsert, 2 для remove
    string node_id
    u64be generation
    u64be node_revision
    только для upsert:
        string address
        string node_type
        i32be x
        i32be y
        i32be z
        string sector_id
        byte online: 0 или 1
```

ID снимка не входит в digest, но смешивание chunks разных snapshot ID
отвергается. Пустой снимок без chunks хэширует только prefix. Пустой chunk
допустим, но добавляет свой index и нулевое число записей, поэтому меняет digest.
`GetDigest` не сбрасывает accumulator. Неверный chunk отклоняется до hashing,
не меняя digest, число chunks или число записей.

Golden vectors независимо проверены Python `hashlib` и зафиксированы в
`SnapshotChecksumTests.cs`:

| Вход | SHA-256 |
|---|---|
| Пустой снимок | `74298a4eed545a6d11cb260a1d2e798cd47a30838b7eee37f7f18965de4bb68b` |
| Один chunk из описанных ниже двух записей | `8df3cedc4227df0ae71339722af0cde256de66ef9a1061b81da3f48711f2b27f` |

Второй vector: chunk index 1; upsert `n1`, generation 1, revision 2, address
`А` (кириллическая U+0410, UTF-8 `d0 90`), type `apc`, XYZ `(10,20,1)`, sector
`s1`, online true; затем remove `n2`, generation 3, revision 4. Canonical поток
вместе с prefix занимает 108 байтов. Смена порядка записей или разбиение их
на два chunks изменяет digest.

## Ошибки и восстановление

Содержательно одинаковый повтор уже принятого кадра в bounded replay-окне
возвращает `duplicate` и текущий ACK. Он не добавляет chunk в checksum повторно,
не воспроизводит journal delta второй раз и не публикует модель снова.

| Ошибка | Поведение |
|---|---|
| Повреждённая схема/Unicode, недопустимые типы, вложенные controls | Wire-кодек отвергает вход; direct API возвращает `invalid_batch` без публикации или продвижения ACK |
| Epoch mismatch | `rejected / epoch_mismatch`, текущая сессия не меняется |
| Control в неверной фазе | `rejected / snapshot_state`; обычный node delta до Ready — `not_ready` |
| Регресс игрового времени | `rejected / time_regression`, ACK и время прежние |
| Gap input sequence; несовпадающий или вытесненный replay | `resync_required` |
| Неверный snapshot ID, gap chunk index, превышение лимита chunks | `resync_required` |
| Несовпадение chunk count, record count или checksum | `resync_required / snapshot_integrity` |
| Gap журнала или неверная финальная граница | `resync_required / journal_gap` либо `journal_boundary` |
| Конфликт версии узла, недопустимое воскрешение поколения или capacity mirror | Семантический отказ всего процесса синхронизации с причиной mirror и `resync_required` |

При `resync_required` staging и checksum немедленно освобождаются. Последний
опубликованный mirror остаётся доступен для диагностики, но сессия закрывает
admission и не принимает новые изменения. Состояние не исправляется отправкой
«недостающего» chunk или изменённого commit с тем же sequence. Восстановление —
новый binding и новый координатор после полного согласованного снимка.

Отклонённый без resync кадр не расходует sequence: исправленный кадр с тем же
следующим номером может быть рассмотрен снова. Это отличается от изменения
содержимого уже применённого кадра, которое нарушает replay-целостность.

## Изменение объектов во время обхода

Снимок собирается без остановки мира, поэтому журнал после границы begin
обязателен. Например, обход прочитал узел generation 1, затем объект удалён и
пересоздан. Catch-up удаляет старое поколение через tombstone и объявляет
generation 2. Старые факты generation 1 больше не могут воскресить объект.
Если обход успел прочитать generation 2, последующее воспроизведение более
старых событий generation 1 игнорируется как устаревшее.

Аналогично, при обходе может быть прочитана revision 8, а журнал сначала
содержит более раннюю revision 7. Она не заменяет более новую версию staging;
следующие события применяются с обычной проверкой поколений и ревизий.
Равная revision с отличающимся полным фактом — конфликт, а не разрешение выбрать
последний пришедший payload. После удаления даже большая revision того же
поколения не разрешает upsert: требуется новая generation.

Гарантия полноты исходного журнала, его ограничение по памяти и отмена обхода
при переполнении — обязанность будущего DM-адаптера. C# не может обнаружить
изменение, которое адаптер вообще не включил в свой журнал, или доказать, что
объявленная финальная граница отражает все факты реального мира.

## Пределы работы и памяти

По умолчанию `CoreLimits` ограничивает один список 128 записями, mirror —
10000 ID с учётом tombstones, snapshot — 10000 chunks, replay — 64 fingerprints.
Включаются и пустые chunks: ими нельзя обойти лимит общего обхода. В каждый
момент удерживаются максимум два mirror — опубликованный и единственный staging;
их capacity учитывается отдельно. До первой публикации опубликованный пуст.

Применение chunk и catch-up выполняется по затронутым записям без копирования
всего мира. Checksum инкрементальный; commit сверяет counts/digest без повторного
обхода staging. В ядре не накапливаются сами chunks или очередь journal payload:
catch-up поступает ограниченными входными кадрами. Время мира задаёт envelope;
wall-clock не продвигает симуляцию.

## Автономный Host

```powershell
dotnet run --project src/Netrun.Host -c Release -- replay-sync FILE.jsonl
dotnet run --project src/Netrun.Host -c Release -- framed-sync WORLD_EPOCH ENGINE_EPOCH BINDING_EPOCH
```

Первый режим читает ограниченный JSONL, второй — тот же length-prefixed binary
framing на stdin/stdout. Epochs задаются входной fixture либо аргументами стенда;
это не реализация авторизованного сетевого подключения. Бинарный режим требует
бинарных потоков, а не текстового PowerShell pipeline.

Успешный EOF допустим только после `Ready`. Обрыв до завершения snapshot/catch-up,
включая пустой ввод, даёт `snapshot_incomplete` и exit code 2. Протокольные и
семантические отказы также завершают прогон с code 2; успешная полная
синхронизация и последующие корректные deltas — с code 0.

Следующие области ещё отсутствуют: секторные факты и порядок связанных этажей,
объявления сервисов, DM-экспорт и его журнал, native-мост, аутентификация IPC,
handshake/capabilities, топология, маршрутизация, транспорт машин и live-cutover.
Этот пакет проверяет согласованность фактов и барьер публикации, а не паритет
или производительность всей сетевой симуляции.
