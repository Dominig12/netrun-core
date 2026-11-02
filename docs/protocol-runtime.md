# Runtime protocol 1.3: discovery, projections и native handoff

Transport framing — unsigned 4-byte big-endian длина и UTF-8 JSON body до 1 MiB.
Соединение сначала проходит authenticated hello и полный snapshot/catch-up.
Примеры ниже описывают logical frames уже выбранного binding; токен в них отсутствует.

Каждый logical frame содержит ровно одну команду либо batch world facts. Команду
нельзя смешивать с world facts или другой командой. Все поля в примерах обязательны,
включая nullable поля; неизвестные и повторные поля отвергаются.

`sequence` — положительный canonical decimal UInt64 string. `ack_output_sequence`
— cumulative ACK **обработанных** output frames, с допустимым значением `"0"`.
Положительные счётчики не содержат leading zero, sign или whitespace.
`request_id` — `producer:sequence`: producer содержит только ASCII letters/digits
и `._-`, максимум 64 символа, sequence положительный UInt64. Отдельный producer
использует возрастающие serials за binding, в том числе после вытеснения history.

## Discovery

```json
{
  "protocol_major": 1,
  "protocol_minor": 3,
  "world_epoch": "world",
  "engine_epoch": "engine",
  "binding_epoch": "binding",
  "sequence": "5",
  "ack_output_sequence": "4",
  "simulation_time_ds": 10,
  "entries": [{
    "kind": "service_discover",
    "request_id": "lookup:1",
    "source": {"service_id": "source-service", "generation": "1"},
    "destination_family": "research.authority",
    "recipient_tag": null,
    "communications": false,
    "frequency": null,
    "max_results": 4096
  }]
}
```

`max_results` допускает 1..4096, default typed API — 4096. `recipient_tag` — null
либо exact tag до 256 UTF-16 units. `communications` явно разрешает отдельный
allowlisted fallback; обычный путь имеет приоритет. `frequency` — null либо
положительный Int32, причём ненулевое значение требует `communications: true`.

Команда отмены вместо `entries` выше:

```json
[{"kind":"service_discovery_cancel","request_id":"lookup:1"}]
```

Один элемент `results` выходного кадра:

```json
{
  "kind": "discovery_page",
  "binding_epoch": "binding",
  "request_id": "lookup:1",
  "candidates": [{"service_id":"authority-service","generation":"1"}],
  "complete": true,
  "code": "completed"
}
```

Страница содержит максимум 64 service keys. `page` означает продолжение,
`completed` — полный bounded ответ, `truncated` — достигнут candidate/result cap.
Native `matches`, `accepts`, permissions и `source.select_targets` выполняются
после получения кандидатов. В частности, Research не отсекается до первого
кандидата до применения native policy. Это сетевое наблюдение; последующий
transport повторно проверяет маршрут и поколения перед invocation.

## Projection query и cancel

Graph query занимает одну `entries` запись:

```json
{
  "kind": "projection_query",
  "request_id": "ui:1",
  "projection_kind": "graph",
  "source": null,
  "mode": "discovery"
}
```

Полная таблица маршрутов использует exact physical source:

```json
{
  "kind": "projection_query",
  "request_id": "ui:2",
  "projection_kind": "routes",
  "source": {"node_id":"source-node","generation":"1"},
  "mode": "machine"
}
```

`projection_kind` допускает только `graph` и `routes`. Graph требует `source: null`,
Routes — NodeKey с положительным строковым generation. `mode` обязателен для обоих
видов и допускает `machine` или `discovery`; для Graph входит в request identity.
Machine дерево требует online source и endpoints. Discovery может включать
offline endpoints, но не forwarding через них; offline source получает только self.

```json
{"kind":"projection_cancel","request_id":"ui:2"}
```

## Projection pages и typed rows

Один элемент `results`; Runtime гарантирует максимум **32 rows** на страницу:

```json
{
  "kind": "projection_page",
  "binding_epoch": "binding",
  "request_id": "ui:1",
  "projection_kind": "graph",
  "graph_version": "1",
  "graph_input_sequence": "4",
  "availability_version": "1",
  "infra_stamp": "1",
  "av_infra_stamp": "1",
  "source_node_revision": "0",
  "source": null,
  "page_index": "1",
  "rows": [{
    "kind": "node",
    "node": {"node_id":"source-node","generation":"1"},
    "address": "B",
    "anchor": {"node_id":"apc-node","generation":"1"},
    "lan_id": "area/home",
    "is_relay": false
  }],
  "complete": false,
  "code": "page"
}
```

Graph выдаёт все address-sorted `node` rows, затем physical `edge` rows. Узел без
anchor/LAN имеет явные null. Quantum relay clique не разворачивается в edge rows.
Физический edge имеет точную форму:

```json
{
  "kind": "edge",
  "from": {"node_id":"apc-node","generation":"1"},
  "to": {"node_id":"source-node","generation":"1"},
  "link_kind": "endpoint",
  "distance": 5,
  "latency_ds": 1
}
```

