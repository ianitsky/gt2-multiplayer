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
            texture = RecompOne.Runtime.Host.HostWindow.UploadTexture(rgba, width, height);
        }

        _textures[code] = texture;
        return texture;
    }

    public void Dispose() => _disposed = true;
}
