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

/// <summary>
/// What the room is arranging next.
///
/// A room with qualifying opens on <see cref="Qualifying"/> and moves to
/// <see cref="Racing"/> once the qualifying session has been run. A room
/// without it is <see cref="Racing"/> from the start and never leaves.
///
/// One value rather than a "has qualifying" flag and a "has qualified" flag,
/// because two would allow a state that means nothing - qualified without
/// qualifying - and every machine has to agree on what the Start button is
/// about to do.
/// </summary>
/// <summary>
/// <see cref="Qualified"/> is a race like <see cref="Racing"/> and not the
/// same thing: it is a room that has run its qualifying and is arranging the
/// race off the grid that produced. The lobby stops letting anybody change car
/// or take their readiness back there, because both would make the grid
/// everybody just earned a lie about what is on it.
///
/// A room that never had qualifying is <see cref="Racing"/> throughout and
/// keeps every choice open, which is why one value cannot do for both.
/// </summary>
public enum RoomStage : byte { Racing = 0, Qualifying = 1, Qualified = 2 }

/// <summary>
/// A room, and the race it is arranging.
///
/// <paramref name="Laps"/> is how many laps that race is run over - the host's
/// choice, one to ninety-nine. Two is what an arcade race is built as, so a
/// room that never says otherwise runs what the game would have run anyway.
///
/// <paramref name="Minutes"/> is how long instead, when the host has asked for
/// a race against a clock rather than a lap count. Zero means laps, which keeps
/// a room that has never heard of this running exactly as it did.
///
/// <paramref name="Secret"/> is what a client has to say to be let in, and it is
/// the one thing about a room that is never published. A room announced on a
/// local network is announced to everyone on it, so a secret in that
/// announcement would be a secret told to the people it is meant to keep out.
/// It travels one way only: from a client that is asking, to the host that
/// decides. Empty means the room is open, which is what every room was before
/// there was anything to keep out.
/// </summary>
public record Room(Guid Id, string Name, string Track, string CarGroup, int MaxPlayers,
                   IReadOnlyList<Player> Players, byte Laps = RaceLaps.AsBuilt,
                   ushort Minutes = TimedRace.ByLaps,
                   RoomStage Stage = RoomStage.Racing,
                   string Secret = "")
{
    /// <summary>Whether this room's race is run to a clock.</summary>
    public bool ByTheClock => Minutes > TimedRace.ByLaps;

    /// <summary>Whether the next session out of this lobby is a qualifying one.</summary>
    public bool QualifyingNext => Stage == RoomStage.Qualifying;

    /// <summary>
    /// Whether this room has run its qualifying. The grid is settled from
    /// here, so the choices that made it are settled too.
    /// </summary>
    public bool HasQualified => Stage == RoomStage.Qualified;

    /// <summary>
    /// How long the next session is, which is not the room's own length while
    /// there is qualifying to do: qualifying is always two laps.
    /// </summary>
    public byte LapsNext => QualifyingNext ? Qualifying.Laps : Laps;

    public ushort MinutesNext => QualifyingNext ? TimedRace.ByLaps : Minutes;
}

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
    const byte Version = 7;
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
        buffer.Add(room.Laps);
        buffer.AddRange(BitConverter.GetBytes(room.Minutes));
        buffer.Add((byte)room.Stage);
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
        if (!TryByte(data, ref offset, out byte laps)) return false;
        if (!TryByte(data, ref offset, out byte minutesLow)) return false;
        if (!TryByte(data, ref offset, out byte minutesHigh)) return false;
        int minutes = minutesLow | (minutesHigh << 8);
        if (!TryByte(data, ref offset, out byte stage)) return false;
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

        // Clamped rather than trusted: this came off a socket, and a room
        // claiming a zero-lap race would be a race that ends before it starts.
        // Clamped rather than trusted, both of them: this came off a socket,
        // and a room claiming a zero-lap race would be a race that ends before
        // it starts. Zero minutes is not clamped, because zero is what a race
        // run to laps says.
        room = new Room(id, name, track, carGroup, maxPlayers, players,
                        (byte)RaceLaps.Sensible(laps),
                        minutes == 0 ? TimedRace.ByLaps : (ushort)TimedRace.Sensible(minutes),
                        // Named values only. This is a byte off a socket, and
                        // a room from a newer build may carry a stage this one
                        // has no meaning for - Racing is the honest reading of
                        // "some kind of race", and the host enforces the rest.
                        stage switch
                        {
                            (byte)RoomStage.Qualifying => RoomStage.Qualifying,
                            (byte)RoomStage.Qualified => RoomStage.Qualified,
                            _ => RoomStage.Racing,
                        });
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
