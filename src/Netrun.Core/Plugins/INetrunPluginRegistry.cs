using Netrun.Core.Routing;

namespace Netrun.Core.Plugins;

/// <summary>
/// Поверхность регистрации вкладов плагина во время composition. Методы вызываются только из
/// <see cref="INetrunPlugin.Configure"/> и только синхронно; реестр нельзя сохранять и использовать позднее.
/// Каждый зарегистрированный объект обязан быть чистым и стабильным по чтению: ядро — единоличный
/// последовательный владелец, а результаты должны воспроизводиться при replay одной и той же последовательности.
/// </summary>
public interface INetrunPluginRegistry
{
    /// <summary>
    /// Устанавливает единственную для всего набора плагинов политику стоимостей маршрутизации.
    /// Применяется к обычному (Machine), коммуникационному и полному (source-tree) поиску маршрутов.
    /// Повторная установка — тем же или другим плагином — является ошибкой состава.
    /// </summary>
    /// <param name="policy">Чистая ревизуемая политика положительных конечных стоимостей (см. <see cref="IRouteCostPolicy"/>).</param>
    void SetRouteCostPolicy(IRouteCostPolicy policy);

    /// <summary>Регистрирует чистый нормализатор собственного typed node-факта; штатные записи заменять нельзя.</summary>
    void AddNodeChangeHandler(INodeChangeHandler handler);

    /// <summary>Регистрирует stateful модуль; отдельный экземпляр его состояния создаётся для каждого binding.</summary>
    void RegisterModule(INetrunModuleFactory factory);
}
