using System.Diagnostics;
using Serilog.Events;
using static Lukdrasil.StepUpLogging.Tests.PrebufferHarness;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// <see cref="PreErrorBufferSink"/> picks a trace's stripe from the last four characters of its key, the same in every
/// process: random W3C trace ids and the benchmarks' counter ids spread over the stripes, and a key shorter than the
/// tail still maps into range.
/// </summary>
public class PrebufferHoldCostStripeTests
{
    private const int SixteenStripes = 1024;
    private const int OneStripe = 127;
    private const int W3CTraceIdSeed = 20261009;
    private const int W3CTraceIds = 16384;
    private const int W3CSpreadTolerance = 256;
    private const int BenchmarkStripeCapacity = 64;
    private const int BenchmarkHoldTraces = 256;
    private const int BenchmarkGrowthTraces = 4096;
    private const int AllocationCalls = 10_000;

    private static readonly string[] ShortKeys = ["", "a", "ab", "abc", "abcd"];

    [Fact]
    public void StripeOf_FixedTraceIds_MapToFixedStripes()
    {
        using var sink = SinkWith(SixteenStripes);
        string[] keys = ["4bf92f3577b34da6a3ce929d0e0e4736", "0af7651916cd43dd8448eb211c80319c", "00000000000000000000000000000001", "__global__", "trace-7", "a", ""];

        Assert.Equal([8, 11, 1, 2, 14, 1, 10], keys.Select(sink.StripeOf).ToArray());
    }

    [Fact]
    public void StripeOf_KeysWithTheSameLastFourChars_ShareAStripe()
    {
        const string firstPrefix = "4bf92f3577b34da6a3ce929d0e0e";
        const string secondPrefix = "0af7651916cd43dd8448eb211c80";
        using var sink = SinkWith(SixteenStripes);
        var tails = Enumerable.Range(0, 32).Select(i => $"{i * 2027:x4}").ToArray();

        Assert.Equal(tails.Select(tail => sink.StripeOf(firstPrefix + tail)), tails.Select(tail => sink.StripeOf(secondPrefix + tail)));
    }

    [Fact]
    public void StripeOf_OneStripe_IsAlwaysZero()
    {
        using var sink = SinkWith(OneStripe);
        string[] keys = [.. ShortKeys, "__global__", "trace-7", "4bf92f3577b34da6a3ce929d0e0e4736"];

        Assert.All(keys, key => Assert.Equal(0, sink.StripeOf(key)));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(1024)]
    public void StripeOf_KeysShorterThanTheTail_StayInRange(int maxContexts)
    {
        using var sink = SinkWith(maxContexts);
        var stripes = PreErrorBufferSink.StripeCountFor(maxContexts);

        Assert.All(ShortKeys, key => Assert.InRange(sink.StripeOf(key), 0, stripes - 1));
    }

    [Fact]
    public void StripeOf_SeededW3CTraceIds_SpreadEvenly()
    {
        using var sink = SinkWith(SixteenStripes);
        var random = new Random(W3CTraceIdSeed);
        var bytes = new byte[16];
        var traceIds = Enumerable.Range(0, W3CTraceIds).Select(_ =>
        {
            random.NextBytes(bytes);
            return ActivityTraceId.CreateFromBytes(bytes).ToHexString();
        });
        var perStripe = W3CTraceIds / PreErrorBufferSink.StripeCountFor(SixteenStripes);

        Assert.All(TracesPerStripe(sink, traceIds), count => Assert.InRange(count, perStripe - W3CSpreadTolerance, perStripe + W3CSpreadTolerance));
    }

    [Fact]
    public void StripeOf_CounterTraceIdsOfTheBenchmarks_FitTheBenchmarkCaps()
    {
        using var sink = SinkWith(SixteenStripes);

        Assert.All(TracesPerStripe(sink, CounterTraceIds(BenchmarkHoldTraces)), count => Assert.True(count <= BenchmarkStripeCapacity, $"{count} of {BenchmarkHoldTraces} counter ids share a stripe of {BenchmarkStripeCapacity}"));
        Assert.All(TracesPerStripe(sink, CounterTraceIds(BenchmarkGrowthTraces)), count => Assert.True(count >= BenchmarkStripeCapacity, $"only {count} of {BenchmarkGrowthTraces} counter ids fill a stripe of {BenchmarkStripeCapacity}"));
    }

    [Fact]
    public void StripeOf_DoesNotAllocate()
    {
        using var sink = SinkWith(SixteenStripes);
        string[] keys = [.. ShortKeys, "__global__", "trace-7", "4bf92f3577b34da6a3ce929d0e0e4736"];
        _ = sink.StripeOf(keys[0]);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationCalls; i++)
        {
            _ = sink.StripeOf(keys[i % keys.Length]);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static PreErrorBufferSink SinkWith(int maxContexts)
        => new(BypassInto(new Collector()), capacityPerContext: 1, maxContexts, minimumLevel: LogEventLevel.Information);

    private static IEnumerable<string> CounterTraceIds(int count)
        => Enumerable.Range(0, count).Select(trace => $"{trace:x32}");

    private static int[] TracesPerStripe(PreErrorBufferSink sink, IEnumerable<string> traceIds)
    {
        var counts = new int[PreErrorBufferSink.StripeCountFor(SixteenStripes)];
        foreach (var traceId in traceIds)
        {
            counts[sink.StripeOf(traceId)]++;
        }

        return counts;
    }
}
