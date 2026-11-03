using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>FIFO ordering, budget/backpressure и защита от повторного выполнения task.</summary>
public sealed class CoreTaskQueueTests
{
    private sealed class Work(string name, int remaining = 1)
    {
        internal readonly string Name = name;
        internal int Remaining = remaining;
    }
    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void EnqueueDoesNotExecuteAndLongHeadCannotBeOvertaken()
    {
        var order = new List<string>();
        using var queue = new CoreTaskQueue<Work>("binding", (work, _) =>
        { order.Add(work.Name); return new(1, --work.Remaining == 0); });
        queue.Enqueue(new("a", 1), new("first", 3), Hash("first"));
        queue.Enqueue(new("b", 1), new("second"), Hash("second"));
        Assert.Empty(order);
        Assert.Equal(2, queue.Advance(2));
        Assert.Equal(new[] { "first", "first" }, order);
        Assert.Equal(0UL, queue.CompletedSequence);
        queue.Advance(2);
        Assert.Equal(new[] { "first", "first", "first", "second" }, order);
        Assert.Equal(2UL, queue.CompletedSequence);
        queue.TryDequeueEvent(out var first);
        queue.TryDequeueEvent(out var second);
        Assert.Equal(1UL, first!.TaskSequence);
        Assert.Equal(2UL, second!.TaskSequence);
    }

    [Fact]
    public void ReplayAndConflictNeverExecuteSameAcceptedTaskTwice()
    {
        var calls = 0;
        using var queue = new CoreTaskQueue<Work>("binding", (_, _) => { calls++; return new(1, true); });
        var key = new RequestKey("a", 1);
        Assert.Equal(AdmissionStatus.Accepted, queue.Enqueue(key, new("first"), Hash("first")).Status);
        Assert.Equal(AdmissionStatus.Duplicate, queue.Enqueue(key, new("first"), Hash("first")).Status);
        Assert.Equal("request_conflict", queue.Enqueue(key, new("other"), Hash("other")).Code);
        queue.Advance(10);
        queue.TryDequeueEvent(out _);
        Assert.Equal(AdmissionStatus.Duplicate, queue.Enqueue(key, new("first"), Hash("first")).Status);
        queue.Advance(10);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OutputPressurePausesHeadWithoutDroppingOrReordering()
    {
        var order = new List<string>();
        using var queue = new CoreTaskQueue<Work>("binding", (work, _) =>
        { order.Add(work.Name); return new(1, true); }, new(MaximumEvents: 1));
        queue.Enqueue(new("a", 1), new("one"), Hash("one"));
        queue.Enqueue(new("a", 2), new("two"), Hash("two"));
        Assert.Equal(1, queue.Advance(10));
        Assert.Equal(new[] { "one" }, order);
        Assert.Equal(1, queue.Count);
        queue.TryDequeueEvent(out _);
        Assert.Equal(1, queue.Advance(10));
        Assert.Equal(new[] { "one", "two" }, order);
    }

    [Fact]
    public void CapacityFenceDoesNotLetNewerProducerSerialOvertakeBlockedTask()
    {
        using var queue = new CoreTaskQueue<Work>("binding", (_, _) => new(1, true), new(MaximumTasks: 1));
        queue.Enqueue(new("a", 1), new("one"), Hash("one"));
        Assert.Equal(AdmissionStatus.WouldBlock, queue.Enqueue(new("a", 2), new("two"), Hash("two")).Status);
        queue.Advance(1);
        Assert.Equal("producer_backpressure", queue.Enqueue(new("a", 3), new("three"), Hash("three")).Code);
        Assert.Equal(AdmissionStatus.Accepted, queue.Enqueue(new("a", 2), new("two"), Hash("two")).Status);
    }

    [Fact]
    public void WaitingStepDoesNotSpinOrRunFollowingTask()
    {
        var calls = 0;
        using var queue = new CoreTaskQueue<Work>("binding", (_, _) => { calls++; return new(0, false); });
        queue.Enqueue(new("a", 1), new("one"), Hash("one"));
        queue.Enqueue(new("a", 2), new("two"), Hash("two"));
        Assert.Equal(0, queue.Advance(100));
        Assert.Equal(1, calls);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void TaskFaultClosesExecutionInsteadOfRetryingUnknownPartialEffects()
    {
        var calls = 0;
        using var queue = new CoreTaskQueue<Work>("binding", (_, _) =>
        { calls++; throw new InvalidOperationException("fixture"); });
        queue.Enqueue(new("a", 1), new("one"), Hash("one"));
        Assert.Throws<InvalidOperationException>(() => queue.Advance(1));
        Assert.Throws<InvalidOperationException>(() => queue.Advance(1));
        Assert.Equal(1, calls);
    }
}
