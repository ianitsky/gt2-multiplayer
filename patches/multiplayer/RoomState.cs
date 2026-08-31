using System.Text;

namespace GT2Port.Multiplayer;

/// <summary>
/// One player in a room. <paramref name="Colour"/> is an index into the paints
/// their car comes in, not a colour in itself: the cars each carry their own
/// list, so the same number means different paint on different cars and only
/// the pair is meaningful. It travels as the index rather than as the letter
/// the race wants because the lobby is where a car can still change, and an
/// index is the thing that has to be re-checked when it does.
///
/// Defaulted, so the hundred places that build a player without caring about
/// paint keep saying what they mean. The first paint is the one the arcade
/// would have chosen.
/// </summary>
public record Player(string Name, string Car, bool Ready, byte Colour = 0, bool Watching = false);

/// <summary>
/// Who is in a room, split the way the race needs them.
///
/// A room used to be a list of players, and a player's place in that list was
/// also their place on the grid and the seat every pose on the wire is keyed
/// by. Viewers break that: they are in the room and not in the race, so the two
/// numberings come apart, and a pose keyed by the wrong one arrives at the
/// wrong car.
///
/// So the seat counts along the drivers, and only the drivers. The order comes
/// from the room the host published, so every machine derives the same list
/// from the same room without anyone sending it.
/// </summary>
public static class Seats
{
    /// <summary>The players who are racing, in the order the seats count along.</summary>
    public static List<Player> Drivers(IReadOnlyList<Player> players) =>
        [.. players.Where(p => !p.Watching).Take(RaceGrid.Slots)];

    /// <summary>The players who are only watching.</summary>
    public static List<Player> Viewers(IReadOnlyList<Player> players) =>
        [.. players.Where(p => p.Watching)];

    /// <summary>
    /// Which seat <paramref name="who"/> holds, or -1 for a viewer and for
    /// anyone the room does not have - which are the same answer, because
    /// neither has a car for a pose to be about.
    /// </summary>
    public static int Of(IReadOnlyList<Player> players, string who)
    {
        var drivers = Drivers(players);
        for (int i = 0; i < drivers.Count; i++)
            if (drivers[i].Name == who) return i;
        return -1;
    }
}

public record Room(Guid Id, string Name, string Track, string CarGroup, int MaxPlayers, IReadOnlyList<Player> Players);

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
    const byte Version = 4;
    public const int MaxPlayers = 6;
    public const int MaxStringBytes = 64;

    /// <summary>
    /// Serialises a room that is already known to be valid. This is a programming
    /// error surface, not a hostile-input one: a room that breaks the wire format's
    /// own invariants (too many players, or a cap set above what the format allows)
    /// throws rather than silently emitting a packet whose count byte lies about its
    /// contents.
    /// </summary>
    public static byte[] Serialise(Room room)
    {
        if (room.Players.Count > MaxPlayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(room), room.Players.Count,
                $"Room has {room.Players.Count} players, which exceeds the {MaxPlayers}-player cap.");
        }
        if (room.MaxPlayers < 0 || room.MaxPlayers > MaxPlayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(room), room.MaxPlayers,
                $"Room.MaxPlayers is {room.MaxPlayers}, which must be between 0 and {MaxPlayers}.");
        }

        var buffer = new List<byte> { Version };
        buffer.AddRange(room.Id.ToByteArray());
        WriteString(buffer, room.Name);
        WriteString(buffer, room.Track);
        WriteString(buffer, room.CarGroup);
        buffer.Add((byte)room.MaxPlayers);
        buffer.Add((byte)room.Players.Count);
        foreach (var player in room.Players)
        {
            WriteString(buffer, player.Name);
            WriteString(buffer, player.Car);
            buffer.Add(player.Ready ? (byte)1 : (byte)0);
            buffer.Add(player.Colour);
            buffer.Add(player.Watching ? (byte)1 : (byte)0);
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
        if (!TryString(data, ref offset, out string carGroup)) return false;
        if (!TryByte(data, ref offset, out byte maxPlayers) || maxPlayers > MaxPlayers) return false;
        if (!TryByte(data, ref offset, out byte count) || count > MaxPlayers) return false;

        var players = new List<Player>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryString(data, ref offset, out string playerName)) return false;
            if (!TryString(data, ref offset, out string car)) return false;
            if (!TryByte(data, ref offset, out byte ready)) return false;
            if (!TryByte(data, ref offset, out byte colour)) return false;
            if (!TryByte(data, ref offset, out byte watching)) return false;
            players.Add(new Player(playerName, car, ready != 0, colour, watching != 0));
        }

        room = new Room(id, name, track, carGroup, maxPlayers, players);
        return true;
    }

    static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(TruncateToUtf8ByteLimit(value, MaxStringBytes));
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    /// <summary>
    /// Truncates to at most <paramref name="maxBytes"/> UTF-8 bytes, cutting only on a
    /// codepoint boundary. Walks the string one <see cref="Rune"/> (not char - a
    /// surrogate pair is one codepoint) at a time and stops before a rune's encoded
    /// bytes would push the total past the limit, so the result is always a valid
    /// UTF-8 prefix of the input rather than a byte sequence split mid-codepoint.
    /// </summary>
    static string TruncateToUtf8ByteLimit(string value, int maxBytes)
    {
        int byteCount = 0;
        int charsToKeep = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes) break;
            byteCount += runeBytes;
            charsToKeep += rune.Utf16SequenceLength;
        }
        return charsToKeep == value.Length ? value : value[..charsToKeep];
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
