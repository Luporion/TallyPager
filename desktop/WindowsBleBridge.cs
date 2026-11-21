using System.Collections.Concurrent;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace TallyPager;

internal sealed class WindowsBleBridge : IRadioBridge
{
    private static readonly Guid ServiceId = new("d28b0400-d09a-4f12-bcdf-17a46b560e00");
    private static readonly Guid DownId = new("d28b0401-d09a-4f12-bcdf-17a46b560e00");
    private static readonly Guid UpId = new("d28b0402-d09a-4f12-bcdf-17a46b560e00");
    private readonly BluetoothLEAdvertisementWatcher watcher = new();
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly ConcurrentDictionary<string, long> retryAt = new();
    private readonly Channel<RadioPacket> inbound = Channel.CreateBounded<RadioPacket>(128);
    private volatile bool closed;
    private volatile string? failure;
    private volatile string? peerProblem;
    public string Mode => "ble";
    public int Connected => peers.Values.Count(peer => peer.Ready);
    public string? Failure => failure;
    public string? PeerProblem => peerProblem;

    public static async Task<WindowsBleBridge> Start()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter?.IsLowEnergySupported != true)
            throw new InvalidOperationException("Windows meldet keinen Bluetooth-LE-Adapter.");
        var radio = await adapter.GetRadioAsync();
        if (radio?.State != RadioState.On)
            throw new InvalidOperationException("Bluetooth zuerst in den Windows-Einstellungen einschalten.");
        return new WindowsBleBridge();
    }

    private WindowsBleBridge()
    {
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received += Discover;
        watcher.Stopped += Stopped;
        try { watcher.Start(); }
        catch { Dispose(); throw; }
    }

    private void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (!closed) failure = $"Windows hat die BLE-Suche beendet ({args.Error}).";
    }

    private void Discover(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string address = args.BluetoothAddress.ToString("x12");
        if (closed || peers.Count >= 6 || !Transport.IsMac(address) || peers.ContainsKey(address) ||
            retryAt.GetValueOrDefault(address) > Environment.TickCount64) return;
        var peer = new Peer();
        if (!peers.TryAdd(address, peer)) { peer.Close(); return; }
        if (closed) peer.Close();
        _ = Task.Run(() => RunPeer(address, args.BluetoothAddress, args.BluetoothAddressType, peer));
    }

    private async Task RunPeer(string address, ulong value, BluetoothAddressType type, Peer peer)
    {
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(12));
            var token = connect.Token;
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(value, type).AsTask(token);
            if (device is null) return;
            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return;
            using var service = services.Services[0];
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
            if (characteristics.Status != GattCommunicationStatus.Success) return;
            var down = characteristics.Characteristics.FirstOrDefault(item => item.Uuid == DownId);
            var up = characteristics.Characteristics.FirstOrDefault(item => item.Uuid == UpId);
            if (down is null || up is null) return;
            if (service.Session.MaxPduSize < RadioFragments.RequiredMtu)
            {
                peerProblem = $"Stick {Transport.DisplayAddress(address)} · BLE-Paketgröße {service.Session.MaxPduSize}, benötigt {RadioFragments.RequiredMtu}.";
                return;
            }

            var assembly = new RadioFragments();
            void Notification(GattCharacteristic characteristic, GattValueChangedEventArgs args)
            {
                if (closed || args.CharacteristicValue.Length is <= RadioFragments.Header or > RadioFragments.FrameLimit) return;
                using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var frame = new byte[args.CharacteristicValue.Length];
                reader.ReadBytes(frame);
                lock (assembly)
                    if (assembly.Receive(frame, Environment.TickCount64) is { } payload)
                        inbound.Writer.TryWrite(new(address, payload));
            }
            void ConnectionChanged(BluetoothLEDevice source, object args)
            {
                if (source.ConnectionStatus == BluetoothConnectionStatus.Disconnected) peer.Close();
            }

            up.ValueChanged += Notification;
            device.ConnectionStatusChanged += ConnectionChanged;
            try
            {
                if (await up.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token)
                    != GattCommunicationStatus.Success) return;
                peerProblem = null;
                peer.Ready = true;
                ushort packetId = 0;
                await foreach (var payload in peer.Outbound.Reader.ReadAllAsync(peer.Token))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    foreach (byte[] bytes in RadioFragments.Encode(payload, unchecked(++packetId)))
                    {
                        using var writer = new DataWriter();
                        writer.WriteBytes(bytes);
                        if (await down.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
                                .AsTask(deadline.Token) != GattCommunicationStatus.Success) return;
                    }
                }
            }
            finally
            {
                peer.Ready = false;
                up.ValueChanged -= Notification;
                device.ConnectionStatusChanged -= ConnectionChanged;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (!peer.Token.IsCancellationRequested) peerProblem = "BLE-Verbindung fehlgeschlagen · Windows versucht es erneut.";
        }
        finally
        {
            retryAt[address] = Environment.TickCount64 + 3500;
            peers.TryRemove(address, out _);
            peer.Close();
        }
    }

    public bool TryRead(out RadioPacket packet) => inbound.Reader.TryRead(out packet);
    public bool Send(string address, string payload, string? transport = null) => !closed && peers.TryGetValue(address, out var peer) &&
        peer.Ready && peer.Outbound.Writer.TryWrite(payload);

    public void Dispose()
    {
        closed = true;
        watcher.Received -= Discover;
        watcher.Stopped -= Stopped;
        watcher.Stop();
        foreach (var peer in peers.Values) peer.Close();
    }

    private sealed class Peer
    {
        private readonly CancellationTokenSource lifetime = new();
        public readonly Channel<string> Outbound = Channel.CreateBounded<string>(8);
        public CancellationToken Token => lifetime.Token;
        public volatile bool Ready;
        public void Close() => lifetime.Cancel();
    }
}
