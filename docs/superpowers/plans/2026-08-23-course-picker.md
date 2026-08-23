# Course Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the room-creation screen's track text box with a grid of the game's own two-player courses, each drawn with the course map the disc ships.

**Architecture:** The roster is generated at build time from the two-player course tables in `gt2_03` into a committed C# file. The maps are read at runtime out of `GT2.VOL` — a GTFS archive on the disc — decoded from TIM to RGBA and uploaded as GL textures the first time each cell is drawn.

**Tech Stack:** C# / .NET 10, ImGuiNET, Silk.NET GL (through `HostWindow.UploadTexture`), xunit. Python 3 for the generator.

## Global Constraints

- Target framework `net10.0`.
- New code only under `patches/multiplayer/`, `tools/`, and `tests/GT2Port.Tests/`.
- **Nothing under `generated/` or the `RecompOne/` submodule may change.** Everything needed from the runtime is already public: `DiscFs.Locate`, `DiscFs.ReadSector`, `HostWindow.UploadTexture`.
- The disc image is **not** in the repository. No test may depend on it.
- Course maps are 96×96, 4bpp, 16-colour CLUT. The roster is 27 courses: 21 tarmac, 6 dirt.
- A missing or malformed asset degrades that one cell to name-only. The lobby must never fail to open because a picture is missing.
- `Room.Track` carries the asset code (`"2p_mountain"`), never the display name.

---

### Task 1: Read a file out of GT2.VOL

**Files:**
- Create: `patches/multiplayer/VolArchive.cs`
- Test: `tests/GT2Port.Tests/VolArchiveTests.cs`

**Interfaces:**
- Consumes: `RecompOne.Runtime.Cdrom.DiscFs` — `DiscFs.Open(string path)`, `bool Locate(string name, out int lba, out uint size)`, `byte[] ReadSector(int lba)` returning 2048 bytes.
- Produces:
  - `public sealed class VolArchive : IDisposable`
  - `public static VolArchive? TryOpen(string discPath)` — null when the disc cannot be opened or carries no `GT2.VOL`. Never throws.
  - `public bool TryRead(string path, out byte[] data)` — `path` is slash-separated, e.g. `"crsmap/2p_mountain.tim.gz"`. False for anything missing or malformed. Never throws. A member whose name ends `.gz` is gunzipped; anything else is returned raw.
  - `public IReadOnlyList<string> Entries(string directory)` — names in one directory, `".."` excluded. Empty for a path that is not a directory.

**The format**, which the reader must implement exactly:

```
0x00   'GTFS'
0x04   0
0x08   build timestamp
0x0C   0
0x10   a count this reader does not use
0x14   u32[] offsets, increasing. The run ENDS at the first value that does
       not increase. Each entry is a file's END.
then padded to the next 2048 boundary:
       32-byte entries:
         +0  u32 timestamp
         +4  u16 value
         +6  u8  flags: 0x01 directory, 0x80 last entry of this directory
         +7  name, NUL-padded, up to 25 bytes
```

A directory's `value` is the entry index its listing starts at; the listing runs
until an entry carrying `0x80`. Entry 0 begins the root listing. Every
directory's listing opens with `".."`.

A file's `value` indexes the offset table. Its bytes run
`offsets[value - 1] .. offsets[value]` — and **every file starts on its own 2 KB
sector**, with `offsets[value - 1]` landing inside that sector rather than on it.
So the read starts at `offsets[value - 1] / 2048 * 2048` and runs
`offsets[value] - offsets[value - 1]` bytes. This was verified by decompressing
all 5114 gzip members of the real archive, sizes from 652 bytes to 336 KB.

- [ ] **Step 1: Write the failing tests**

Tests build a GTFS image in memory rather than touching a disc. Write this
helper at the top of `VolArchiveTests.cs` and use it for every case:

