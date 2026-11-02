# DM integration bundle

`files/` содержит новые файлы адаптера, fixtures, тесты и build/deploy tools;
`station-hooks.patch` содержит изменения существующей станции, включая удаление
перенесённых DM builders и миграцию их потребителей. Базовый commit
и SHA-256 каждого артефакта записываются в `manifest.json`.
Текст новых файлов сохраняется в UTF-8/LF, поэтому контрольные суммы не меняются
при checkout на Linux или Windows с `.gitattributes` этого репозитория.

Bundle обновляется из checkout станции командой:

```powershell
./scripts/export-dm-integration.ps1 -SourceRoot ../MOLOT-BlueMoon-Station
```

Повторный export сохраняет `base_revision` существующего manifest, включая после
коммитов станции. Для намеренной смены baseline передайте `-BaseRevision` с полным
40-символьным commit SHA. Без существующего manifest первый export использует HEAD.

`-SourceRevision <полный SHA>` фиксирует hook diff по выбранному commit и записывает
`source_revision` в manifest. Новые adapter files в checkout обязаны совпадать с
его Git blobs после UTF-8/LF нормализации; смешивание ревизий отклоняется.
Без параметра экспортируется working tree. Binary additions передаются через
`station-hooks.patch` с `git diff --binary`; `files/` остаётся текстовым UTF-8/LF.

Перед применением к другому checkout проверьте совместимость базовой ревизии и
выполните `git apply --check /absolute/path/station-hooks.patch` из его корня.
Просмотрите patch, затем примените его и скопируйте новые файлы из `files/`,
сохраняя относительные пути. Существующие файлы адаптера с локальными изменениями
нельзя заменять вслепую. Нативные DLL/SO, сборки .NET и секреты в bundle не входят.

Сборочные и launch скрипты станции используют отдельный checkout `netrun-core`.
Если его нет, они получают публичную ревизию из `tools/netrun/core-source.json`
в ignored каталог станции. SDK и Zig также загружаются автоматически с проверкой
контрольных сумм. Секрет создаётся отдельно от образов и исходников.
Копия адаптера хранится здесь для воспроизводимости и ревью полного контракта
двух репозиториев. Игровые обработчики и исходные NETRUN системы остаются в
репозитории станции.

DM сохраняет native facts, права, read-model import и финальные эффекты машин.
Топология, binding, routing, discovery и transport вычисляются только в C#;
режимы `dm`/`shadow` отклоняются. Изолированные DM tests импортируют явно заданные
таблицы путей и результаты IPC через production codecs. Реальную связку проверяет
`node tools/netrun/run-live-core-test.mjs`, развёртывание — paired Docker lifecycle.
