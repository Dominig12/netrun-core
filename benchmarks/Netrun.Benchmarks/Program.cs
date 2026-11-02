using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Protocol;

namespace Netrun.Benchmarks;

/// <summary>Standalone C# baseline без NuGet benchmark framework, native IPC, BYOND или игровых эффектов.</summary>
internal static class Program
{
    /// <summary>Стабильный seed собственного xorshift32 layout generator.</summary>
    private const uint Seed = 0x4e455452;
    /// <summary>Каждая метрика получает два полных прогрева и три измеряемых запуска.</summary>
    private const int Warmups = 2, MeasuredRuns = 3;
    /// <summary>Наблюдаемый checksum не позволяет отбросить результаты измеряемого кода.</summary>
    private static long _sink;

    /// <summary>Пишет воспроизводимый JSON report в stdout и optional --output PATH; никакие production настройки не меняются.</summary>
    private static void Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
            throw new ArgumentException("Usage: Netrun.Benchmarks [--output PATH]");
        var cases = new List<object>();
        foreach (var size in new[] { 1000, 5000, 10000 }) cases.Add(Run(size));
        var report = new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            LayoutSeed = Seed,
            WarmupRuns = Warmups,
            MeasuredRuns,
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                LogicalProcessors = System.Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                GCLatencyMode = GCSettings.LatencyMode.ToString(),
                Stopwatch.Frequency,
                AllocationCounter = "GC.GetAllocatedBytesForCurrentThread; synchronous measured operations only"
            },
            Cases = cases,
            ResultChecksum = _sink
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        if (args.Length == 2)
        {
            var path = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json + System.Environment.NewLine);
        }
        Console.WriteLine(json);
    }

    /// <summary>Подготавливает fixture вне измерений; каждый metric описывает отдельную фазу, а не сумму полного серверного tick.</summary>
    private static object Run(int size)
    {
        var fixture = new Fixture(size, Seed);
        Console.Error.WriteLine($"Preparing {size} nodes / {fixture.ApcCount} APC / {fixture.RelayCount} real relays");
        var topology = Measure(size, "topology_build", () =>
        {
            var built = new NetworkTopology().Build(fixture.Nodes, fixture.Sectors, 1);
            return built.Nodes.Count + built.PhysicalEdges.Count;
        });
        var graph = fixture.Build();
        var router = new NetworkRouter(options: new(CacheCapacity: 1024));
        var destinations = Enumerable.Range(0, 1000).Select(index => fixture.Keys[(index * 7919 + 37) % size]).ToArray();
        foreach (var destination in destinations) RequireFound(router.FindRoute(graph, fixture.Source, destination));
        var cachedPairs = Measure(size, "cached_pair_routes_1000", () =>
        {
            var misses = router.CacheMisses;
            foreach (var destination in destinations) RequireFound(router.FindRoute(graph, fixture.Source, destination));
            if (router.CacheMisses != misses) throw new InvalidOperationException("Expected 1000 cache hits.");
            return destinations.Length;
        });
        var trees = new SourceRouteTreeProvider(options: new(CacheCapacity: 0));
        var sourceTree = Measure(size, "uncached_source_tree", () =>
        {
            var tree = trees.Build(graph, fixture.Source);
            if (tree.Status != RouteStatus.Found || tree.Entries.Count != size)
                throw new InvalidOperationException("Incomplete source tree.");
            return tree.Entries.Count;
        });
        using var discovery = new DiscoveryScenario(fixture, graph);
        var ordinaryDiscovery = Measure(size, "ordinary_discovery_1000_candidates", discovery.Run, discovery.Prepare);
        var snapshotOperations = 0;
        var snapshotPages = 0;
        var graphSnapshot = Measure(size, "graph_sync_snapshot_ack", () =>
        {
            var authority = new GraphAuthorityTracker(fixture);
            authority.Reset();
            using var broker = new GraphSynchronizationBroker(() => graph, authority, "benchmark-binding");
            broker.Start(new("graph", 1));
            (snapshotOperations, snapshotPages) = Transfer(broker);
            if (snapshotOperations != graph.Nodes.Count + graph.PhysicalEdges.Count)
                throw new InvalidOperationException("Incomplete graph snapshot.");
            return snapshotOperations;
        });
        using var delta = new DeltaScenario(fixture, graph);
        var deltaOperations = 0;
        var deltaPages = 0;
        var graphDelta = Measure(size, "graph_sync_single_leaf_delta_ack", () =>
        {
            (deltaOperations, deltaPages) = delta.Transfer();
            if (deltaOperations != 2 || deltaPages != 1)
                throw new InvalidOperationException("Expected one node and one edge update.");
            return deltaOperations;
        }, delta.Prepare);
        using var lifecycle = new RuntimeScenario(fixture);
        var runtimeCodec = Measure(size, "runtime_codec_128_lifecycles", lifecycle.Run);
        return new
        {
            Nodes = size,
            fixture.ApcCount,
            fixture.RelayCount,
            PhysicalEdges = graph.PhysicalEdges.Count,
            SnapshotOperations = snapshotOperations,
            SnapshotPages = snapshotPages,
            SingleLeafDeltaOperations = deltaOperations,
            SingleLeafDeltaPages = deltaPages,
            CachedPairQueries = 1000,
            DiscoveryCandidates = 1000,
            DiscoverySourceSolves = discovery.SourceSolves,
            DiscoveryTreeCacheHits = discovery.TreeCacheHits,
            DiscoveryPages = discovery.Pages,
            RuntimeLifecycles = 128,
            Measurements = new Dictionary<string, Metric>
            {
                ["topology_build"] = topology,
                ["cached_pair_routes_1000"] = cachedPairs,
                ["uncached_source_tree"] = sourceTree,
                ["ordinary_discovery_1000_candidates"] = ordinaryDiscovery,
                ["graph_sync_snapshot_ack"] = graphSnapshot,
                ["graph_sync_single_leaf_delta_ack"] = graphDelta,
                ["runtime_codec_128_lifecycles"] = runtimeCodec
            }
        };
    }

    /// <summary>Измеряет только operation; optional prepare выполняется вне таймера/alloc counter, включая delta topology rebuild.</summary>
    private static Metric Measure(int size, string name, Func<long> operation, Action? prepare = null)
    {
        Console.Error.WriteLine($"Measuring {size}: {name}");
        for (var i = 0; i < Warmups; i++) { prepare?.Invoke(); _sink ^= operation(); }
        var samples = new List<Sample>();
        for (var i = 0; i < MeasuredRuns; i++)
        {
            prepare?.Invoke();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = operation();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            _sink ^= checksum;
            samples.Add(new(elapsed, bytes));
        }
        return new(samples.Select(sample => sample.Milliseconds).Order().ElementAt(MeasuredRuns / 2),
            samples.Select(sample => sample.AllocatedBytes).Order().ElementAt(MeasuredRuns / 2), samples);
    }

    /// <summary>Application ACK loop не включает native work: проверяет один exact page и немедленно вызывает Continue.</summary>
    private static (int Operations, int Pages) Transfer(GraphSynchronizationBroker broker)
    {
        var operations = 0;
        var pages = 0;
        for (var guard = 0; guard < 4000; guard++)
        {
            if (broker.Advance(1) != 1 || !broker.TryDequeueEvent(out var page) || page is null || page.Code is not ("page" or "completed"))
                throw new InvalidOperationException("Graph synchronization did not produce a page.");
            operations += page.Operations.Count;
            pages++;
            if (!broker.Continue(new("graph", 1), page.PageIndex).Accepted)
                throw new InvalidOperationException("Graph synchronization ACK failed.");
            if (page.BatchComplete) return (operations, pages);
        }
        throw new InvalidOperationException("Graph synchronization exceeded bounded pages.");
    }

    /// <summary>Ненайденный путь делает fixture некорректным, а не быстрым успешным результатом.</summary>
    private static void RequireFound(RouteResult route)
    {
        if (route.Status != RouteStatus.Found) throw new InvalidOperationException("Unexpected unreachable fixture route.");
    }

    /// <summary>Один измеренный elapsed/managed-allocation sample.</summary>
    /// <param name="Milliseconds">Stopwatch wall time только синхронного operation.</param>
    /// <param name="AllocatedBytes">Новые managed bytes текущего потока; не retained memory и не native allocations.</param>
    private sealed record Sample(double Milliseconds, long AllocatedBytes);
    /// <summary>Отдельные medians трёх samples; они не обязательно взяты из одной и той же строки.</summary>
    /// <param name="MedianMilliseconds">Медиана elapsed samples.</param>
    /// <param name="MedianAllocatedBytes">Медиана allocation samples.</param>
    /// <param name="Samples">Все raw samples для проверки разброса.</param>
    private sealed record Metric(double MedianMilliseconds, long MedianAllocatedBytes, IReadOnlyList<Sample> Samples);

    /// <summary>Детерминированная synthetic станция с одним ordered sector и native area на каждый APC.</summary>
    private sealed class Fixture : INetworkWorldView
    {
        /// <summary>Полные node facts; внутри benchmark runtime они публикуются отдельным snapshot.</summary>
        internal Dictionary<string, NodeFact> MutableNodes { get; } = new(StringComparer.Ordinal);
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => MutableNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>(StringComparer.Ordinal)
        {
            ["station"] = new("station", 1, 1, new([5, 1, 9]))
        };
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services { get; }
        /// <summary>Stable generation keys в fixture insertion order.</summary>
        internal NodeKey[] Keys { get; }
        /// <summary>Точный исходный APC для query/lifecycle scenarios.</summary>
        internal NodeKey Source => Keys[0];
        /// <summary>Первый endpoint находится у того же APC, обеспечивая нулевой scaled machine delay.</summary>
        internal NodeKey LocalEndpoint => Keys[ApcCount];
        /// <summary>APC count растёт до 64, не имитируя quadratic APC clique.</summary>
        internal int ApcCount { get; }
        /// <summary>Настоящие relay flags включают implicit quantum bucket.</summary>
        internal int RelayCount { get; }

        /// <summary>Один xorshift32 stream задаёт placement; текстовые IDs и layout формулы не зависят от randomized GetHashCode.</summary>
        internal Fixture(int size, uint seed)
        {
            ApcCount = Math.Min(64, Math.Max(8, size / 100));
            RelayCount = Math.Min(32, Math.Max(2, size / 500));
            var random = seed;
            var levels = new[] { 5, 1, 9 };
            for (var i = 0; i < size; i++)
            {
                var apc = i < ApcCount ? i : (i - ApcCount) % ApcCount;
                var area = "area-" + apc.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                var x = 100 + (apc % 8) * 96;
                var y = 100 + (apc / 8) * 96;
                if (i >= ApcCount) { x += (int)(Next(ref random) % 25) - 12; y += (int)(Next(ref random) % 25) - 12; }
                var id = "node-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
                MutableNodes.Add(id, new(id, 1, 1, new(id, i < ApcCount ? "apc" : "server",
                    new(x, y, levels[apc % 3]), "station", true, new(area, area,
                        IsNtnetRelay: i >= ApcCount && i < ApcCount + RelayCount))));
            }
            Keys = MutableNodes.Values.Select(node => new NodeKey(node.NodeId, node.Generation)).ToArray();
            var operations = new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)], 64);
            Services = new Dictionary<string, ServiceFact>(StringComparer.Ordinal)
            {
                ["source"] = new("source", 1, 1, Source, "benchmark.source", null, true, operations),
                ["target"] = new("target", 1, 1, LocalEndpoint, "benchmark.target", null, true, operations)
            };
        }

        /// <summary>Новая complete публикация строится production topology owner.</summary>
        internal GraphSnapshot Build() => new NetworkTopology().Build(Nodes, Sectors, 1);
        /// <summary>Xorshift32 фиксирован явно, чтобы framework version не меняла Random sequence.</summary>
        private static uint Next(ref uint state) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
    }

    /// <summary>Persistent baseline подготовлен один раз; measured delta исключает preceding topology rebuild.</summary>
    private sealed class DeltaScenario : IDisposable
    {
        /// <summary>Собственные facts изолируют перемещение endpoint от остальных benchmark metrics.</summary>
        private readonly DeltaWorld _world;
        /// <summary>Builder сохраняет stable bindings между измерениями.</summary>
        private readonly NetworkTopology _topology = new();
        /// <summary>Тот же authority tracker получает O(1) changed node hook.</summary>
        private readonly GraphAuthorityTracker _authority;
        /// <summary>Реальный single-inflight broker.</summary>
        private readonly GraphSynchronizationBroker _broker;
        /// <summary>Current target getter обновляется вне timed transfer.</summary>
        private GraphSnapshot _graph;
        /// <summary>Один ordinary non-relay leaf, расположенный у APC0.</summary>
        private readonly string _moving;
        /// <summary>Input boundary и чередование позиции не совпадают с measured page serial.</summary>
        private ulong _sequence = 1;

        /// <summary>Snapshot+ACK setup excluded из subsequent leaf-delta измерений.</summary>
        internal DeltaScenario(Fixture fixture, GraphSnapshot graph)
        {
            _world = new(fixture);
            _moving = fixture.Keys[2 * fixture.ApcCount].NodeId;
            _graph = _topology.Build(_world.Nodes, _world.Sectors, _sequence);
            _authority = new(_world);
            _authority.Reset();
            _broker = new(() => _graph, _authority, "delta-binding");
            _broker.Start(new("graph", 1));
            Program.Transfer(_broker);
            if (graph.Nodes.Count != _graph.Nodes.Count) throw new InvalidOperationException("Delta fixture mismatch.");
        }

        /// <summary>Меняет один cable distance и revision; пересборка graph намеренно вне delta-transfer таймера.</summary>
        internal void Prepare()
        {
            var before = _world.MutableNodes[_moving];
            var after = before with
            {
                Revision = before.Revision + 1,
                State = before.State with { Position = before.State.Position! with { X = _sequence % 2 == 0 ? 140 : 180 } }
            };
            _world.MutableNodes[_moving] = after;
            _authority.ObserveNodeChange(before, after);
            _graph = _topology.Build(_world.Nodes, _world.Sectors, ++_sequence);
        }
        /// <summary>Измеряемая часть: сравнение frozen target, two-op page, dequeue и native-style application ACK.</summary>
        internal (int Operations, int Pages) Transfer() => Program.Transfer(_broker);
        /// <inheritdoc />
        public void Dispose() => _broker.Dispose();
    }

    /// <summary>Mutable clone нужен только одному changed node, без изменения общего fixture.</summary>
    private sealed class DeltaWorld(Fixture fixture) : INetworkWorldView
    {
        /// <summary>Собственная current fact registry.</summary>
        internal Dictionary<string, NodeFact> MutableNodes { get; } = new(fixture.Nodes, StringComparer.Ordinal);
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => MutableNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors => fixture.Sectors;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services => fixture.Services;
    }

    /// <summary>Реальный ordinary discovery читает один cold source tree для 1000 service identities без pair path allocations.</summary>
    private sealed class DiscoveryScenario : INetworkWorldView, IDisposable
    {
        /// <summary>Готовые facts станционного layout остаются вне measured setup.</summary>
        private readonly Fixture _fixture;
        /// <summary>Готовая публикация графа не пересобирается между query samples.</summary>
        private readonly GraphSnapshot _graph;
        /// <summary>Family/tag индекс создаётся один раз вне измерения.</summary>
        private readonly ServiceDirectory _directory = new();
        /// <summary>Каждый sample начинает с нового пустого bounded tree cache.</summary>
        private SourceRouteTreeProvider? _trees;
        /// <summary>Самостоятельный owner не сохраняет query history между samples.</summary>
        private ServiceDiscovery? _discovery;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => _fixture.Nodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors => _fixture.Sectors;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services { get; }
        /// <summary>Фактическое число source solves последнего sample, а не оценка из elapsed time.</summary>
        internal ulong SourceSolves => _trees!.CacheMisses;
        /// <summary>Фактическое число O(1) cache reads после единственного solve.</summary>
        internal ulong TreeCacheHits => _trees!.CacheHits;
        /// <summary>Число прочитанных страниц по 64 keys, включая последнюю terminal страницу.</summary>
        internal int Pages { get; private set; }

        /// <summary>Распределяет 1000 новых authority services по уже существующим online узлам fixture.</summary>
        internal DiscoveryScenario(Fixture fixture, GraphSnapshot graph)
        {
            _fixture = fixture;
            _graph = graph;
            var operations = new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)], 64);
            var services = new Dictionary<string, ServiceFact>(StringComparer.Ordinal)
            {
                ["discovery-source"] = new("discovery-source", 1, 1, fixture.Source, "benchmark.source", null, true, operations)
            };
            for (var i = 0; i < 1000; i++)
            {
                var id = "authority-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                services.Add(id, new(id, 1, 1, fixture.Keys[(i * 7919 + 37) % fixture.Keys.Length],
                    "benchmark.authority", null, true, operations));
            }
            Services = services;
            foreach (var service in services.Values)
                if (_directory.Upsert(service) != DirectoryUpdateResult.Applied)
                    throw new InvalidOperationException("Invalid discovery fixture service.");
        }

        /// <summary>Сбрасывает кеш и owner до таймера; первый candidate внутри Run обязан выполнить полный source solve.</summary>
        internal void Prepare()
        {
            _discovery?.Dispose();
            _trees = new();
            _discovery = new(this, _directory, () => _graph, new SourceTreeRouteProvider(_trees), "discovery-binding");
            Pages = 0;
        }

        /// <summary>Измеряет Submit/capture, 1000 bounded candidate checks, один cold solve и dequeue всех страниц.</summary>
        internal long Run()
        {
            if (_discovery!.Submit(new(new("discovery", 1), new("discovery-source", 1), "benchmark.authority")).Status != AdmissionStatus.Accepted)
                throw new InvalidOperationException("Discovery query rejected.");
            var candidates = 0;
            var work = 0;
            for (var guard = 0; guard < 1000; guard++)
            {
                work += _discovery.Advance(128);
                while (_discovery.TryDequeueEvent(out var page))
                {
                    candidates += page!.Candidates.Count;
                    Pages++;
                    if (!page.Complete) continue;
                    if (page.Code != "completed" || candidates != 1000 || work != 1000 ||
                        SourceSolves != 1 || TreeCacheHits != 999 || Pages != 16 || _discovery.ActiveCount != 0)
                        throw new InvalidOperationException("Discovery did not use one complete source tree.");
                    return candidates;
                }
            }
            throw new InvalidOperationException("Discovery exceeded bounded candidate work.");
        }

        /// <inheritdoc />
        public void Dispose() => _discovery?.Dispose();
    }

    /// <summary>Codec+Runtime lifecycle не выполняет DM receive: native success имитируется exact InvocationResult.</summary>
    private sealed class RuntimeScenario : IDisposable
    {
        /// <summary>Изолированный benchmark binding.</summary>
        private static readonly Epochs Epochs = new("benchmark-world", "benchmark-engine", "benchmark-binding");
        /// <summary>Полностью published runtime; initial snapshot excluded из timed operation.</summary>
        private readonly NetworkRuntime _runtime = new(Epochs);
        /// <summary>Bounded immutable payload одинаков для всех сообщений.</summary>
        private readonly NetworkValue _payload = NetworkValue.FromObject(new Dictionary<string, NetworkValue>
        {
            ["value"] = NetworkValue.FromNumber(1),
            ["text"] = NetworkValue.FromText("benchmark")
        });
        /// <summary>Outer sequence отдельно от producer message serial.</summary>
        private ulong _sequence;
        /// <summary>Domain serial не переиспользуется между warmup/measured runs.</summary>
        private ulong _request;

        /// <summary>Публикует полный world в bounded chunks с checksum v2, передавая каждый frame через codec.</summary>
        internal RuntimeScenario(Fixture fixture)
        {
            var facts = fixture.Sectors.Values.Select(sector => (WorldChange)new SectorUpsert(sector))
                .Concat(fixture.Nodes.Values.Select(node => new NodeUpsert(node)))
                .Concat(fixture.Services.Values.Select(service => new ServiceUpsert(service))).ToArray();
            var chunks = facts.Chunk(128).Select((changes, index) => new SnapshotChunk("snapshot", (ulong)index + 1, changes)).ToArray();
            Apply(new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)]));
            foreach (var chunk in chunks) Apply(new ApplyWorld([chunk]));
            Apply(new ApplyWorld([new SnapshotCommit("snapshot", (ulong)chunks.Length, (ulong)facts.Length, SnapshotChecksum.Compute(chunks, 2))]));
            Apply(new ApplyWorld([new CatchUpComplete("snapshot", 0)]));
        }

        /// <summary>128 serial Submit→Pump→Invocation→Result→Completion с encode/decode обоих logical inputs.</summary>
        internal long Run()
        {
            for (var i = 0; i < 128; i++)
            {
                var admission = Apply(new SubmitRequest(new(new("benchmark", ++_request), new("source", 1), new("target", 1), "read", _payload)));
                if (admission.Admission?.Status != AdmissionStatus.Accepted) throw new InvalidOperationException("Runtime request rejected.");
                _runtime.Pump(1);
                if (!_runtime.TryDequeueEvent(out var next) || next is not ServiceInvocation invocation)
                    throw new InvalidOperationException("Missing invocation.");
                Apply(new FinishInvocation(new(Epochs.Binding, invocation.InvocationId, true)));
                _runtime.Pump(1);
                if (!_runtime.TryDequeueEvent(out var terminal) || terminal is not RequestCompleted { State: RequestState.Completed })
                    throw new InvalidOperationException("Missing completion.");
            }
            if (_runtime.EventCount != 0 || _runtime.ActiveRequestCount != 0) throw new InvalidOperationException("Runtime did not drain.");
            return 128;
        }

        /// <summary>Один serialized input и typed outcome без Host/TCP framing или JSON output serialization.</summary>
        private RuntimeResult Apply(RuntimeCommand command)
        {
            var input = new RuntimeInput(Epochs, ++_sequence, 0, command);
            var decoded = (RuntimePacket)RuntimeJson.Decode(RuntimeJson.Encode(input));
            var result = _runtime.Apply(decoded.Input);
            if (result.Apply.Status != ApplyStatus.Applied) throw new InvalidOperationException("Runtime input rejected.");
            return result;
        }
        /// <inheritdoc />
        public void Dispose() => _runtime.Dispose();
    }
}
