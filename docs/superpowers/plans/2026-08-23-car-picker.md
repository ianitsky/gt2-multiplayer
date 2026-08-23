# Car Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The host picks a car class when creating a room; every player then picks a car from that class instead of typing one.

**Architecture:** The five arcade classes are generated at build time from the car-code arrays in the `gt2_03` overlay into a committed C# file. Display names come off the player's own disc at runtime, out of `.carinfoe` in `GT2.VOL`. A catalogue layer merges the generated groups with optional user-defined groups from a JSON file, so new groups and new cars need no rebuild.

**Tech Stack:** C# / .NET 10, ImGuiNET, `System.Text.Json`, xunit. Python 3 for the generator.

## Global Constraints

- Target framework `net10.0`.
- New code only under `patches/multiplayer/`, `tools/`, and `tests/GT2Port.Tests/`.
- **Nothing under `generated/` or the `RecompOne/` submodule may change.**
- The disc image is **not** in the repository. No test may depend on it. `ovl_bin/gt2_03.bin` **is** in the repository.
- `Room.CarGroup` carries a group **id**; `Player.Car` carries a car **code**. Never display text.
- A car whose name will not parse shows as its code. A group id this build does not have shows raw and is not pickable. The lobby never fails to open because data is missing or a config file is wrong.
- The five arcade groups, in order: `special` (10 cars), `a` (8), `b` (9), `c` (9), `rally` (24).

---

### Task 1: Read the car database

**Files:**
- Create: `patches/multiplayer/CarInfo.cs`
- Test: `tests/GT2Port.Tests/CarInfoTests.cs`

**Interfaces:**
- Consumes: `VolArchive.TryRead(string path, out byte[] data)`.
- Produces:
  - `public sealed class CarInfo`
  - `public static CarInfo? TryLoad(VolArchive? archive)` — reads `.carinfoe`. Null when there is no archive or the file will not parse. Never throws.
  - `public static CarInfo? TryParse(byte[] data)` — the same over bytes. Internal seam for tests; make it `internal`.
  - `public bool TryName(string code, out string name)` — false for a code that is not in the database or whose name will not parse.
  - `public string DisplayName(string code)` — the code itself when unknown, mirroring `CourseTable.DisplayName`.
  - `public int Count { get; }`

**The format:**

```
+0   'C','A','R', 0
+4   u32 count            (1110 on the real disc)
+8   count * 8-byte records:
       +0  u32  packed code
       +4  u16  offset of this car's field block
       +6  u16  a field this reader does not use
then the field blocks, back to back
```

The packed code is five characters, six bits each, **most significant first**,
over the alphabet `-0123456789abcdefghijklmnopqrstuvwxyz` — index 0 is `-`,
index 1 is `0`, index 11 is `a`. Record 0 on the real disc decodes to `a-a7r`.

A field block runs from its own offset to the **next record's** offset (the last
one runs to the end of the file). Strip its trailing NULs. The display name is
the last length-counted string in it: scanning backwards, the first position `p`
whose byte value equals `block.Length - p - 1` and is non-zero; the name is
everything after `p`. Some names begin with a `0x7f` marker that is counted in
the length — drop any byte outside the printable ASCII range 32..126 when
building the string.

