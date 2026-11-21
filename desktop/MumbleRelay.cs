using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TallyPager;

// Used only by the serial pump. Each phone gets its own TCP connection and UDP
// source port on the PC. Encrypted Mumble payloads pass through unchanged.
internal sealed class MumbleRelay(IPAddress server, int port = MumbleRelay.Port) : IDisposable
{
    internal const int Port = 64738, MaxClients = 2, TcpChunk = 384, MaxDatagram = 1024;
    internal const int MaxLine = 16 + ((MaxDatagram + 2) / 3) * 4;
    internal const string Prefix = "TP M ";
    private const long Timeout = 5000;
    private readonly Peer?[] peers = new Peer?[MaxClients];
    private readonly Queue<uint> rejected = new();
    private readonly byte[] buffer = new byte[MaxDatagram + 1];
    private bool started;
    private int next, clients;
    private long heartbeat;
    private string? lastError;
    public int Clients => Volatile.Read(ref clients);
    public string? LastError => Volatile.Read(ref lastError);

    private sealed class Peer : IDisposable
    {
        internal readonly uint Id;
        internal readonly Socket Tcp, Udp;
        internal long OpeningAt, OutgoingAt, IncomingAt;
        internal bool Ready, AwaitingAck;
        internal byte[]? Incoming;
        internal int Offset;

        internal Peer(uint id, IPEndPoint destination, long now)
        {
            Id = id;
            OpeningAt = now;
            Tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                Udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Udp.Connect(destination);
                Udp.Blocking = false;
                Tcp.NoDelay = true;
                Tcp.Blocking = false;
                try { Tcp.Connect(destination); }
                catch (SocketException error) when (Retry(error)) { }
            }
            catch
            {
                Tcp.Dispose();
                Udp?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Tcp.Dispose();
            Udp.Dispose();
        }
    }

    internal readonly record struct Frame(uint Id, char Op, byte[]? Data = null);

    internal static string Format(uint id, char op, ReadOnlySpan<byte> data = default) =>
        FormattableString.Invariant($"TP M {id:x8} {op}") + (data.IsEmpty ? "" : " " + Convert.ToBase64String(data));

