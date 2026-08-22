using System.Text.Json;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Applies cheat writes that target RAM the game fills at runtime.
///
/// Most of a cheat patches code, which a static recompilation must take before
/// translation - tools/apply_cht.py does that. What is left are writes into
/// data the game loads from GT2.VOL, which has no image to patch: the unit
/// strings ("mph" to "km/h") live there.
///
/// The cheat format makes this safe. Every write states the halfword it
/// expects to replace, so applying it repeatedly is idempotent: before the
/// data is loaded nothing matches and nothing happens, once loaded it is
/// rewritten exactly once, and if the game reloads that region it is caught
/// again on the next pass.
/// </summary>
public static class RuntimePatches
{
    readonly record struct Patch(uint Address, ushort Expect, ushort Value);

    static readonly List<Patch> _patches = [];
    static int _pending;

    public static void Load(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            int before = _patches.Count;
            foreach (var entry in root.GetProperty("patches").EnumerateArray())
            {
                _patches.Add(new Patch(
                    Convert.ToUInt32(entry.GetProperty("address").GetString(), 16),
                    Convert.ToUInt16(entry.GetProperty("expect").GetString(), 16),
                    Convert.ToUInt16(entry.GetProperty("value").GetString(), 16)));
            }
            string name = root.TryGetProperty("name", out var n) ? n.GetString()! : Path.GetFileName(file);
            Console.WriteLine($"[Patch] {name}: {_patches.Count - before} runtime write(s)");
        }
        _pending = _patches.Count;
    }

    /// <summary>Called once a frame. Stops scanning once everything has landed.</summary>
    public static void Apply(IMemory m)
    {
        if (_pending == 0) return;
        int remaining = 0;
        foreach (var patch in _patches)
        {
            ushort current = m.ReadU16(patch.Address);
            if (current == patch.Expect)
            {
                m.WriteU16(patch.Address, patch.Value);
                continue;
            }
            if (current != patch.Value) remaining++;
        }
        if (remaining != _pending)
        {
            _pending = remaining;
            if (remaining == 0) Console.WriteLine("[Patch] all runtime writes applied");
        }
    }
}
