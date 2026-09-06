namespace GT2Port;

/// <summary>
/// Finds a file the port ships with, wherever it is being run from.
///
/// The port read these by a path relative to the working directory, which is
/// the repository root under `dotnet run` and is whatever a person happened to
/// be in when they double-clicked the .exe. On the machine it was developed on
/// those are the same place, and on every other machine they are not: the first
/// person to run the built .exe got a race with no parameters, an empty race
/// record, and a jump to 0x051C9444 - which is not an address.
///
/// So a file is looked for beside the executable first, which is where the
/// build puts it and the one location a shipped copy can rely on, and in the
/// working directory second, which is where a repository has it. When neither
/// has it the relative path comes back anyway, so whatever reports the failure
/// names something a person can go and look for.
/// </summary>
public static class GameFiles
{
    public static string Find(params string[] parts)
    {
        string relative = Path.Combine(parts);

        string besideTheExe = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(besideTheExe)) return besideTheExe;

        return relative;
    }

    /// <summary>
    /// The same, for a directory. A working directory is whatever launched the
    /// game - a shortcut's, a shell's - and only the folder the executable sits
    /// in is somewhere the game's own files are known to be.
    /// </summary>
    public static string FindDirectory(params string[] parts)
    {
        string relative = Path.Combine(parts);

        string besideTheExe = Path.Combine(AppContext.BaseDirectory, relative);
        if (Directory.Exists(besideTheExe)) return besideTheExe;

        return relative;
    }
}
