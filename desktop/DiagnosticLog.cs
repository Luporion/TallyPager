using System.Text.Json;

namespace TallyPager;

internal sealed record DiagnosticEntry(DateTimeOffset Time, string Source, string Event, string Detail);

internal sealed class DiagnosticLog : IDisposable
{
    internal const int Capacity = 1000;
    private readonly Lock gate = new();
    private readonly Queue<DiagnosticEntry> entries = new();
    private readonly string? path;
    private readonly CancellationTokenSource? stop;
    private readonly Task? writer;
    private long revision, savedRevision;
    private int disposed;
    private string? storageIssue;

    public DiagnosticLog(string? path = null)
    {
        this.path = path;
        if (path is null) return;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > 4_000_000) throw new InvalidDataException();
                var previous = JsonSerializer.Deserialize<DiagnosticEntry[]>(stream, ProfileStore.Json);
                foreach (var entry in (previous ?? []).TakeLast(Capacity))
                    if (entry is { Source: not null, Event: not null, Detail: not null })
                        entries.Enqueue(entry);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            storageIssue = "Der gespeicherte Diagnoseverlauf konnte nicht gelesen werden. Ein neuer Verlauf wird angelegt.";
        }
        stop = new();
        writer = Task.Run(() => PersistPeriodically(stop.Token));
    }

    public DiagnosticEntry[] Entries { get { lock (gate) return entries.ToArray(); } }
    public long Revision { get { lock (gate) return revision; } }
    public string? StorageIssue { get { lock (gate) return storageIssue; } }

    public void Add(string source, string kind, string detail)
    {
        lock (gate)
        {
            entries.Enqueue(new(DateTimeOffset.UtcNow, Limit(source, 80), Limit(kind, 80), Limit(detail, 384)));
            if (entries.Count > Capacity) entries.Dequeue();
            revision++;
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    private async Task PersistPeriodically(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(token)) Save();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Save(); }
    }

    private void Save()
    {
        long snapshotRevision;
        DiagnosticEntry[] snapshot;
        lock (gate)
        {
            if (revision == savedRevision) return;
            snapshotRevision = revision;
            snapshot = entries.ToArray();
        }
        try
        {
            ProfileStore.Write(path!, snapshot);
            lock (gate)
            {
                savedRevision = snapshotRevision;
                storageIssue = null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (gate) storageIssue = "Der Diagnoseverlauf kann derzeit nur im Arbeitsspeicher erfasst werden.";
        }
    }

    public void Dispose()
    {
        if (stop is null || Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        writer!.GetAwaiter().GetResult();
        stop.Dispose();
    }
}
