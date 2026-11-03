# Плагины NETRUN

Библиотека расширяется **плагинами** на этапе composition (при создании `NetworkRuntime`).
Плагин — это доверенный локальный код, реализующий `INetrunPlugin`; при создании runtime он
один раз вносит свои стратегии в реестр и затем самим ядром больше не вызывается.

## Что можно расширять (текущая версия)

- **Политику стоимостей маршрутизации** (`IRouteCostPolicy`). Она влияет на обычный (Machine),
  коммуникационный (fallback) и полный (source-tree, discovery) поиск маршрутов. С её помощью можно
  задать, например, взвешивание по загруженности, предпочтение каналов или собственную модель задержек.
- **Нормализаторы typed node-фактов** (`AddNodeChangeHandler`): embedder получает свою форму данных,
  а Core сохраняет канонические snapshot/checksum/топологические правила.
- **Stateful модули** (`RegisterModule`): отдельная сессия на binding, cooperative задачи,
  типизированные Core эффекты и собственные immutable read-model проекции.

Контракт плагина и реестр спроектированы с запасом: по мере открытия новых безопасных точек
расширения в реестр будут добавляться новые методы, а существующие плагины продолжат работать.

## Границы (важно)

Ядро намеренно имеет узкую, безопасную поверхность расширения:

- Плагины **не добавляют** новые wire-команды, типы фактов или игровые эффекты. Протокол — строгий
  закрытый allowlist; ядро не выполняет игровых эффектов и не удерживает native-ссылки. Авторизацию и
  физическое исполнение по-прежнему делает DM-сторона.
- Вклад плагина **обязан быть чистым**: детерминированным, без часов, случайности и ввода-вывода,
  воспроизводимым при повторном применении (replay) одной и той же последовательности. Иначе нарушатся
  кеширование маршрутов, дедупликация и catch-up.
- Ядро — единоличный последовательный владелец. Методы плагина вызываются синхронно; реентерабельность
  и многопоточность не поддерживаются.

> Embedder может зарегистрировать чистый `INodeChangeHandler` через `AddNodeChangeHandler`.
> `NetworkRuntime` нормализует его типизированные изменения в канонические `NodeUpsert`/`NodeRemove`
> до fingerprint и `WorldSynchronization`; это работает также для snapshot chunks и catch-up.
> При подготовке `SnapshotChecksum` embedder должен сам нормализовать изменения тем же чистым
> обработчиком и вычислить checksum канонических фактов. Произвольные типы не добавляются в wire
> allowlist: JSON протокол по-прежнему передаёт только известные канонические записи.

## Контракт

```csharp
public interface INetrunPlugin
{
    PluginMetadata Metadata { get; }                 // стабильный Id, имя, версия
    void Configure(INetrunPluginRegistry registry);  // один раз при создании runtime, до входа
}

public interface INetrunPluginRegistry
{
    void SetRouteCostPolicy(IRouteCostPolicy policy); // одна политика на весь набор плагинов
}
```

- `Metadata.Id` — токен из строчных латинских букв, цифр, `.`, `-`, `_` (1..64 символа); уникален в наборе.
- Две попытки задать политику стоимостей (одним или разными плагинами) — ошибка состава.

## Пример плагина

```csharp
using Netrun.Core.Plugins;
using Netrun.Core.Routing;
using Netrun.Core.Topology;

/// <summary>Масштабирует все веса маршрутизации постоянным положительным множителем.</summary>
public sealed class WeightedRoutingPlugin(double factor) : INetrunPlugin
{
    public PluginMetadata Metadata { get; } = new("example.weighted-routing", "Weighted Routing", "1.0.0");

    public void Configure(INetrunPluginRegistry registry) =>
        registry.SetRouteCostPolicy(new ScaledRouteCostPolicy(factor));
}

/// <summary>Чистая политика: умножает объявленные веса на постоянный множитель; ревизия постоянна.</summary>
public sealed class ScaledRouteCostPolicy(double factor) : IRouteCostPolicy
{
    public ulong Revision => 1;                                            // стабильна в пределах поиска
    public double PhysicalCost(PhysicalEdge edge) => edge.LatencyDs * factor;   // конечная и строго положительная
    public double RelayCost(GraphSnapshot graph) => graph.RelayLatencyDs * factor;
}
```

## Подключение

