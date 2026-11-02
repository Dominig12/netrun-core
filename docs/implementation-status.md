# Состояние реализации

Дата: 2026-11-02. Репозиторий: `Dominig12/netrun-core`.

Реализованы независимые C# модули фактов, атомарной синхронизации, пространственной
топологии, routing, source trees, communications, transport, discovery и paged
projections. Runtime объединяет их под одним последовательным владельцем и
ограничивает общий объём работы и выходных событий. Host реализует loopback
handshake, epochs и bounded output replay с cumulative ACK.

Native мост собран для Windows x86/x64 и Linux x86. Реальный Windows BYOND x86
проверен на цепочке snapshot → catch-up → admission → native receive → terminal
результат. Сохранён воспроизводимый runner `tests/native/test_external_dm.py`.
Локальная Linux runtime-проверка и Docker build пока не выполнены: Docker CLI
в этом окружении отсутствует.

В checkout станции добавлены экспортёр фактов, native effect executor,
асинхронный discovery, dispatcher read-only запросов, incremental topology/path
façade и автоматические build/launch команды. Штатный режим
использует C# для всей сетевой топологии, binding, routing и transport;
автоматического возврата к вычислениям DM при отказе Host нет. Явный режим `dm`
сохраняется для диагностических сравнений и старых тестов.

Полный station code реально запущен на Windows с опубликованными Host/DLL на
hermetic карте. Проверены discovery, доставка и native callback один раз,
GraphSync, source tree, отзыв старого маршрута при movement и восстановление.
Счётчики DM topology/APC/tree builders не увеличились. После исправления
native module lifetime штатный shutdown завершается с кодом 0; runtime errors нет.
Прогон: `core-live-3a41eaa8e954`, Core digest
`740f29188d6b71606094cc1b18190c1c3ac832d87c34b7b27b810c2f9ceed70e`.

В [integration/dm](../integration/dm/README.md) сохранены 21 новый adapter/test/tool
файл и патч игровых hooks, включая build/run и shutdown. Manifest содержит
базовый commit станции и SHA-256; hook patch проверен через `git apply --cached
--check`. Windows default Build/Run собирает согласованный Core пакет и управляет
парой процессов. Linux Docker launcher собирает согласованные station/Host images
и общую loopback namespace; его конфигурация и scripts подготовлены.

Остаются реальная Linux/Docker проверка и production нагрузка: Docker Engine/WSL
локально отсутствуют, доступного тестового Linux-хоста нет. Synthetic C# baseline
сохранён отдельно и не считается end-to-end speedup. Production сервер эта работа
не изменяет. Удалённые проверки конкретной опубликованной ревизии доступны
в [GitHub Actions](https://github.com/Dominig12/netrun-core/actions);
локальные результаты не подменяют статус этих проверок.

DM служит источником игровых контрактов. Внутренние C# алгоритмы не обязаны
повторять BYOND workaround. Изменения поведения задаются отдельными policies
и проверяются тестами; native authorization остаётся в игре.
