using Netrun.Core.Entities;
using Netrun.Core.Transport;
using Netrun.Core.Access;
using Netrun.Core.Plugins;

namespace Netrun.Core.Runtime;

/// <summary>Ставит поиск разрешённого пути сущности в отдельную bounded очередь ядра.</summary>
public sealed record QueryEntityNavigation(EntityNavigationRequest Request) : RuntimeCommand;

/// <summary>Отзывает незавершённый поиск и непрочитанные страницы без native действий.</summary>
public sealed record CancelEntityNavigation(RequestKey Key) : RuntimeCommand;

/// <summary>Ставит actor intent в общую FIFO очередь. Raw access facts вычисляет и проверяет само ядро.</summary>
public sealed record SubmitEntityOperation(EntityOperation Operation, EntityAccessSnapshot? Access = null) : RuntimeCommand;

/// <summary>Подтверждение native эффекта поступает как новая FIFO задача; оно не блокирует предыдущую задачу.</summary>
public sealed record FinishEntityEffect(RequestKey Key, EntityEffectResult Result) : RuntimeCommand;

/// <summary>Адресует stateful мод и ставит его cooperative task в ту же общую FIFO очередь ядра.</summary>
public sealed record SubmitModuleRequest(ModuleRequest Request) : RuntimeCommand;
