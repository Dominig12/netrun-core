using Netrun.Core;
using Netrun.Host;

// Ctrl+C отменяет ожидающее чтение; игровой логический clock при этом не подменяется wall time.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
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
    Console.Error.WriteLine("Usage: Netrun.Host replay[-sync] FILE.jsonl | framed[-sync] WORLD_EPOCH ENGINE_EPOCH BINDING_EPOCH");
    return 2;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 130;
}
catch (IOException)
{
    Console.Error.WriteLine("io_error");
    return 2;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("file_access_denied");
    return 2;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("invalid_arguments");
    return 2;
}
