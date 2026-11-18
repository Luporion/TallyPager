using System.Buffers;
using System.Globalization;
using System.Net;
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
    private const int XmlLimit = 4_000_000;
    private sealed class SubscriptionUnavailable : IOException { }
    private readonly CancellationTokenSource stop = new();
    private bool disposed;
    private volatile MixerState state = new("Verbinde …", []);
    public MixerState State => state;

    public VmixSource(string address, int port = 8099)
    {
        var token = stop.Token;
        var endpoint = WebEndpoint(address);
        _ = Task.Run(() => endpoint is null ? RunTcp(address, port, token) : RunWeb(endpoint, token));
    }

    internal static bool ValidAddress(string address) => Uri.CheckHostName(address) != UriHostNameType.Unknown ||
        WebEndpoint(address) is not null;

    internal static Uri? WebEndpoint(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.HostNameType == UriHostNameType.Unknown || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.AbsolutePath != "/" && !uri.AbsolutePath.TrimEnd('/').Equals("/api", StringComparison.OrdinalIgnoreCase)))
            return null;
        return new UriBuilder(uri) { Path = "/api/" }.Uri;
    }

    private async Task RunTcp(string host, int port, CancellationToken token)
    {
        bool subscribe = true;
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
                state = new(subscribe ? "TCP erreichbar · warte auf SUBSCRIBE TALLY" :
                    "TCP erreichbar · frage TALLY und XML direkt ab", []);
                await Receive(client.GetStream(), subscribe, token);
            }
            catch (SubscriptionUnavailable) when (!token.IsCancellationRequested)
            {
                // The first socket may still receive a late subscription reply or queued events.
                subscribe = false;
                state = new("Tally-Abonnement nicht verfügbar · versuche TCP-Abfrage", []);
                continue;
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

    private async Task Receive(NetworkStream stream, bool subscribe, CancellationToken token)
    {
        var reader = new VmixReader(stream);
        MixerInput[] inputs = [];
        string? current = null;
        bool metadata = false;
        string request = subscribe ? "SUBSCRIBE" : "";
        if (subscribe) await Write(stream, "SUBSCRIBE TALLY", token);
        long deadline = Environment.TickCount64 + 3000;
        long tallyDue = 0, xmlDue = 0;
        var lineTask = reader.Line(token);
        while (!token.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            if (request.Length > 0 && now >= deadline)
            {
                if (request == "SUBSCRIBE") throw new SubscriptionUnavailable();
                throw new TimeoutException($"Keine gültige Antwort auf {request}. vMix-Web-URL als Alternative prüfen.");
            }
            if (request.Length == 0)
            {
                if (now >= xmlDue)
                {
                    request = "XML";
                }
                else if (now >= tallyDue)
                {
                    request = "TALLY";
                }
                if (request.Length > 0)
                {
                    await Write(stream, request, token);
                    deadline = now + 3000;
                }
            }

            // A TALLY event can arrive while waiting for any other response.
            if (await Task.WhenAny(lineTask, Task.Delay(50, token)) != lineTask) continue;
            string line;
            try { line = (await lineTask).TrimEnd(' ', '\t'); }
            catch (IOException) when (request == "SUBSCRIBE" && !token.IsCancellationRequested)
            { throw new SubscriptionUnavailable(); }
            if (line == "TALLY OK" || line.StartsWith("TALLY OK ", StringComparison.Ordinal))
            {
                current = line.Length <= 9 ? "" : line[9..];
                if (current.Length > 1000 || current.Any(c => c is not ('0' or '1' or '2')))
                    throw new IOException("Ungültige TALLY-Antwort.");
                if (request == "TALLY")
                {
                    request = "";
                    tallyDue = Environment.TickCount64 + 850;
                }
                if (request != "SUBSCRIBE") Publish(inputs, metadata, current, subscribe);
            }
            else if (line.StartsWith("XML ", StringComparison.Ordinal) &&
                int.TryParse(line.Split(' ', 3)[1], NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            {
                if (length is < 1 or > XmlLimit) throw new IOException("Ungültige vMix-XML-Länge.");
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
                    current = null;
                    tallyDue = 0;
                }
                inputs = updated;
                metadata = true;
                if (request == "XML")
                {
                    request = "";
                    xmlDue = Environment.TickCount64 + 2000;
                }
                Publish(inputs, metadata, current, subscribe);
            }
            else if (line == "SUBSCRIBE OK TALLY" && request == "SUBSCRIBE")
            {
                request = "";
                state = new("SUBSCRIBE TALLY bestätigt · warte auf XML-Eingänge und TALLY", []);
            }
            else if (request.Length > 0 && (line == request + " ER" || line.StartsWith(request + " ER ", StringComparison.Ordinal)))
            {
                if (request == "SUBSCRIBE") throw new SubscriptionUnavailable();
                throw new IOException($"vMix hat {request} abgelehnt.");
            }
            lineTask = reader.Line(token);
        }
    }

    private void Publish(MixerInput[] inputs, bool metadata, string? tally, bool subscribe)
    {
        if (!metadata || tally is null)
        {
            state = new(metadata ? "XML-Eingänge empfangen · warte auf TALLY" :
                "TALLY empfangen · warte auf XML-Eingänge", inputs);
            return;
        }
        state = new(subscribe ? "Verbunden" : "Verbunden · TCP-Abfrage", inputs.Select(input => input with
        {
            Tally = input.Number <= tally.Length ? DecodeTally(tally[input.Number - 1]) : 0
        }).ToArray());
    }

    private async Task RunWeb(Uri endpoint, CancellationToken token)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        while (!token.IsCancellationRequested)
        {
            int delay = 500;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(3000);
                using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new IOException("Web-API verweigert den Zugriff. API-Freigabe und lokalen Zugriff in vMix prüfen.");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > XmlLimit) throw new IOException("vMix-XML ist zu groß.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var data = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (data.Length + count > XmlLimit) throw new IOException("vMix-XML ist zu groß.");
                    data.Write(buffer, 0, count);
                }
                data.Position = 0;
                var xml = await XDocument.LoadAsync(data, LoadOptions.None, timeout.Token);
                state = new("Verbunden · Web-API", ParseWebInputs(xml.Root));
            }
            catch (Exception error) when (error is IOException or HttpRequestException or System.Xml.XmlException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
                string detail = error switch
                {
                    OperationCanceledException => "Zeitüberschreitung",
                    System.Xml.XmlException => "Ungültige vMix-XML-Antwort",
                    HttpRequestException httpError => httpError.StatusCode is { } code ? $"HTTP {(int)code}" : "Verbindung fehlgeschlagen",
                    _ => error.Message
                };
                state = new($"vMix-Web-API · {detail} · neuer Versuch", []);
                delay = 1250;
            }
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static MixerInput[] ParseWebInputs(XElement? root)
    {
        var inputs = ParseInputs(root);
        if (!int.TryParse(root?.Element("active")?.Value, out int active) || active < 0 ||
            !int.TryParse(root.Element("preview")?.Value, out int preview) || preview < 0)
            throw new IOException("Die Web-API meldet weder einen gültigen Program- noch Preview-Eingang.");
        var overlays = (root.Element("overlays")?.Elements("overlay") ?? [])
            .Select(entry => int.TryParse(entry.Value, out int input) ? input : 0).ToHashSet();
        return inputs.Select(input => input with
        {
            Tally = input.Number == active || overlays.Contains(input.Number) ? 3 : input.Number == preview ? 2 : 1
        }).ToArray();
    }

    internal static MixerInput[] ParseInputs(string xml) => ParseInputs(XDocument.Parse(xml).Root);

    private static MixerInput[] ParseInputs(XElement? root)
    {
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