    internal static Frame? Parse(string line)
    {
        if (line.Length is < 15 or > MaxLine || !line.StartsWith(Prefix, StringComparison.Ordinal) || line[13] != ' ' ||
            !uint.TryParse(line.AsSpan(5, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id))
            return null;
        char op = line[14];
        if (line.Length == 15)
            return (id == 0 ? op is 'R' or 'H' : op is 'O' or 'R' or 'A' or 'C') ? new(id, op) : null;
        if (id == 0 || op is not ('D' or 'U') || line[15] != ' ') return null;
        var encoded = line.AsSpan(16);
        int limit = op == 'D' ? TcpChunk : MaxDatagram;
        if (encoded.Length == 0 || encoded.Length % 4 != 0 || encoded.Length > ((limit + 2) / 3) * 4 ||
            encoded.ContainsAny(" \t\r\n")) return null;
        Span<byte> decoded = stackalloc byte[MaxDatagram + 2];
        return Convert.TryFromBase64Chars(encoded, decoded, out int length) && length > 0 && length <= limit
            ? new(id, op, decoded[..length].ToArray()) : null;
    }

    public void Receive(string line, long now)
    {
        if (Parse(line) is not { Id: > 0 } frame) return;
        int index = Array.FindIndex(peers, peer => peer?.Id == frame.Id);
        if (frame.Op == 'O')
        {
            if (index >= 0) return;
            int free = Array.IndexOf(peers, null);
            if (free >= 0)
            {
                try
                {
                    peers[free] = new(frame.Id, new IPEndPoint(server, port), now);
                    return;
                }
                catch (SocketException error) { ServerError(error.SocketErrorCode); }
            }
            if (rejected.Count < MaxClients) rejected.Enqueue(frame.Id);
            return;
        }
        if (index < 0) return;
        var client = peers[index]!;
        switch (frame.Op)
        {
            case 'C': Close(index); break;
            case 'A' when client.Ready: client.AwaitingAck = false; break;
            case 'D' when client.Ready:
                if (client.Incoming is not null)
                {
                    Close(index);
                    if (rejected.Count < MaxClients) rejected.Enqueue(frame.Id);
                    break;
                }
                client.Incoming = frame.Data;
                client.Offset = 0;
                client.IncomingAt = now;
                break;
            case 'U' when client.Ready:
                try { client.Udp.Send(frame.Data!); }
                catch (SocketException) { /* UDP packets are disposable. */ }
                break;
        }
    }

    public string? NextOutput(long now)
    {
        if (!started || now >= heartbeat)
        {
            char op = started ? 'H' : 'R';
            started = true;
            heartbeat = now + 1000;
            return Format(0, op);
        }
        if (rejected.TryDequeue(out uint id)) return Format(id, 'C');
        for (int i = 0; i < peers.Length; i++)
        {
            int index = next;
            next = (next + 1) % peers.Length;
            if (peers[index] is { } peer && PumpPeer(index, peer, now) is { } line) return line;
        }
        return null;
    }

    private string? PumpPeer(int index, Peer peer, long now)
    {
        try
        {
            if (!peer.Ready)
            {
                if (!peer.Tcp.Poll(0, SelectMode.SelectWrite) && !peer.Tcp.Poll(0, SelectMode.SelectError))
                {
                    if (now - peer.OpeningAt < Timeout) return null;
                    throw new SocketException((int)SocketError.TimedOut);
                }
                int error = (int)peer.Tcp.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                if (error != 0) throw new SocketException(error);
                peer.Ready = true;
                Interlocked.Increment(ref clients);
                Volatile.Write(ref lastError, null);
                return Format(peer.Id, 'R');
            }

            if ((peer.AwaitingAck && now - peer.OutgoingAt >= Timeout) ||
                (peer.Incoming is not null && now - peer.IncomingAt >= Timeout))
            {
                Volatile.Write(ref lastError, "Mumble-Verbindung hat beim Datentransfer nicht rechtzeitig geantwortet.");
                Close(index);
                return Format(peer.Id, 'C');
            }
            if (peer.Incoming is { } pending && peer.Tcp.Poll(0, SelectMode.SelectWrite))
            {
                peer.Offset += peer.Tcp.Send(pending.AsSpan(peer.Offset), SocketFlags.None);
                if (peer.Offset == pending.Length)
                {
                    peer.Incoming = null;
                    return Format(peer.Id, 'A');
                }
            }
            if (!peer.AwaitingAck && peer.Tcp.Poll(0, SelectMode.SelectRead))
            {
                int count = peer.Tcp.Receive(buffer.AsSpan(0, TcpChunk), SocketFlags.None);
                if (count == 0)
                {
                    Close(index);
                    return Format(peer.Id, 'C');
                }
                peer.OutgoingAt = now;
                peer.AwaitingAck = true;
                return Format(peer.Id, 'D', buffer.AsSpan(0, count));
            }
            if (peer.Udp.Poll(0, SelectMode.SelectRead))
            {
                try
                {
                    int count = peer.Udp.Receive(buffer.AsSpan(), SocketFlags.None);
                    if (count is > 0 and <= MaxDatagram) return Format(peer.Id, 'U', buffer.AsSpan(0, count));
                }
                catch (SocketException) { /* A missing UDP response does not break TLS. */ }
            }
        }
        catch (SocketException error) when (Retry(error)) { }
        catch (SocketException error)
        {
            ServerError(error.SocketErrorCode);
            Close(index);
            return Format(peer.Id, 'C');
        }
        return null;
    }

    private static bool Retry(SocketException error) =>
        error.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress or SocketError.AlreadyInProgress;

    private void ServerError(SocketError error) =>
        Volatile.Write(ref lastError, $"Mumble-Server {server}:{port} · {error switch
        {
            SocketError.ConnectionRefused => "Verbindung abgelehnt. Läuft der Server an dieser Adresse?",
            SocketError.TimedOut => "Zeitüberschreitung.",
            _ => error.ToString()
        }}");

    private void Close(int index)
    {
        if (peers[index] is not { } peer) return;
        peers[index] = null;
        peer.Dispose();
        if (peer.Ready) Interlocked.Decrement(ref clients);
    }

    public void Dispose()
    {
        for (int i = 0; i < peers.Length; i++) Close(i);
        rejected.Clear();
    }
}
