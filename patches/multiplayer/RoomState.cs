using System.Buffers.Binary;
using System.Text;

namespace GT2Port.Multiplayer;

public record Player(string Name, string Car, bool Ready);

public record Room(Guid Id, string Name, string Track, int MaxPlayers, IReadOnlyList<Player> Players);

/// <summary>
/// The wire format for room state.
///
/// The host retransmits the whole room on every change rather than sending
/// deltas, so a lost packet corrects itself on the next one. That only works
/// while the whole room fits in a single datagram, which the tests pin down.
///
/// Deserialisation never throws: this parses data straight off a socket, where
/// anything at all can arrive.
/// </summary>
public static class RoomState
{
    const byte Version = 1;
    const int MaxPlayers = 6;
    const int MaxStringBytes = 64;

    public static byte[] Serialise(Room room)
    {
        var buffer = new List<byte> { Version };
        buffer.AddRange(room.Id.ToByteArray());
        WriteString(buffer, room.Name);
        WriteString(buffer, room.Track);
        buffer.Add((byte)room.MaxPlayers);
        buffer.Add((byte)room.Players.Count);
        foreach (var player in room.Players)
        {
            WriteString(buffer, player.Name);
            WriteString(buffer, player.Car);
            buffer.Add(player.Ready ? (byte)1 : (byte)0);
        }
        return [.. buffer];
    }

    public static bool TryDeserialise(ReadOnlySpan<byte> data, out Room room)
    {
        room = null!;
        int offset = 0;

        if (!TryByte(data, ref offset, out byte version) || version != Version) return false;
        if (data.Length - offset < 16) return false;
        var id = new Guid(data.Slice(offset, 16));
        offset += 16;

        if (!TryString(data, ref offset, out string name)) return false;
        if (!TryString(data, ref offset, out string track)) return false;
        if (!TryByte(data, ref offset, out byte maxPlayers)) return false;
        if (!TryByte(data, ref offset, out byte count) || count > MaxPlayers) return false;

        var players = new List<Player>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryString(data, ref offset, out string playerName)) return false;
            if (!TryString(data, ref offset, out string car)) return false;
            if (!TryByte(data, ref offset, out byte ready)) return false;
            players.Add(new Player(playerName, car, ready != 0));
        }

        room = new Room(id, name, track, maxPlayers, players);
        return true;
    }

    static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes) bytes = bytes[..MaxStringBytes];
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    static bool TryByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        if (offset >= data.Length) { value = 0; return false; }
        value = data[offset++];
        return true;
    }

    static bool TryString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (!TryByte(data, ref offset, out byte length)) return false;
        if (length > MaxStringBytes || data.Length - offset < length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }
}