This resolves 1087 of the real disc's 1110 cars and all 60 arcade cars. A block
with no such position has no name; `TryName` returns false.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class CarInfoTests
{
    const string Alphabet = "-0123456789abcdefghijklmnopqrstuvwxyz";

    static uint Pack(string code)
    {
        uint packed = 0;
        for (int i = 0; i < 5; i++)
            packed |= (uint)Alphabet.IndexOf(code[i]) << ((4 - i) * 6);
        return packed;
    }

    /// <summary>
    /// Builds a car database the way the disc stores one: fixed records first,
    /// then the variable field blocks they point at. Each block is given a
    /// binary prefix of its own length, because on the disc that prefix varies
    /// and a reader must not assume it away.
    /// </summary>
    static byte[] BuildDatabase(params (string Code, byte[] Prefix, string Name)[] cars)
    {
        var blocks = new List<byte[]>();
        foreach (var (_, prefix, name) in cars)
        {
            var text = Encoding.ASCII.GetBytes(name);
            var block = new List<byte>(prefix) { (byte)text.Length };
            block.AddRange(text);
            block.Add(0);
            blocks.Add(block.ToArray());
        }

        int blockBase = 8 + cars.Length * 8;
        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes((uint)cars.Length));

        int at = blockBase;
        for (int i = 0; i < cars.Length; i++)
        {
            image.AddRange(BitConverter.GetBytes(Pack(cars[i].Code)));
            image.AddRange(BitConverter.GetBytes((ushort)at));
            image.AddRange(BitConverter.GetBytes((ushort)0));
            at += blocks[i].Length;
        }
        foreach (var block in blocks) image.AddRange(block);
        return image.ToArray();
    }

    [Fact]
    public void Reads_a_name_through_the_packed_code()
    {
        var db = BuildDatabase(("a-a7r", [0xC5, 0x25, 0xA8], "Mazda RX-7 A-spec LM"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal(1, info!.Count);
        Assert.True(info.TryName("a-a7r", out var name));
        Assert.Equal("Mazda RX-7 A-spec LM", name);
    }

    [Fact]
    public void Reads_every_car_when_the_blocks_are_different_lengths()
    {
        var db = BuildDatabase(
            ("a-a7r", [0xC5, 0x25, 0xA8, 0x38, 0x6C, 0x73], "Mazda RX-7 A-spec LM"),
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal("Mazda RX-7 A-spec LM", info!.DisplayName("a-a7r"));
        Assert.Equal("Honda S2000", info.DisplayName("h2s2n"));
        Assert.Equal("Volkswagen Golf Rally Car", info.DisplayName("gv4rr"));
    }

    [Fact]
    public void Drops_the_marker_byte_some_names_carry()
    {
        // On the disc a name is often stored as 0x7f followed by the text, with
        // the marker counted in the length. It is not part of the name.
        var text = Encoding.ASCII.GetBytes("Viper GTS");
        var block = new List<byte> { 0x94, 0x52, (byte)(text.Length + 1), 0x7F };
        block.AddRange(text);
        block.Add(0);

        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(1u));
        image.AddRange(BitConverter.GetBytes(Pack("dvpgn")));
        image.AddRange(BitConverter.GetBytes((ushort)16));
        image.AddRange(BitConverter.GetBytes((ushort)0));
        image.AddRange(block);

        var info = CarInfo.TryParse(image.ToArray());

        Assert.NotNull(info);
        Assert.Equal("Viper GTS", info!.DisplayName("dvpgn"));
    }

    [Fact]
    public void An_unknown_code_shows_as_itself()
    {
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal("Honda S2000", info!.DisplayName("h2s2n"));   // the database works
        Assert.False(info.TryName("zzzzz", out _));
        Assert.Equal("zzzzz", info.DisplayName("zzzzz"));
    }

    [Fact]
    public void Refuses_something_that_is_not_a_car_database()
    {
        Assert.Null(CarInfo.TryParse([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Refuses_a_truncated_database_without_throwing()
    {
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        Assert.NotNull(CarInfo.TryParse(db));                       // whole file parses
        Assert.Null(CarInfo.TryParse(db[..(db.Length - 6)]));
    }

    [Fact]
    public void Refuses_a_count_the_file_cannot_hold()
    {
        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(100000u));

        Assert.Null(CarInfo.TryParse(image.ToArray()));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

- [ ] **Step 3: Implement `CarInfo`**

Follow `patches/multiplayer/VolArchive.cs` and `Tim.cs`: bounds check before
every read, nothing throws, an XML doc saying why the class exists. Parse the
records once into a dictionary at load; do not scan on every lookup.

- [ ] **Step 4: Run the tests**

- [ ] **Step 5: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Read the car database off the disc"
```

---

### Task 2: Generate the arcade groups

**Files:**
- Create: `tools/gen_car_table.py`
- Create (generated, committed): `patches/multiplayer/CarTable.cs`
- Test: `tests/GT2Port.Tests/CarTableTests.cs`

**Interfaces:**
- Consumes: `ovl_bin/gt2_03.bin`.
- Produces:
  - `public sealed record CarGroup(string Id, string Name, IReadOnlyList<string> Cars)`
  - `public static class CarTable` with `public static IReadOnlyList<CarGroup> Arcade { get; }`

**Where the data is.** `gt2_03` loads at `0x80010000`. A table of seven u32
pointers at file offset `0x420e8` points at arrays of pointers to five-character
car-code strings. Only five of the seven targets are distinct, and the duplicates
are the last three entries all pointing at the same array. In table order the
distinct targets are:

| target | cars | id | name |
|---|---|---|---|
| `0x41b84` | 10 | `special` | `Special` |
| `0x41c70` | 8 | `a` | `Class A` |
| `0x41d20` | 9 | `b` | `Class B` |
| `0x41df0` | 9 | `c` | `Class C` |
| `0x41ec0` | 24 | `rally` | `Rally` |

**Do not hardcode those offsets.** Find them the way the course generator finds
its tables: locate the block of car-code strings (anchor on `ldvan`, which is the
first car of the Special group, and walk outward while the bytes stay printable
or NUL), scan every 4-byte-aligned position for pointers into that block, and
group the hits into runs whose neighbours are 4 bytes apart. Take the runs in
ascending address order.

Exactly five runs of 10, 8, 9, 9 and 24 are expected. Anything else must **fail
with a message naming the run sizes found** — a generator that silently emits a
short table is worse than one that stops.

The ids and display names above are ours, not the game's; the game stores no
label for these arrays. Assign them by position, and say so in the generated
file's header comment.

- [ ] **Step 1: Write the generator**

Model it on `tools/gen_course_table.py`, which does the same shape of job — read
it first and match its structure, its failure style, and its C# string escaping.

The emitted file:

```csharp
// Generated by tools/gen_car_table.py from ovl_bin/gt2_03.bin. Do not edit by hand.
//
// The five car arrays arcade mode picks from, in the order the overlay stores
// them. The game keeps no label for them: the ids and names here are ours,
// assigned by position, and the order is what the sixteen event codes
// A0S A0A A0B A0C ... A3C imply - S, A, B, C - with the rally array last.

namespace GT2Port.Multiplayer;

public sealed record CarGroup(string Id, string Name, IReadOnlyList<string> Cars);

public static class CarTable
{
    public static IReadOnlyList<CarGroup> Arcade { get; } =
    [
        new CarGroup("special", "Special", ["ldvan", "dvpgn", ...]),
        ...
    ];
}
```

- [ ] **Step 2: Run it**

```bash
python tools/gen_car_table.py
```

Expected: five groups, 60 cars. Read the generated file and confirm the Special
group opens with `ldvan`, Class A with `ccrcn`, and Rally with `t2cxr`.

- [ ] **Step 3: Write the tests**

```csharp
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class CarTableTests
{
    [Fact]
    public void Holds_the_five_arcade_groups()
    {
        Assert.Equal(
            new[] { "special", "a", "b", "c", "rally" },
            CarTable.Arcade.Select(g => g.Id).ToArray());
        Assert.Equal(
            new[] { 10, 8, 9, 9, 24 },
            CarTable.Arcade.Select(g => g.Cars.Count).ToArray());
    }

    [Fact]
    public void Every_group_is_named_and_every_code_is_five_characters()
    {
        Assert.All(CarTable.Arcade, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.All(CarTable.Arcade, g => Assert.All(g.Cars, c => Assert.Equal(5, c.Length)));
    }

    [Fact]
    public void No_car_appears_in_two_groups()
    {
        var all = CarTable.Arcade.SelectMany(g => g.Cars).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
```

- [ ] **Step 4: Run the tests**

- [ ] **Step 5: Commit**

```bash
git add tools patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Generate the arcade car groups from the game's tables"
```

---

### Task 3: The catalogue, and custom groups

**Files:**
- Create: `patches/multiplayer/CarCatalogue.cs`
- Test: `tests/GT2Port.Tests/CarCatalogueTests.cs`

**Interfaces:**
- Consumes: `CarTable.Arcade`, `CarInfo`.
- Produces:
  - `public sealed class CarCatalogue`
  - `public static CarCatalogue Load(CarInfo? info, string? configPath)` — merges the built-in groups with the file at `configPath` when it exists. Never throws.
  - `internal static CarCatalogue FromJson(CarInfo? info, string? json)` — the same over a string. The seam tests use.
  - `public IReadOnlyList<CarGroup> Groups { get; }`
  - `public bool TryFind(string groupId, out CarGroup group)`
  - `public string DisplayName(string carCode)` — through `CarInfo`, falling back to the code.

**The merge rules**, and there are only four:

1. A custom group whose id is not built in is appended, after the built-in ones,
   in the order the file lists them.
2. A custom group whose id matches a built-in one replaces it **in place**, so
   the order the player sees does not jump around.
3. A car code that `CarInfo` does not know is dropped from that group, and one
   line naming it goes to the console. When `CarInfo` is null nothing is dropped
   — with no database there is nothing to check against, and dropping every car
   would be worse than trusting the file.
4. A group left with no cars is dropped. So is one with a blank id or name.

A file that is not valid JSON, or is not an array, is ignored whole, with one
console line. The catalogue is then exactly the built-in groups.

- [ ] **Step 1: Write the failing tests**

```csharp
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class CarCatalogueTests
{
    // A database holding just the cars these tests name. CarInfo's own tests
    // cover parsing; this only needs it to answer "is this a real car".
    static CarInfo Database() => CarInfoTests.BuildDatabaseFor(
        "ldvan", "dvpgn", "x2a8n", "q2mcn", "h2s2n");

    [Fact]
    public void Without_a_config_file_the_catalogue_is_the_arcade_groups()
    {
        var catalogue = CarCatalogue.FromJson(Database(), null);

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    [Fact]
    public void A_new_group_is_appended_after_the_built_in_ones()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "q2mcn"] }]""");

        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);
        Assert.Equal("kei", catalogue.Groups[^1].Id);
        Assert.Equal(new[] { "x2a8n", "q2mcn" }, catalogue.Groups[^1].Cars);
    }

    [Fact]
    public void A_matching_id_replaces_the_built_in_group_in_place()
    {
        int position = CarTable.Arcade.ToList().FindIndex(g => g.Id == "c");

        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "c", "name": "Class C (house)", "cars": ["h2s2n"] }]""");

        Assert.Equal(CarTable.Arcade.Count, catalogue.Groups.Count);
        Assert.Equal("Class C (house)", catalogue.Groups[position].Name);
        Assert.Equal(new[] { "h2s2n" }, catalogue.Groups[position].Cars);
    }

    [Fact]
    public void A_car_the_database_does_not_know_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "nosuch", "q2mcn"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n", "q2mcn" }, group.Cars);
    }

    [Fact]
    public void A_group_left_with_no_cars_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """
            [{ "id": "kei",   "name": "Kei cars", "cars": ["x2a8n"] },
             { "id": "ghost", "name": "Ghosts",   "cars": ["nosuch"] }]
            """);

        Assert.True(catalogue.TryFind("kei", out _));     // the file was read
        Assert.False(catalogue.TryFind("ghost", out _));
    }

    [Fact]
    public void Nothing_is_dropped_when_there_is_no_database()
    {
        var catalogue = CarCatalogue.FromJson(null,
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "nosuch"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n", "nosuch" }, group.Cars);
    }

    [Fact]
    public void A_malformed_file_is_ignored_whole()
    {
        var good = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]""");
        Assert.True(good.TryFind("kei", out _));          // valid JSON is read

        var catalogue = CarCatalogue.FromJson(Database(), "{ not json at all");

        Assert.Equal(CarTable.Arcade.Count, catalogue.Groups.Count);
    }

    [Fact]
    public void A_group_with_a_blank_id_or_name_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """
            [{ "id": "",    "name": "Nameless", "cars": ["x2a8n"] },
             { "id": "ok",  "name": "  ",       "cars": ["x2a8n"] },
             { "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]
            """);

        Assert.True(catalogue.TryFind("kei", out _));     // the file was read
        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);
    }

    [Fact]
    public void An_unknown_group_is_not_found()
    {
        var catalogue = CarCatalogue.FromJson(Database(), null);

        Assert.True(catalogue.TryFind("special", out _));
        Assert.False(catalogue.TryFind("nosuchgroup", out _));
    }
}
```

`CarInfoTests` needs a shared builder for this: add
`internal static CarInfo BuildDatabaseFor(params string[] codes)` to it, giving
each code the name `"Car " + code` and a one-byte prefix, and make the class
`public` so the sibling test file can call it.

- [ ] **Step 2: Run the tests and confirm they fail**

- [ ] **Step 3: Implement `CarCatalogue`**

`System.Text.Json` with a small DTO record; `JsonSerializerOptions` with
`PropertyNameCaseInsensitive = true` and `AllowTrailingCommas = true`, because
this is a file a person types by hand.

- [ ] **Step 4: Run the tests**

- [ ] **Step 5: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Merge custom car groups over the arcade ones"
```

---

### Task 4: Pick the class, then pick the car

**Files:**
- Modify: `patches/multiplayer/RoomState.cs`
- Modify: `patches/multiplayer/Session.cs`
- Modify: `patches/multiplayer/ModeHook.cs`
- Modify: `patches/multiplayer/MultiplayerPanel.cs`
- Modify: `tests/GT2Port.Tests/RoomStateTests.cs`
- Modify: `tests/GT2Port.Tests/SessionTests.cs`

**Interfaces:**
- Consumes: `CarCatalogue` (Task 3), `CarInfo` (Task 1), `VolArchive`.
- Produces: `Room` gains `CarGroup`; `Session.Host` takes it.

- [ ] **Step 1: Carry the group on the wire**

`Room` becomes:

```csharp
public record Room(Guid Id, string Name, string Track, string CarGroup, int MaxPlayers, IReadOnlyList<Player> Players);
```

In `RoomState`, raise `Version` to `2`, and write `CarGroup` immediately after
`Track` in `Serialise`, reading it in the same position in `TryDeserialise`. A
version 1 packet is rejected by the check that is already there.

Update the existing `RoomStateTests` for the new field, and add:

```csharp
    [Fact]
    public void Round_trips_the_car_group()
    {
        var room = new Room(Guid.NewGuid(), "Room", "2p_mountain", "special", 6,
            [new Player("ian", "dvpgn", true)]);

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));

        Assert.Equal("special", back.CarGroup);
        Assert.Equal("dvpgn", back.Players[0].Car);
    }

    [Fact]
    public void Rejects_a_packet_from_the_older_format()
    {
        var room = new Room(Guid.NewGuid(), "Room", "2p_mountain", "special", 6, []);
        var packet = RoomState.Serialise(room);
        packet[0] = 1;

        Assert.False(RoomState.TryDeserialise(packet, out _));
    }
```

The worst-case size test that already exists must be updated to include a
64-byte car group, and must still assert the packet fits one datagram.

- [ ] **Step 2: Let the host choose it**

`Session.Host(string roomName, string track, int maxPlayers)` gains a
`string carGroup` parameter, placed after `track`. It is stored on the room and
never changes for the room's life. Update every call site and every test that
calls `Host`.

- [ ] **Step 3: Build the catalogue once, in `ModeHook`**

Beside the existing `_archive` and `_courseMaps` statics, add a `CarInfo` and a
`CarCatalogue`, built on first lobby entry from `_archive` and from
`config/car-groups.json` relative to the working directory. Pass the catalogue to
the panel's constructor.

Follow whatever the existing statics do about disposal — do not invent a
disposal point that is not already there.

- [ ] **Step 4: The two screens**

In `DrawCreate`, under the course grid, add a car-group selector: the catalogue's
groups in order, one per row or one row of buttons, the chosen one marked, with a
label. Default to the first group. Choosing one sets the field passed to
`Session.Host`.

In `DrawLobby`, replace

```csharp
        if (ImGui.InputText("My car", ref _car, 32))
            _session.SetCar(_session.PlayerName, _car);
```

with a list of the room's group's cars, showing `catalogue.DisplayName(code)`,
the local player's current car marked, and clicking one calling
`_session.SetCar(_session.PlayerName, code)`. Requirements rather than a
transcription — write it to fit the file:

- The room's group comes from `catalogue.TryFind(room.CarGroup, out var group)`.
  When that fails, show the raw id and the message that this build does not have
  that group, and offer no cars. Do not throw and do not fall back to a different
  group.
- Ten to twenty-four items: a plain scrolling list, no search, no grid.
- The player list already renders `player.Car`; show `catalogue.DisplayName` of
  it there too, so the lobby reads as names throughout.
- Size the list from ImGui's own metrics, not from constants. The course grid was
  built with pixel constants and had to be redone when it broke at 150% display
  scale — do not repeat that.

- [ ] **Step 5: Build and run the tests**

```bash
dotnet build GT2Port.csproj
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

All must pass. Run the suite three times; the socket tests in this project are
the flaky-prone ones and a flake is a finding.

- [ ] **Step 6: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Pick a car class when hosting, and a car from it in the lobby"
```
