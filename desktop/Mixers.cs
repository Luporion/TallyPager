using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using LibAtem.Commands;
using LibAtem.Commands.MixEffects;
using LibAtem.Commands.Settings;
using LibAtem.Net;

namespace TallyPager;

internal sealed record MixerInput(int Number, string Name, int Tally, string? Key = null)
{
    public override string ToString() => $"{Number} · {Name}";
}

internal sealed record MixerState(string Status, MixerInput[] Inputs)
{
    public static MixerState Off { get; } = new("Getrennt", []);
}

internal sealed class AtemSource : IDisposable
{
    private readonly AtemClient client;
    private readonly object gate = new();
    private readonly Dictionary<int, string> names = [];
    private Dictionary<int, int>? tallies;
    private int? program, preview;
    private bool initialized, disposed;
    private volatile MixerState state = new("Verbinde …", []);
    public MixerState State => state;

    public AtemSource(string address)
    {
        client = new AtemClient(address, false);
        client.OnReceive += Receive;
        client.OnDisconnect += Disconnected;
        try { client.Connect(); }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Disconnected(object sender)
    {
        lock (gate)
        {
            initialized = false;
            names.Clear();
            tallies = null;
            program = preview = null;
            state = new("Verbindung verloren · neuer Versuch", []);
        }
    }

    private void Receive(object sender, IReadOnlyList<ICommand> commands)
    {
        lock (gate)
        {
            if (disposed) return;
            bool changed = false;
            foreach (var command in commands)
            {
                switch (command)
                {
                    case InitializationCompleteCommand:
                        initialized = changed = true;
                        break;
                    case InputPropertiesGetCommand input:
                        names[(int)input.Id] = input.LongName;
                        changed = true;
                        break;
                    case TallyBySourceCommand tally:
                        tallies = tally.Tally.ToDictionary(pair => (int)pair.Key,
                            pair => pair.Value.Item1 ? 3 : pair.Value.Item2 ? 2 : 1);
                        changed = true;
                        break;
                    case ProgramInputGetCommand pgm when (int)pgm.Index == 0:
                        program = (int)pgm.Source;
                        changed = true;
                        break;
                    case PreviewInputGetCommand pvw when (int)pvw.Index == 0:
                        preview = (int)pvw.Source;
                        changed = true;
                        break;
                }
            }
            if (!initialized || !changed) return;
            var numbers = names.Keys.Concat(tallies?.Keys.AsEnumerable() ?? [])
                .Concat(new[] { program, preview }.OfType<int>()).Distinct().Order();
            state = new("Verbunden", numbers.Select(number => new MixerInput(number,
                names.GetValueOrDefault(number, $"Eingang {number}"),
                tallies is not null ? tallies.GetValueOrDefault(number, 1) :
                    program == number ? 3 : preview == number ? 2 : program.HasValue && preview.HasValue ? 1 : 0)).ToArray());
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        client.OnReceive -= Receive;
        client.OnDisconnect -= Disconnected;
        client.Dispose();
    }
}

internal sealed class VmixSource : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private bool disposed;
    private volatile MixerState state = new("Verbinde …", []);
    public MixerState State => state;

    public VmixSource(string host, int port = 8099)
    {
        var token = stop.Token;
        _ = Task.Run(() => Run(host, port, token));
    }

    private async Task Run(string host, int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool tcpConnected = false;
            try
            {
                using var client = new TcpClient { NoDelay = true };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(3000);
                    await client.ConnectAsync(host, port, timeout.Token);
                }
                tcpConnected = true;
                state = new("TCP erreichbar · warte auf SUBSCRIBE TALLY", []);
                await Receive(client.GetStream(), token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or
                System.Xml.XmlException or TimeoutException)
            {
                if (token.IsCancellationRequested) return;
                string errorText = error switch
                {
                    SocketException socket => socket.SocketErrorCode.ToString(),
                    OperationCanceledException => "Zeitüberschreitung",
                    System.Xml.XmlException => "Ungültige XML-Eingangsliste",
                    _ => error.Message
                };
                state = new(tcpConnected ? $"TCP verbunden, vMix-Protokoll: {errorText} · neuer Versuch" :
                    $"TCP {host}:{port} nicht erreichbar · {errorText} · neuer Versuch", []);
            }
            try { await Task.Delay(1250, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Receive(NetworkStream stream, CancellationToken token)
    {
        var reader = new VmixReader(stream);
        MixerInput[] inputs = [];
        string current = "";
        bool metadata = false;
        string request = "SUBSCRIBE";
        await Write(stream, "SUBSCRIBE TALLY", token);
        long deadline = Environment.TickCount64 + 3000;
        long tallyDue = 0, xmlDue = 0;
        var lineTask = reader.Line(token);
        while (!token.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            if (request.Length > 0 && now >= deadline)
                throw new TimeoutException($"Keine gültige Antwort auf {request switch { "SUBSCRIBE" => "SUBSCRIBE TALLY", _ => request }}");
            if (request.Length == 0)
            {
                if (now >= xmlDue)
                {
                    request = "XML";
                    xmlDue = now + 2000;
                }
                else if (now >= tallyDue)
                {
                    request = "TALLY";
                    tallyDue = now + 850;
                }
                if (request.Length > 0)
                {
                    await Write(stream, request, token);
                    deadline = now + 3000;
                }
            }

            // A TALLY event can arrive while waiting for any other response.
            if (await Task.WhenAny(lineTask, Task.Delay(50, token)) != lineTask) continue;
            string line = await lineTask;
            if (line.StartsWith("TALLY OK ", StringComparison.Ordinal))
            {
                current = line[9..];
                if (current.Length == 0 || current.Any(c => c is not ('0' or '1' or '2')))
                    throw new IOException("Ungültige TALLY-Antwort.");
                if (request == "TALLY") request = "";
                if (request != "SUBSCRIBE") Publish(inputs, metadata, current);
            }
            else if (line.StartsWith("XML ", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            {
                if (length is < 1 or > 4_000_000) throw new IOException("Ungültige vMix-XML-Länge.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(3000);
                string xml;
                try { xml = Encoding.UTF8.GetString(await reader.Bytes(length, timeout.Token)); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("XML-Eingangsliste unvollständig."); }
                var updated = ParseInputs(xml);
                if (metadata && !inputs.Select(input => (input.Number, input.Key))
                    .SequenceEqual(updated.Select(input => (input.Number, input.Key))))
                {
                    // Wait for a fresh TALLY after a preset/layout change.
                    current = "";
                    tallyDue = 0;
                }
                inputs = updated;
                metadata = true;
                if (request == "XML") request = "";
                Publish(inputs, metadata, current);
            }
            else if (line == "SUBSCRIBE OK TALLY" && request == "SUBSCRIBE")
            {
                request = "";
                state = new("SUBSCRIBE TALLY bestätigt · warte auf XML-Eingänge und TALLY", []);
            }
            else if (request.Length > 0 && line.StartsWith(request + " ER", StringComparison.Ordinal))
                throw new IOException($"vMix hat {request} abgelehnt.");
            lineTask = reader.Line(token);
        }
    }

    private void Publish(MixerInput[] inputs, bool metadata, string tally)
    {
        if (!metadata || tally.Length == 0)
        {
            state = new(metadata ? "XML-Eingänge empfangen · warte auf TALLY" :
                "TALLY empfangen · warte auf XML-Eingänge", inputs);
            return;
        }
        state = new("Verbunden", inputs.Select(input => input with
        {
            Tally = input.Number <= tally.Length ? DecodeTally(tally[input.Number - 1]) : 0
        }).ToArray());
    }

    internal static MixerInput[] ParseInputs(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root?.Name.LocalName != "vmix" || root.Element("inputs") is not { } inputs)
            throw new IOException("XML-Antwort enthält keine vMix-Eingangsliste.");
        return inputs.Elements("input")
        .Select(entry => new MixerInput(int.TryParse((string?)entry.Attribute("number"), out int number) ? number : 0,
            (string?)entry.Attribute("title") ?? "",
            0, (string?)entry.Attribute("key")))
        .Where(input => input.Number is > 0 and <= 1000).OrderBy(input => input.Number).ToArray();
    }

    internal static int DecodeTally(char tally) => tally switch { '0' => 1, '1' => 3, '2' => 2, _ => 0 };

    private static Task Write(NetworkStream stream, string command, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), token).AsTask();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        stop.Dispose();
    }
}

// vMix prefixes XML with its byte length. Keep line and payload reads on the same buffer.
internal sealed class VmixReader(NetworkStream stream)
{
    private readonly byte[] buffer = new byte[8192];
    private int offset, end;

    public async Task<string> Line(CancellationToken token)
    {
        var result = new ArrayBufferWriter<byte>();
        while (result.WrittenCount <= 4096)
        {
            if (offset == end) await Fill(token);
            int newline = Array.IndexOf(buffer, (byte)'\n', offset, end - offset);
            int length = newline < 0 ? end - offset : newline - offset;
            result.Write(buffer.AsSpan(offset, length));
            offset += length;
            if (newline < 0) continue;
            offset++;
            if (result.WrittenCount > 4096) break;
            return Encoding.UTF8.GetString(result.WrittenSpan).TrimEnd('\r');
        }
        throw new IOException("Zu lange vMix-Protokollzeile.");
    }

    public async Task<byte[]> Bytes(int length, CancellationToken token)
    {
        byte[] result = new byte[length];
        int written = 0;
        while (written < length)
        {
            if (offset == end) await Fill(token);
            int count = Math.Min(end - offset, length - written);
            buffer.AsSpan(offset, count).CopyTo(result.AsSpan(written));
            offset += count;
            written += count;
        }
        return result;
    }

    private async Task Fill(CancellationToken token)
    {
        end = await stream.ReadAsync(buffer, token);
        offset = 0;
        if (end == 0) throw new EndOfStreamException();
    }
}