```csharp
using System.IO.Compression;
using System.Text;
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class VolArchiveTests
{
    const int Sector = 2048;

    /// <summary>One entry to place in a built archive.</summary>
    sealed record Item(string Name, byte[]? Payload, int FirstChild = -1, bool Last = false);

    /// <summary>
    /// Builds a GTFS image byte for byte the way the disc stores one, so the
    /// reader is tested against the real layout rather than against a
    /// convenient one. Files land in the order given, each on its own sector.
    /// </summary>
    static byte[] BuildArchive(IReadOnlyList<Item> entries)
    {
        var files = entries.Where(e => e.Payload is not null).ToList();

        // Offset table stores each file's END, and consecutive files sit on
        // consecutive sector boundaries, so a file's end is its start plus its
        // length, not its start plus a whole sector.
        var offsets = new List<uint> { 0 };
        int sector = 0;
        var startSector = new Dictionary<string, int>();
        foreach (var f in files)
        {
            startSector[f.Name] = sector;
            offsets.Add((uint)(sector * Sector + f.Payload!.Length));
            sector += (f.Payload.Length + Sector - 1) / Sector;
        }

        int tableBytes = 0x14 + offsets.Count * 4;
        int entryBase = (tableBytes + Sector - 1) / Sector * Sector;
        int dataBase = entryBase + (entries.Count * 32 + Sector - 1) / Sector * Sector;

        var image = new byte[dataBase + sector * Sector];
        Encoding.ASCII.GetBytes("GTFS").CopyTo(image, 0);
        for (int i = 0; i < offsets.Count; i++)
            BitConverter.GetBytes(offsets[i]).CopyTo(image, 0x14 + i * 4);
        // The run must stop increasing so the reader knows where it ends.
        BitConverter.GetBytes(0u).CopyTo(image, 0x14 + offsets.Count * 4);

        int fileIndex = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int at = entryBase + i * 32;
            ushort value = e.Payload is null ? (ushort)e.FirstChild : (ushort)(++fileIndex);
            BitConverter.GetBytes(value).CopyTo(image, at + 4);
            image[at + 6] = (byte)((e.Payload is null && e.FirstChild >= 0 ? 0x01 : 0x00) | (e.Last ? 0x80 : 0x00));
            Encoding.ASCII.GetBytes(e.Name).CopyTo(image, at + 7);
            if (e.Payload is not null)
                e.Payload.CopyTo(image, dataBase + startSector[e.Name] * Sector);
        }
        return image;
    }

    static byte[] Gzip(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(plain);
        return ms.ToArray();
    }
}
```

`VolArchive` therefore needs a seam that takes an image without a disc:
`internal static VolArchive FromImage(byte[] image)` and
`internal static VolArchive? FromImageOrNull(byte[] image)`, both reading
sectors straight out of the array. `TryOpen` uses the same core over a
`DiscFs`. `InternalsVisibleTo("GT2Port.Tests")` is already declared in
`GT2Port.csproj`.

A directory's stored value is the index of the entry its listing **starts at**,
and that first entry is the `".."` link - confirmed on the real archive, where
`arcade` carries 30 and entry 30 is `".."`. The fixture above follows that, so
a reader that skips the parent link by starting one entry late will fail these
tests.

Cases to cover, each asserting a positive outcome before any negative one:

```csharp
    [Fact]
    public void Reads_a_file_from_the_root()
    {
        var payload = Encoding.ASCII.GetBytes("course table");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("readme", out var data));
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Reads_a_file_from_a_directory()
    {
        var payload = Encoding.ASCII.GetBytes("a course map");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("crsmap", null, 2, Last: true),
            new Item("..", null, 0),
            new Item("2p_mountain.tim", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("crsmap/2p_mountain.tim", out var data));
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Gunzips_a_member_whose_name_says_so()
    {
        var plain = Encoding.ASCII.GetBytes("the decompressed course map");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("map.tim.gz", Gzip(plain), Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("map.tim.gz", out var data));
        Assert.Equal(plain, data);
    }

    [Fact]
    public void Reads_a_file_that_spans_several_sectors()
    {
        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("big", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("big", out var data));
        Assert.Equal(payload, data);
    }

    [Fact]
    public void Reads_the_file_after_one_that_did_not_fill_its_last_sector()
    {
        // The rule this pins: a file's stored offset is its unpadded end, but
        // the next file still begins on the next whole sector. Reading the
        // second file from its unrounded offset returns the tail of the first.
        var first = new byte[3000];
        var second = Encoding.ASCII.GetBytes("second file");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("first", first, Last: false),
            new Item("second", second, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("second", out var data));
        Assert.Equal(second, data);
    }

    [Fact]
    public void Lists_a_directory_without_its_parent_link()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("crsmap", null, 2, Last: true),
            new Item("..", null, 0),
            new Item("a.tim", [1], Last: false),
            new Item("b.tim", [2], Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.Equal(new[] { "a.tim", "b.tim" }, vol.Entries("crsmap"));
    }

    [Fact]
    public void Refuses_a_path_that_is_not_there()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", [1], Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("readme", out _));          // the archive works
        Assert.False(vol.TryRead("crsmap/nothing", out _));  // and still says no
    }

    [Fact]
    public void Refuses_an_image_that_is_not_an_archive()
    {
        Assert.Null(VolArchive.FromImageOrNull(new byte[Sector]));
    }

    [Fact]
    public void Refuses_a_truncated_archive_without_throwing()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", Encoding.ASCII.GetBytes("hello"), Last: true),
        ]);
        var cut = image[..(image.Length - Sector)];
        using var vol = VolArchive.FromImage(cut);

        Assert.False(vol.TryRead("readme", out _));
    }
```

`FromImage` returns an archive over a byte array (throwing only if the caller
hands it something that is not GTFS at all); `FromImageOrNull` returns null in
that case. Both are internal.

- [ ] **Step 2: Run the tests and confirm they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: build failure, `VolArchive` does not exist. Record the output.

- [ ] **Step 3: Implement `VolArchive`**

Follow `patches/multiplayer/LanDiscovery.cs` for conventions: `sealed`, XML doc
saying why the class exists, every public member safe after `Dispose`, and no
exceptions escaping a `Try*` method.

Read the offset table in blocks rather than assuming a length, stopping at the
first value that does not increase. Cache directory listings; do not cache file
contents — the caller decides what to keep.

`TryOpen` opens its own `DiscFs`. That is a second handle on the disc image
while the game holds one, which is fine: the runtime opens it with
`File.OpenRead`, so shared reading is allowed.

- [ ] **Step 4: Run the tests**

All must pass — the 146 that exist plus the ones added here.

- [ ] **Step 5: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Read files out of GT2.VOL at runtime"
```

---

### Task 2: Decode a TIM

**Files:**
- Create: `patches/multiplayer/Tim.cs`
- Test: `tests/GT2Port.Tests/TimTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class Tim` with
  `public static bool TryDecode(byte[] data, out int width, out int height, out byte[] rgba)`.
  `rgba` is `width * height * 4` bytes, RGBA8. False for anything it cannot
  decode. Never throws.

**The format:**

```
+0  u32 magic, always 0x10
+4  u32 flags: bits 0-1 depth (0 = 4bpp, 1 = 8bpp, 2 = 16bpp, 3 = 24bpp)
                bit 3 CLUT present
then, if bit 3:
    u32 byte length of this block (including the length itself)
    u16 vram x, u16 vram y, u16 entries wide, u16 entries high
    that many u16 colours
then:
    u32 byte length of this block (including the length itself)
    u16 vram x, u16 vram y, u16 words per row, u16 rows
    the pixels