```csharp
var options = new RuntimeOptions(Plugins: [new WeightedRoutingPlugin(1.5)]);
using var runtime = new NetworkRuntime(epochs, options);
// Дальше маршрутизация ядра использует веса, умноженные на 1.5.
```

## Правила политики стоимостей

- `PhysicalCost`/`RelayCost` возвращают **конечные строго положительные** значения; иначе поиск маршрута
  завершится безопасным кодом `invalid_route_cost`.
- `Revision` должна быть **стабильной в пределах одного поиска** и меняться только при изменении значений
  стоимостей (это корректно инвалидирует кеш маршрутов). Для неизменной политики достаточно постоянного числа.

## Диагностика

`NetworkRuntime.Plugins` возвращает метаданные подключённых плагинов в порядке объявления (пусто, если
расширений нет). Это удобно для журналирования и мониторинга активного состава.

## Ошибки состава

Проблемы обнаруживаются при создании `NetworkRuntime`, до обработки любого входа:

| Ситуация | Исключение | `Code` |
| --- | --- | --- |
| Недопустимые метаданные (Id/имя/версия) | `PluginException` | `plugin_metadata_invalid` |
| Повтор `Metadata.Id` в наборе | `PluginException` | `plugin_duplicate_id` |
| Вторая политика стоимостей | `PluginException` | `duplicate_route_cost_policy` |
| `null`-плагин или `null`-политика | `ArgumentNullException` | — |
| Исключение внутри `Configure` | пробрасывается как есть | — |

## Тесты

Рабочий пример и регрессии: [`tests/Netrun.Tests/PluginTests.cs`](../tests/Netrun.Tests/PluginTests.cs).

## Загрузка локальных DLL

`PluginLoader` принимает **явный список** сборок; поиск по каталогам, загрузка из сети и hot reload
не выполняются. Плагин — доверенный код с правами процесса Host; `AssemblyLoadContext` изолирует
зависимости и идентичность типов, но не является песочницей безопасности.

```csharp
using var loaded = PluginLoader.Load([Path.GetFullPath("plugins/WeightedRouting.dll")]);
using var runtime = new NetworkRuntime(epochs, new RuntimeOptions(Plugins: loaded.Plugins));
```

Список `loaded.Plugins` доступен только для чтения. Набор живёт дольше всех использующих его runtime.
Один экземпляр плагина может участвовать в нескольких runtime compositions: `Configure` вызывается
по одному разу для каждого runtime, поэтому у расширений не должно быть изменяемого разделяемого состояния.

У каждой явно указанной DLL свой collectible-контекст. Системные сборки и контракт `Netrun.Core`
разделяются с Host; остальные managed/native dependencies разрешаются через `AssemblyDependencyResolver`
относительно этой DLL и её `.deps.json`. Копируйте весь каталог результата сборки плагина.
Вторая локальная копия Core не подменяет контракт Host. Несовместимая версия ссылки Core отвергается;
`INetrunPlugin.ApiVersion` должна совпадать с `NetrunPluginApi.Version` (сейчас `1`).

Каждая публичная реализация `INetrunPlugin` должна быть конкретным негeneric-классом с публичным
конструктором без параметров. Загрузчик создаёт реализации в порядке полных имён типов, сохраняя
порядок путей. Абстрактные/открытые generic реализации, дубликаты пути или `Metadata.Id` — ошибка
всего набора. Ошибка не оставляет частично подключённый состав: уже созданные `IDisposable`
экземпляры освобождаются в обратном порядке, все контексты получают `Unload`.

`LoadedPluginSet.Dispose` вызывает `IDisposable.Dispose` расширений и инициирует выгрузку контекстов.
Фактическая выгрузка завершится после освобождения всех ссылок на типы/экземпляры и следующего GC.
Исключение одного плагина не прерывает освобождение остальных; вызывающий код получает только
безопасный код `plugin_dispose_failed`.

Host использует опциональную переменную `NETRUN_PLUGIN_PATHS`: пути разделяются `;` в Windows и
`:` в Linux (`Path.PathSeparator`). Пустая переменная означает отсутствие плагинов. Задавайте
абсолютные пути, чтобы рабочий каталог запуска не менял состав:

```powershell
$env:NETRUN_PLUGIN_PATHS = 'C:\netrun\plugins\WeightedRouting.dll;C:\netrun\plugins\Other.dll'
dotnet run --project src/Netrun.Host -- serve
```