`link_kind` — `apc` или `endpoint`. Distance — неотрицательный Int32, latency —
положительный Int32 в децисекундах. Routes выдаёт только достижимые entries одного
source tree, без материализации полного пути каждой цели:

```json
{
  "kind": "route",
  "node": {"node_id":"source-node","generation":"1"},
  "previous": null,
  "latency_ds": 0,
  "quantum": false,
  "node_revision": "1"
}
```

У source predecessor null и latency 0. У остальных predecessor — NodeKey;
`latency_ds` — конечное неотрицательное JSON number. `quantum` показывает virtual
relay hop в любом месте predecessor пути. Page `source` у Routes содержит исходный
NodeKey и совпадает между страницами.

`graph_version`, `graph_input_sequence`, `availability_version` — canonical
zero-allowed UInt64 strings. Ноль допустим в отказе до capture. `page_index` —
положительный UInt64 string. Успешные страницы одного запроса упорядочены;
terminal отказ может пропустить номера отозванных unread страниц.

Client накапливает строки отдельно и публикует таблицу только после
`complete: true, code: "completed"`. Любой иной terminal (`cancelled`,
`projection_changed`, `source_unavailable`, `projection_capacity`,
`routing_unavailable`) отзывает накопленную таблицу; его `rows` пусты.
Pending graph паркует запрос и unread успешные страницы. Legacy Graph query
отзывается при замене publication. В Runtime Routes удерживает captured source
tree при изменениях посторонних endpoints: validity задают infrastructure stamps
и source revision. Каждая RouteRow несёт raw `node_revision` своей цели, поэтому
native consumer может сразу отвергнуть устаревшую цель. Graph содержит только
structural rows; availability version остаётся метаданными capture.

## Revision hints, budgets и replay

`runtime_result` содержит текущие `graph_version`, `graph_input_sequence`,
`availability_version`, `infra_stamp`, `av_infra_stamp` и `communication_revision`
в том же строковом counter формате. Первые два равны `"0"`
при pending graph. `graph_input_sequence` — граница structural publication,
а не sequence последней logical команды. Availability-only изменение может
оставить graph version прежней и изменить availability version. Infrastructure
stamps меняются при изменениях forwarding nodes, sector geometry и backbone,
но не при churn посторонних leaf endpoints. Native route caches используют эти
stamps, raw revisions source/target и точное поколение binding.

Transport, discovery, projection и graph synchronization делят общий Pump budget
с round-robin выдачей результатов. Projection work unit ограничен 32 строками; первый Routes slice
дополнительно выполняет один bounded source-tree поиск. Poll не двигает
simulation time или logical input sequence.

Semantic admission `would_block` подтверждает outer frame, сохраняя domain
request ID для повтора в **новом** logical frame. Повтор идентичного уже принятого
request ID возвращает Duplicate status, не генерируя заново страницы. Доставку
прошлых событий обеспечивает output journal: poll со старым output ACK возвращает
те же bytes кадра. Native ACK посылается только после обработки всех его entries.

## Point-to-APC fallback без собственного узла

Консоль без собственного node посылает одну logical команду вместо локального
обхода APC registry:

```json
{
  "kind": "anchor_query",
  "request_id": "point:1",
  "position": {"x": 42, "y": 50, "z": 1},
  "sector_id": "sector:1",
  "base_area_id": "native-area"
}
```

Все пять свойств обязательны. `base_area_id` может быть null; sector/base-area
идентификаторы ограничены 128 UTF-16 units. X/Y — целые 1..32767, Z — целое
1..4096. Положительный request serial использует общий canonical формат.

Ответ находится в `runtime_result`, без нового event producer:

```json
{
  "anchor_result": {
    "request_id": "point:1",
    "node": {"node_id":"apc-node","generation":"1"},
    "graph_version": "2",
    "graph_input_sequence": "17",
    "code": "found"
  }
}
```

Выбор использует spatial index текущей успешной публикации. APC, питающий
`base_area_id`, имеет приоритет над ближайшим APC другой area. При отсутствии
такого bucket выбирается ближайший APC того же сектора. Метрика Chebyshev
использует индекс в native ordered floors вместо числового Z; равенство разрешает
ordinal address. Offline APC допустим как structural anchor; последующий route
запрос отдельно проверяет доступность.

`node: null` сопровождается явным code: `not_found` — сектор существует, но APC нет;
`sector_unknown` — sector отсутствует; `sector_position` — Z вне его ordered floors;
`topology_pending` — текущий graph непригоден/ещё не опубликован. Последний ответ
имеет нулевые graph counters и никогда не использует предыдущую stale publication.
Это semantic reply принятого frame, а не fatal `error_code` с закрытием binding.

Point queries не меняют graph, bindings или availability и не создают query slots.
Outer sequence и replay fingerprint включают весь typed request. Повтор того же
logical frame возвращает прежний `anchor_result`; новое наблюдение после
изменения graph требует нового logical frame.