```

A row is `words * 2` bytes. At 4bpp that is `words * 4` pixels, low nibble
first; at 8bpp, `words * 2` pixels. A colour is 5 bits per channel in BGR
order — `r = (c & 31) << 3`, `g = (c >> 5 & 31) << 3`, `b = (c >> 10 & 31) << 3`
— and a colour whose low 15 bits are zero with bit 15 clear is TIM's
transparent black, so its alpha is 0. Everything else is opaque.

Only depth 0 and 1 are handled. Depth 2 and 3 return false rather than guessing:
no course map uses them.

- [ ] **Step 1: Write the failing tests**

```csharp
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class TimTests
{
    /// <summary>A TIM with one CLUT and one image block, built field by field.</summary>
    static byte[] Build(uint depth, ushort[] clut, ushort words, ushort rows, byte[] pixels)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x10u);
        w.Write(depth | 8u);
        w.Write((uint)(12 + clut.Length * 2));
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)clut.Length); w.Write((ushort)1);
        foreach (var c in clut) w.Write(c);
        w.Write((uint)(12 + pixels.Length));
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(words); w.Write(rows);
        w.Write(pixels);
        return ms.ToArray();
    }

    static ushort Colour(int r, int g, int b) => (ushort)((r >> 3) | (g >> 3) << 5 | (b >> 3) << 10);

    [Fact]
    public void Decodes_a_4bpp_image_low_nibble_first()
    {
        // Two pixels: index 1 then index 2, in a row of four.
        var clut = new ushort[16];
        clut[1] = Colour(248, 0, 0);
        clut[2] = Colour(0, 248, 0);
        var tim = Build(0, clut, words: 1, rows: 1, pixels: [0x21, 0x00]);

        Assert.True(Tim.TryDecode(tim, out int width, out int height, out var rgba));

        Assert.Equal(4, width);
        Assert.Equal(1, height);
        Assert.Equal([248, 0, 0, 255], rgba[0..4]);
        Assert.Equal([0, 248, 0, 255], rgba[4..8]);
    }

    [Fact]
    public void Decodes_an_8bpp_image()
    {
        var clut = new ushort[256];
        clut[7] = Colour(0, 0, 248);
        var tim = Build(1, clut, words: 1, rows: 1, pixels: [7, 0]);

        Assert.True(Tim.TryDecode(tim, out int width, out int height, out var rgba));

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal([0, 0, 248, 255], rgba[0..4]);
    }

    [Fact]
    public void Treats_black_with_the_flag_clear_as_transparent()
    {
        var clut = new ushort[16];
        clut[0] = 0;
        clut[1] = Colour(248, 248, 248);
        var tim = Build(0, clut, words: 1, rows: 1, pixels: [0x10, 0x00]);

        Assert.True(Tim.TryDecode(tim, out _, out _, out var rgba));

        Assert.Equal(0, rgba[3]);     // index 0 is see-through
        Assert.Equal(255, rgba[7]);   // index 1 is not
    }

    [Fact]
    public void Refuses_a_depth_it_does_not_handle()
    {
        var tim = Build(2, new ushort[16], words: 1, rows: 1, pixels: [0, 0]);

        Assert.False(Tim.TryDecode(tim, out _, out _, out _));
    }

    [Fact]
    public void Refuses_something_that_is_not_a_tim()
    {
        Assert.False(Tim.TryDecode([1, 2, 3, 4], out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_truncated_tim_without_throwing()
    {
        var clut = new ushort[16];
        clut[1] = Colour(248, 0, 0);
        var tim = Build(0, clut, words: 4, rows: 4, pixels: new byte[32]);

        Assert.True(Tim.TryDecode(tim, out _, out _, out _));       // whole file decodes
        Assert.False(Tim.TryDecode(tim[..(tim.Length - 8)], out _, out _, out _));
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

- [ ] **Step 3: Implement `Tim`**

Bounds-check before every read, the way `RoomState.TryDeserialise` does. A
palette index past the end of the CLUT is malformed input, not a crash.

- [ ] **Step 4: Run the tests**

- [ ] **Step 5: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Decode the disc's paletted TIMs"
```

---

### Task 3: Generate the course roster from the game's own tables

**Files:**
- Create: `tools/gen_course_table.py`
- Create (generated, committed): `patches/multiplayer/CourseTable.cs`
- Test: `tests/GT2Port.Tests/CourseTableTests.cs`

**Interfaces:**
- Consumes: `ovl_bin/gt2_03.bin`, the decompressed overlay image already in the repository.
- Produces:
  - `public enum CourseSurface { Tarmac, Dirt }`
  - `public sealed record Course(string Code, string Name, CourseSurface Surface)`
  - `public static class CourseTable` with
    `public static IReadOnlyList<Course> All { get; }`,
    `public static bool TryFind(string code, out Course course)`,
    `public static string DisplayName(string code)` — the code itself when unknown.

**How the generator finds the tables.** `gt2_03` loads at `0x80010000`, so a
pointer `p` is at file offset `p - 0x80010000`. Records are 32 bytes:

```
+0x04  u32 class
+0x08  u32 index
+0x10  char* asset code
+0x14  char* display name
```

The two-player asset codes live in one contiguous block of NUL-terminated,
4-byte-aligned strings. Locate it by finding `2p_seattle` and walking outward
while the bytes stay printable-and-NUL — do not hardcode `0x40448`. Then scan
every 4-byte-aligned position in the image for a u32 pointing into that block,
treat each hit as a record's `+0x10`, and group hits into runs whose neighbours
are 32 bytes apart.

Exactly two runs are expected: 21 records and 6. The 6-record run is dirt. If the
scan finds any other shape, **fail with a message naming what it found** — a
generator that silently emits a short table is worse than one that stops.

Emit courses in the order the tables store them.

- [ ] **Step 1: Write the generator**

```python
#!/usr/bin/env python3
"""Emit the two-player course roster from the tables inside gt2_03.

The game carries, as arrays of 32-byte records, both the asset code and the
display name of every course its two-player mode offers. Taking the roster from
there rather than typing it out is what stops it drifting - and it settles
pairings nobody would guess: speed2p is Super Speedway, and Grindelwald's
two-player asset is called Gtest.

Run by hand after changing the overlay images, and commit the result:

    python tools/gen_course_table.py
"""
import struct
import sys

OVERLAY = 'ovl_bin/gt2_03.bin'
OUTPUT = 'patches/multiplayer/CourseTable.cs'
BASE = 0x80010000
RECORD = 32
CODE_PTR = 0x10
NAME_PTR = 0x14
ANCHOR = b'2p_seattle\x00'


def string_at(data, offset):
    end = data.index(b'\x00', offset)
    return data[offset:end].decode('ascii')


def code_block(data):
    """The span of the two-player asset-code strings, found rather than assumed."""
    anchor = data.find(ANCHOR)
    if anchor < 0:
        raise SystemExit(f'{OVERLAY}: the two-player course codes are not in this image')

    def printable(i):
        return 32 <= data[i] < 127 or data[i] == 0

    start = anchor
    while start > 0 and printable(start - 1):
        start -= 1
    end = anchor
    while end < len(data) and printable(end):
        end += 1
    return start, end


def records(data, lo, hi):
    """Every 32-byte record whose code pointer lands between lo and hi."""
    hits = []
    for at in range(0, len(data) - 3, 4):
        pointer = struct.unpack_from('<I', data, at)[0]
        if lo <= pointer - BASE < hi:
            hits.append(at - CODE_PTR)

    runs = []
    for at in hits:
        if runs and at - runs[-1][-1] == RECORD:
            runs[-1].append(at)
        else:
            runs.append([at])
    return runs


def read(data, run):
    out = []
    for at in run:
        code = string_at(data, struct.unpack_from('<I', data, at + CODE_PTR)[0] - BASE)
        name = string_at(data, struct.unpack_from('<I', data, at + NAME_PTR)[0] - BASE)
        out.append((code, name))
    return out


def main():
    data = open(OVERLAY, 'rb').read()
    lo, hi = code_block(data)
    runs = [r for r in records(data, lo, hi) if len(r) > 1]
    sizes = sorted(len(r) for r in runs)
    if sizes != [6, 21]:
        raise SystemExit(
            f'{OVERLAY}: expected two-player tables of 21 and 6 records, found {sizes}. '
            'The overlay changed shape - read it before touching this script.')

    dirt_run = next(r for r in runs if len(r) == 6)
    tarmac_run = next(r for r in runs if len(r) == 21)
    courses = ([(c, n, 'Tarmac') for c, n in read(data, tarmac_run)]
               + [(c, n, 'Dirt') for c, n in read(data, dirt_run)])

    rows = '\n'.join(
        f'        new Course("{code}", "{name}", CourseSurface.{surface}),'
        for code, name, surface in courses)

    with open(OUTPUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write(f'''// Generated by tools/gen_course_table.py from {OVERLAY}. Do not edit by hand.
//
// These are the game's own two-player course tables: {len(tarmac_run)} tarmac and
// {len(dirt_run)} dirt, with the asset code and display name it pairs them with.

namespace GT2Port.Multiplayer;

public enum CourseSurface {{ Tarmac, Dirt }}

public sealed record Course(string Code, string Name, CourseSurface Surface);

public static class CourseTable
{{
    public static IReadOnlyList<Course> All {{ get; }} =
    [
{rows}
    ];

    static readonly Dictionary<string, Course> ByCode =
        All.ToDictionary(c => c.Code, StringComparer.Ordinal);

    public static bool TryFind(string code, out Course course) =>
        ByCode.TryGetValue(code, out course!);

    /// <summary>
    /// The name to show for a course code. An unknown code returns itself
    /// rather than an empty string: a room announced from a disc revision this
    /// build does not know should read as something odd, not as nothing.
    /// </summary>
    public static string DisplayName(string code) =>
        ByCode.TryGetValue(code, out var course) ? course.Name : code;
}}
''')
    print(f'{len(courses)} courses -> {OUTPUT}')


if __name__ == '__main__':
    main()
```

- [ ] **Step 2: Run it**

```bash
python tools/gen_course_table.py
```

Expected: `27 courses -> patches/multiplayer/CourseTable.cs`. Read the generated
file and confirm it contains `2p_mountain` as `Trial Mountain Circuit`,
`speed2p` as `Super Speedway`, `Gtest` as `Grindelwald`, and that `pikes_2p_rev`
is `Dirt`.

- [ ] **Step 3: Write the tests**

These pin the generator's output so a regeneration that produces nothing, or
half a table, fails the build rather than shipping.

```csharp
using GT2Port.Multiplayer;

namespace GT2Port.Tests;

public class CourseTableTests
{
    [Fact]
    public void Holds_the_whole_two_player_roster()
    {
        Assert.Equal(27, CourseTable.All.Count);
        Assert.Equal(21, CourseTable.All.Count(c => c.Surface == CourseSurface.Tarmac));
        Assert.Equal(6, CourseTable.All.Count(c => c.Surface == CourseSurface.Dirt));
    }

    [Fact]
    public void Every_course_has_a_unique_code_and_a_name()
    {
        Assert.Equal(CourseTable.All.Count, CourseTable.All.Select(c => c.Code).Distinct().Count());
        Assert.All(CourseTable.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Theory]
    [InlineData("2p_mountain", "Trial Mountain Circuit")]
    [InlineData("speed2p", "Super Speedway")]
    [InlineData("Gtest", "Grindelwald")]
    public void Pairs_the_codes_the_game_pairs(string code, string name)
    {
        Assert.Equal(name, CourseTable.DisplayName(code));
    }

    [Fact]
    public void An_unknown_code_shows_as_itself()
    {
        Assert.False(CourseTable.TryFind("not_a_course", out _));
        Assert.Equal("not_a_course", CourseTable.DisplayName("not_a_course"));
    }
}
```

- [ ] **Step 4: Run the tests**

- [ ] **Step 5: Commit**

```bash
git add tools patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Generate the two-player course roster from the game's tables"
```

---

### Task 4: The picker

**Files:**
- Create: `patches/multiplayer/CourseMaps.cs`
- Modify: `patches/multiplayer/MultiplayerPanel.cs`
- Modify: `patches/multiplayer/ModeHook.cs`
- Modify: `tests/GT2Port.Tests/SessionTests.cs` (the panel's constructor changes)

**Interfaces:**
- Consumes: `VolArchive` (Task 1), `Tim` (Task 2), `CourseTable` (Task 3),
  `RecompOne.Runtime.Host.Window.HostWindow.UploadTexture(byte[] rgba, int width, int height)`
  returning a GL texture id, or 0 on failure.
- Produces:
  - `public sealed class CourseMaps : IDisposable`
  - `public CourseMaps(Func<VolArchive?> archive)`
  - `public uint TextureFor(string code)` — 0 when there is no map to show.
    Decodes and uploads on first use, then remembers. Remembers failures too, so
    a missing map is not re-read every frame.

- [ ] **Step 1: Implement `CourseMaps`**

```csharp
namespace GT2Port.Multiplayer;

/// <summary>
/// The course outline the disc ships for each course, ready to draw.
///
/// Read on first use and kept: the grid draws the same 27 pictures every frame,
/// and re-reading a sector off the disc image for each of them would be absurd.
/// A course whose map is missing or malformed is remembered as having none, so
/// a broken asset costs one read rather than one per frame.
/// </summary>
public sealed class CourseMaps : IDisposable
{
    readonly Func<VolArchive?> _archive;
    readonly Dictionary<string, uint> _textures = new(StringComparer.Ordinal);
    bool _disposed;

    public CourseMaps(Func<VolArchive?> archive) => _archive = archive;

    public uint TextureFor(string code)
    {
        if (_disposed) return 0;
        if (_textures.TryGetValue(code, out var known)) return known;

        uint texture = 0;
        if (_archive() is { } vol
            && vol.TryRead($"crsmap/{code}.tim.gz", out var tim)
            && Tim.TryDecode(tim, out int width, out int height, out var rgba))
        {
            texture = RecompOne.Runtime.Host.Window.HostWindow.UploadTexture(rgba, width, height);
        }

        _textures[code] = texture;
        return texture;
    }

    public void Dispose() => _disposed = true;
}
```

- [ ] **Step 2: Open the archive once, in `ModeHook`**

Alongside the existing statics, add a lazily opened `VolArchive` and a
`CourseMaps` over it, built the first time the lobby is entered and passed to
the panel's constructor. The archive comes from
`RecompOne.Runtime.Runtime.CdPath`, and `VolArchive.TryOpen` returns null when
that path is unusable — which the panel already copes with, because
`TextureFor` then returns 0 for everything.

Dispose both where `LanDiscovery` is disposed.

- [ ] **Step 3: Replace the track text box with the grid**

In `MultiplayerPanel`, take a `CourseMaps` as a fourth constructor argument.
Replace the `_track` string's initial value with the first tarmac course's code
(`CourseTable.All[0].Code`), and replace this line in `DrawCreate`:

```csharp
        ImGui.InputText("Track", ref _track, 32);
```

with a grid. Requirements, not a transcription — write it to fit the file:

- One cell per course, in `CourseTable.All` order, under a `Tarmac` heading then
  a `Dirt` heading. Wrap cells to the window's width.
- A cell is the 96×96 map with the display name beneath it. Clicking the cell
  selects that course, setting `_track` to its **code**.
- The selected cell is outlined. Nothing else marks it.
- Draw the map with `ImGui.Image` tinted by the current text colour — the maps
  are line art on transparency, and untinted dark lines vanish on a dark theme.
- `TextureFor` returning 0 means draw the name alone in a cell of the same size,
  so the grid does not reflow when one map is missing.
- Put the grid in a scrolling child region so 27 cells cannot push the Create and
  Cancel buttons off the screen.

In `DrawLobby` and `DrawRoomList`, show `CourseTable.DisplayName(room.Track)`
rather than `room.Track`, so the wire carries the code and the screen shows the
name.

- [ ] **Step 4: Fix up the panel's other callers**

`tests/GT2Port.Tests/SessionTests.cs` constructs `MultiplayerPanel` in three
places. Pass a `CourseMaps` over a `() => null` archive: with no archive every
`TextureFor` returns 0 without touching GL, which is what a test process needs.

- [ ] **Step 5: Build and run the tests**

```bash
dotnet build GT2Port.csproj
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

All must pass. Run the suite three times — the socket tests in this project are
the flaky-prone ones and a flake is a finding.

- [ ] **Step 6: Commit**

```bash
git add patches tests && git -c user.email=ianitsky@gmail.com -c user.name=inmor commit -m "Pick the track from a grid of the game's own course maps"
```