Ошибки чтения/загрузки/свойств расширения имеют стабильные коды (`plugin_file_missing`,
`plugin_assembly_load_failed`, `plugin_type_load_failed`, `plugin_type_missing`, `plugin_type_invalid`,
`plugin_constructor_missing`, `plugin_constructor_failed`, `plugin_metadata_failed`,
`plugin_api_version_mismatch`, `plugin_duplicate_path`). Пути, исключения конструкторов и содержимое
расширений в эти сообщения не включаются. Ошибки метаданных/Id совпадают с composition-кодами выше.

## Быстрое создание плагина

```powershell
pwsh -File scripts/new-plugin.ps1 -Name MyRouting -PluginId my.routing -OutputDirectory C:\netrun\MyRouting
dotnet build C:\netrun\MyRouting\MyRouting.csproj -c Release
```

Скрипт создаёт `.csproj`, `INetrunPlugin` и README, добавляет только project reference на существующий
Core и отказывается перезаписывать существующий каталог. Параметр `-CoreProject` позволяет явно
указать другой checkout. В готовой сборке Core предоставляет Host, поэтому локальная копия контракта
не включается. Полноценный собираемый пример политики: [`samples/WeightedRouting`](../samples/WeightedRouting).
Загрузка отдельных DLL, изоляция зависимостей, несовместимость API и rollback проверяются
[`PluginLoaderTests.cs`](../tests/Netrun.Tests/PluginLoaderTests.cs).

## Модули с состоянием

`INetrunPlugin.Configure` регистрирует `INetrunModuleFactory` через
`registry.RegisterModule(factory)`. `factory.Id` — уникальный token (до 64 символов), для модов
используйте namespace вида `author.feature`. Создавайте новый `IModuleSession` при каждом
`factory.Create(context)`; mutable состояние между binding не разделяется.

`ModuleContext` предоставляет только immutable `ReadEntity(ActorKey)`, текущие simulation DS и
binding identity. Прямого mutable `EntityWorld`, native-ссылок или callback исполнения DM нет.
Мод возвращает известные типизированные `EntityOperation`; общий Core owner проверяет и применяет
их перед следующей задачей центральной FIFO. Пользовательские имена не интерпретируются как DM proc.

```csharp
var registry = new PluginModuleRegistry();
registry.Register(new MyModuleFactory());
using var sessions = registry.CreateSessions(new ModuleContext(binding, entities.Read, () => nowDs));
using var task = sessions.Start(new ModuleRequest(new RequestKey("producer", 1),
    "author.feature", "inspect", NetworkValue.Null));
var step = task.Advance(workBudget: 8);
// Root применяет step.Effects через Core owners в порядке списка, затем публикует step.Projections.
// Незавершённую task Root снова ставит в общую FIFO; отдельного module scheduler нет.
```

`Start` только создаёт continuation. Долгий алгоритм хранит курсор в `IModuleTask`, делает не более
переданного бюджета за `Advance` и возвращает `ModuleStep(Work, Complete, Result, Effects, Projections)`.
Незавершённый шаг при положительном бюджете обязан потратить хотя бы одну единицу; нулевой work
не может выдавать mutations/projections. Результат допустим только при `Complete`. Логика и состояние
должны воспроизводиться по тем же requests, Core facts и simulation time.

`ModuleSessionSet` не планирует задачи: он ограничивает и проверяет request/step shape, скрывает
исключения расширений стабильными кодами и владеет ресурсами. По умолчанию шаг возвращает максимум
32 entity effects и 8 projections; суммарные result/projection данные проходят **единый**
`NetworkValue` бюджет (256 KiB, 4096 значений, глубина 32). Producer deduplication, admission,
общий размер очереди и backpressure принадлежат центральному Runtime owner.

Terminal/invalid/throwing task автоматически освобождается; на shutdown активные tasks освобождаются
до module sessions. Ошибка Create откатывает уже созданные sessions. Core эффекты не исполняются
внутри мода и не применяются частично при ошибке проверки шага.

Собираемый пример per-binding модуля: [`samples/CounterModule`](../samples/CounterModule).
Регрессии: [`PluginModuleTests.cs`](../tests/Netrun.Tests/PluginModuleTests.cs).
