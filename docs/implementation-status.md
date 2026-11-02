# Состояние реализации

Дата: 2026-11-02. Репозиторий: `Dominig12/netrun-core`.

Реализованы независимые C# модули фактов, атомарной синхронизации, пространственной
топологии, routing, source trees, communications, transport, discovery и paged
projections. Runtime объединяет их под одним последовательным владельцем и
ограничивает общий объём работы и выходных событий. Host реализует loopback
handshake, epochs и bounded output replay с cumulative ACK.

Native мост собирается для Windows x86/x64 и Linux x86. Host и bridge получают
один immutable production source context с manifest и контрольными суммами.
Windows ABI suite включает настоящий BYOND x86 и завершение активного worker;
результаты текущих station/Docker запусков приведены в [verification.md](verification.md).

В checkout станции добавлены экспортёр фактов, native effect executor,
асинхронный discovery, dispatcher read-only запросов, incremental topology/path
façade и автоматические build/launch команды. Штатный режим
использует C# для всей сетевой топологии, binding, routing и transport;
автоматического возврата к вычислениям DM при отказе Host нет. Режимы `dm` и
`shadow` отклоняются. Старые DM prebuild, APC spatial index, route solver/cache
и machine packet engine удалены вместе с их неиспользуемыми API и константами.

DM scenes импортируют заданные attachment/path rows и подают IPC events через
production codecs. Они проверяют native права, lifecycle, UI, очереди и эффекты
без второго сетевого engine. Dedicated live runner использует настоящий Host
и station subsystem: discovery, native delivery один раз, GraphSync/source tree,
movement recovery, отдельный idempotent контракт `state` и нейролинк/резидент.
Карта импланта использует общий async waiter и wire session. Импортированные
маршруты проверяются по точным identity/revision/stamp, без потери authority
из-за вычитания округлённых DM float costs. Core coalescing
сохраняет native ownership последнего payload; обычные mutating сообщения
остаются независимыми. Машинные SEND/RECEIVE journals и bounded ROOT payload
capture сохраняются без локального расчёта маршрута или задержки.

В [integration/dm](../integration/dm/README.md) сохранены 30 новых adapter/test/tool
файлов и патч 82 существующих файлов станции, включая удалённые builders,
миграцию тестов, build/run и shutdown. Manifest содержит
базовый commit станции и SHA-256; hook patch проверен через `git apply --cached
--check`. Windows default Build/Run собирает согласованный Core пакет и управляет
парой процессов. Linux Docker launcher собирает согласованные station/Host images
и общую loopback namespace. Отсутствующий публичный Core, SDK и Zig получаются
автоматически по закреплённым версиям и hash, без пользовательских Codex paths.
BYOND archive также проверяется SHA-256; runtime image содержит совместимые
x86 библиотеки и Node 24 для игрового crypto helper.

Production нагрузка с игроками остаётся отдельной проверкой. Synthetic C# baseline
сохранён отдельно и не считается end-to-end speedup. Production сервер эта работа
не изменяет. Удалённые проверки конкретной опубликованной ревизии доступны
в [GitHub Actions](https://github.com/Dominig12/netrun-core/actions);
локальные результаты не подменяют статус этих проверок.

DM служит источником игровых контрактов. Внутренние C# алгоритмы не обязаны
повторять BYOND workaround. Изменения поведения задаются отдельными policies
и проверяются тестами; native authorization остаётся в игре.