## Отказ одного excessive request payload

`request_submit` с корректной envelope и metadata, но недопустимым typed payload,
получает `runtime_result.admission.status: "rejected"` и `code: "invalid_payload"`.
Outer sequence подтверждается; соединение и остальные принятые запросы сохраняются.
Невалидное дерево не сохраняется в Core и не порождает native invocation.

Payload caps: глубина 32 (root=1), суммарно 4096 values, 1024 элемента на container,
65536 UTF-16 units текста, 128 units ключа, conservative JSON estimate 256 KiB.
Дубликаты object keys, непарные surrogate и не конечные числа также дают отказ
этого сообщения. Native peer выполняет только `json_encode` и ограничение raw
тела; глубокую проверку выполняет Host.

Все остальные request fields проверяются до payload-нормализации. Повреждённый
JSON, UTF-8, envelope/epochs, unknown/duplicate metadata и неверные counters/типы
остаются protocol errors. Body более 1 MiB отвергается до parse. Общий structural
parser допускает глубину внутри этого byte cap, а materialization typed payload
останавливается на своих строгих пределах.

Внутренняя команда Core `RejectRequest` хранит только canonical request key и
safe code; отдельного внешнего wire kind для неё нет. Replay identity такого
отказа нормализована по этим полям: разные excessive trees того же уже принятого
logical frame не выполняют эффект и не удерживаются в replay history. Исправленный
valid request требует нового outer sequence.

## Persistent graph synchronization

Штатный DM façade использует persistent stream вместо повторных whole-graph queries:

```json
{"kind":"graph_sync_start","request_id":"projection:1"}
{"kind":"graph_sync_continue","request_id":"projection:1","page_index":"1"}
{"kind":"graph_sync_cancel","request_id":"projection:1"}
```

Каждая команда занимает отдельный logical frame. `continue` подтверждает, что
native consumer применил конкретную страницу; транспортный output ACK сам по
себе этого не означает. До подтверждения Core сохраняет ровно одну inflight
страницу. Повторная доставка не применяет операции второй раз.

```json
{
  "kind":"graph_sync_page",
  "binding_epoch":"binding",
  "request_id":"projection:1",
  "page_index":"1",
  "phase":"snapshot",
  "from_graph_version":"0",
  "graph_version":"2",
  "graph_input_sequence":"17",
  "infra_stamp":"1",
  "av_infra_stamp":"1",
  "operations":[{
    "kind":"node_upsert",
    "node":{"node_id":"source-node","generation":"1"},
    "address":"B",
    "anchor":{"node_id":"apc-node","generation":"1"},
    "lan_id":"area/home",
    "is_relay":false,
    "node_revision":"3",
    "is_endpoint":true
  }],
  "batch_complete":false,
  "code":"page"
}
```

Страница содержит до 32 flat operations. Допустимы `node_upsert` (выше),
`node_remove` с `node`, `edge_upsert` с `edge_id`, `from`, `to`, `link_kind`,
`distance`, `latency_ds` и `edge_remove` с `edge_id`. Ключ ребра — стабильный
opaque hash NodeKey pair/kind; смена address не меняет identity ребра.

Первая фаза — `snapshot`, следующие — `delta`. `batch_complete` завершает один
captured target graph. Baseline сдвигается только после `continue` последней
страницы. Пока предыдущий target передаётся, новые publications объединяются
в следующий diff; движущийся leaf не перезапускает незавершённый snapshot.
Core удерживает bounded baseline, target и одну страницу, без истории graphs.
Cancel также выдаёт terminal page; `continue` этой страницы освобождает stream
slot даже после удаления native consumer.

## Proof перед native invocation

`service_invocation` включает строковые `source_node_revision`,
`destination_node_revision`, `infra_stamp`, `av_infra_stamp` и nullable
`communication_revision`. Для communications последнее поле содержит counter;
для ordinary traffic — null. Эти значения фиксируются в Runtime при handoff,
а не добавляются заново при более поздней сериализации или replay.

Native executor сверяет binding, поколения сервисов/узлов, raw node revisions,
infrastructure stamps, communication revision и ACK известных infrastructure
facts перед `matches/accepts` и повторно после этих callbacks, поскольку они
могут yield. Изменившийся мир запрещает старый effect; native permission checks
остаются в игре. Payload/context identity и bounded invocation ledger исключают
повторный native receive при retransmission. Потеря связи после возможного
эффекта даёт `outcome_unknown`, без автоматического повтора mutating request.

DM source journal объединяет только ещё не отправленные upserts одного key
в пределах causal epoch. Захват barrier для любой logical команды закрывает
эту epoch: более новые изменения не могут переписать предшествующие ей facts.
Удаления не объединяются. Один journal frame содержит до 128 записей и 128 KiB
их сериализованных тел; данные удаляются из очереди только после enqueue.
