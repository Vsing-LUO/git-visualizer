using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace GitVisualizer.Infrastructure.Diagnostics;

// Fixed identifiers only: never accept repository paths, content, messages or credentials.
[JsonConverter(typeof(JsonStringEnumConverter<PerformanceOperation>))]
public enum PerformanceOperation
{
    OpenRepository,
    Refresh,
    HistoryPage,
    FileTreeBuild,
    DirectoryExpand,
    DirectoryApplyBatch,
    GraphRender
}

public sealed record PerformanceSample(int SchemaVersion, Guid RunId, long Sequence,
    PerformanceOperation Operation, double ElapsedMs, long WorkingSetStartBytes,
    long SampledPeakWorkingSetBytes, long ProcessLifetimePeakWorkingSetBytes,
    long ManagedHeapStartBytes, long ManagedHeapEndBytes, long ProcessAllocatedBytesDelta);

/// <summary>Opt-in JSONL recorder. Memory is process-wide, sampled every 10 ms, not per-operation allocation attribution.</summary>
public sealed class PerformanceRecorder : IDisposable
{
    private readonly Channel<PerformanceSample> queue = Channel.CreateBounded<PerformanceSample>(4096);
    private readonly Task writer;
    private readonly Timer timer;
    private readonly object gate = new();
    private readonly HashSet<Measurement> active = [];
    private long sequence;
    private long dropped;
    private int disposed;
    private Exception? writeError;
    private readonly string outputFile;
    public Guid RunId { get; } = Guid.NewGuid();
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public Exception? WriteError => writeError;
    public static PerformanceRecorder? Current { get; set; }

    public PerformanceRecorder(string outputFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        this.outputFile = outputFile;
        // Fail visibly at explicit setup; never overwrite an earlier measurement file.
        var stream = new StreamWriter(new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        writer = Task.Run(async () =>
        {
            try
            {
                using (stream)
                {
                    await foreach (var sample in queue.Reader.ReadAllAsync())
                        await stream.WriteLineAsync(JsonSerializer.Serialize(sample));
                }
            }
            catch (Exception error) { writeError = error; }
        });
        timer = new Timer(_ => SampleMemory(), null, 10, 10);
    }

    public static Measurement? Begin(PerformanceOperation operation) => Current?.Measure(operation);

    // UI batches need duration only. Querying process memory for every short batch
    // can itself exceed the dispatcher budget and distort the operation being timed.
    // Zero memory counters explicitly mean that this duration-only sample has none.
    public static void RecordDuration(PerformanceOperation operation, double elapsedMs)
    {
        var recorder = Current;
        if (recorder is null) return;
        lock (recorder.gate)
        {
            if (recorder.disposed != 0) return;
            var sample = new PerformanceSample(1, recorder.RunId, Interlocked.Increment(ref recorder.sequence),
                operation, elapsedMs, 0, 0, 0, 0, 0, 0);
            if (!recorder.queue.Writer.TryWrite(sample)) Interlocked.Increment(ref recorder.dropped);
        }
    }

    public Measurement Measure(PerformanceOperation operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var measurement = new Measurement(this, operation);
            active.Add(measurement);
            return measurement;
        }
    }

    private void SampleMemory()
    {
        lock (gate)
        {
            if (active.Count == 0) return;
            using var process = Process.GetCurrentProcess();
            foreach (var measurement in active)
                measurement.Peak = Math.Max(measurement.Peak, process.WorkingSet64);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            timer.Dispose();
            queue.Writer.TryComplete();
        }
        writer.GetAwaiter().GetResult();
        try
        {
            using var summary = new StreamWriter(new FileStream(outputFile + ".summary.json", FileMode.CreateNew));
            summary.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, runId = RunId, samples = sequence, droppedSamples = DroppedSamples,
                unfinishedScopes = active.Count, samplingIntervalMs = 10,
                writerErrorType = writeError?.GetType().Name
            }));
        }
        catch (Exception error) { writeError ??= error; }
    }

    public sealed class Measurement : IDisposable
    {
        private readonly PerformanceRecorder owner;
        private readonly PerformanceOperation operation;
        private readonly long started;
        private readonly long workingSet;
        private readonly long heap = GC.GetTotalMemory(false);
        private readonly long allocated = GC.GetTotalAllocatedBytes(false);
        private int ended;
        internal long Peak;

        internal Measurement(PerformanceRecorder owner, PerformanceOperation operation)
        {
            this.owner = owner;
            this.operation = operation;
            using var process = Process.GetCurrentProcess();
            workingSet = Peak = process.WorkingSet64;
            started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            using var process = Process.GetCurrentProcess();
            lock (owner.gate)
            {
                owner.active.Remove(this);
                var sample = new PerformanceSample(1, owner.RunId, Interlocked.Increment(ref owner.sequence),
                    operation, elapsed, workingSet, Math.Max(Peak, process.WorkingSet64),
                    process.PeakWorkingSet64, heap, GC.GetTotalMemory(false),
                    Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocated));
                if (!owner.queue.Writer.TryWrite(sample)) Interlocked.Increment(ref owner.dropped);
            }
        }
    }
}
