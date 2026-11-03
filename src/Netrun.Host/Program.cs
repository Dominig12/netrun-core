using Netrun.Core;
using Netrun.Host;
using Netrun.Core.Plugins;
using Netrun.Core.Runtime;
using System.Globalization;
using System.Runtime.InteropServices;

// Ctrl+C отменяет ожидающее чтение; игровой логический clock при этом не подменяется wall time.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// Docker SIGTERM должен закрывать listener и классифицировать незавершённые native invocation.
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; shutdown.Cancel(); });
// Диагностика создаётся только в serve; stdout offline/framed стендов остаётся их собственным протоколом.
HostLog? diagnostics = null;
try
{
    if (args is ["serve"] or ["serve", _])
    {
        diagnostics = new HostLog(Console.Error, HostLogOptions.FromEnvironment());
        var portText = args.Length == 2 ? args[1] : Environment.GetEnvironmentVariable("NETRUN_PORT") ?? "18462";
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("invalid_port");
            diagnostics.TryWrite(new(HostLogLevel.Error, "startup_failed", Code: "invalid_port"));
            return 2;
        }
        var token = Environment.GetEnvironmentVariable("NETRUN_SESSION_TOKEN");
        if (token is null && Environment.GetEnvironmentVariable("NETRUN_SESSION_TOKEN_FILE") is { } tokenFile)
        {
            if (new FileInfo(tokenFile).Length > 1024)
                throw new ArgumentException("Invalid token file.");
            token = (await File.ReadAllTextAsync(tokenFile, shutdown.Token)).Trim();
        }
        if (token is null)
        {
            Console.Error.WriteLine("session_token_missing");
            diagnostics.TryWrite(new(HostLogLevel.Error, "startup_failed", Code: "session_token_missing"));
            return 2;
        }
        // Только явные пути доверенных сборок; нет сканирования каталогов или загрузки из сети.
        var pluginPaths = Environment.GetEnvironmentVariable("NETRUN_PLUGIN_PATHS") is { } configuredPlugins
            ? configuredPlugins.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        using var plugins = PluginLoader.Load(pluginPaths);
        await using var host = new LoopbackHost(new(port, token),
            new RuntimeHostSessionFactory(new RuntimeOptions(Plugins: plugins.Plugins)), diagnostics);
        Console.Out.WriteLine($"NETRUN Host listening on loopback:{host.Port}");
        await host.RunAsync(shutdown.Token);
        return 0;
    }
    if (args is ["replay" or "replay-sync", var path])
    {
        await using var input = File.OpenRead(path);
        return args[0] == "replay-sync"
            ? await Harness.ReplaySynchronizedAsync(input, Console.Out, Console.Error, shutdown.Token)
            : await Harness.ReplayAsync(input, Console.Out, Console.Error, shutdown.Token);
    }
    if (args is ["framed" or "framed-sync", var world, var engine, var binding])
    {
        // Epochs заданы вручную только для стенда; это не реализация сетевого handshake.
        return args[0] == "framed-sync"
            ? await Harness.FramedSynchronizedAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                Console.Error, new Epochs(world, engine, binding), shutdown.Token)
            : await Harness.FramedAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                Console.Error, new Epochs(world, engine, binding), shutdown.Token);
    }
    Console.Error.WriteLine("Usage: Netrun.Host serve [PORT] | replay[-sync] FILE.jsonl | framed[-sync] WORLD_EPOCH ENGINE_EPOCH BINDING_EPOCH");
    return 2;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 130;
}
catch (IOException)
{
    Console.Error.WriteLine("io_error");
    diagnostics?.TryWrite(new(HostLogLevel.Error, "host_failed", Code: "io_error"));
    return 2;
}
catch (PluginException error)
{
    Console.Error.WriteLine(error.Code);
    diagnostics?.TryWrite(new(HostLogLevel.Error, "plugin_failed", Code: error.Code));
    return 2;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("file_access_denied");
    diagnostics?.TryWrite(new(HostLogLevel.Error, "host_failed", Code: "file_access_denied"));
    return 2;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("invalid_arguments");
    diagnostics?.TryWrite(new(HostLogLevel.Error, "host_failed", Code: "invalid_arguments"));
    return 2;
}
finally
{
    if (diagnostics is not null) await diagnostics.DisposeAsync();
}
