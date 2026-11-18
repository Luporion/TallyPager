using System.Buffers.Binary;
using System.Text;

namespace TallyPager;

internal sealed class RadioFragments
{
    public const int Header = 8;
    private const int Fragment = 200, Limit = 900;
    public const int FrameLimit = Header + Fragment, RequiredMtu = FrameLimit + 3;
    private const uint Magic = 0x31525054;
    private readonly byte[] buffer = new byte[Limit];
    private ushort id;
    private int next, total;
    private long updated;

    public static IEnumerable<byte[]> Encode(string payload, ushort id)
    {
        byte[] data = Encoding.ASCII.GetBytes(payload);
        if (data.Length is < 1 or > Limit) yield break;
        int count = (data.Length + Fragment - 1) / Fragment;
        for (int part = 0; part < count; part++)
        {
            int offset = part * Fragment;
            int size = Math.Min(Fragment, data.Length - offset);
            var frame = new byte[Header + size];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), id);
            frame[6] = (byte)part;
            frame[7] = (byte)count;
            data.AsSpan(offset, size).CopyTo(frame.AsSpan(Header));
            yield return frame;
        }
    }

    public string? Receive(ReadOnlySpan<byte> frame, long now)
    {
        if (frame.Length is <= Header or > FrameLimit || BinaryPrimitives.ReadUInt32LittleEndian(frame) != Magic)
            return null;
        ushort packet = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]);
        int part = frame[6], count = frame[7], bytes = frame.Length - Header, offset = part * Fragment;
        if (count is < 1 or > 5 || part >= count || offset + bytes > Limit || part + 1 < count && bytes != Fragment)
            return null;
        if (part == 0) { id = packet; next = 0; total = count; }
        else if (id != packet || next != part || total != count || now - updated > 350) return null;
        frame[Header..].CopyTo(buffer.AsSpan(offset));
        updated = now;
        next = part + 1;
        if (next < total) return null;
        next = 0;
        return Encoding.ASCII.GetString(buffer, 0, offset + bytes);
    }
}
