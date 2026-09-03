namespace GT2Port.Rendezvous;

/// <summary>
/// The six characters that stand for a room nobody listed.
///
/// A room's real name is a <see cref="Guid"/>, which is the right thing for a
/// wire and the wrong thing for a person: nobody reads one out over a call.
/// The code is what a person carries between two screens.
///
/// The alphabet leaves out 0, 1, I and O. Not for tidiness - because a code
/// that can be mistyped into a *different valid code* puts somebody in a
/// stranger's room, while one that cannot be mistyped into anything valid puts
/// them back in the box with a message. Thirty-two characters over six places
/// is still a thousand million codes, which is far more than a hobby server
/// will ever hold at once.
/// </summary>
public static class RoomCode
{
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int Length = 6;

    public static string Next(Random random)
    {
        Span<char> code = stackalloc char[Length];
        for (int i = 0; i < Length; i++) code[i] = Alphabet[random.Next(Alphabet.Length)];
        return new string(code);
    }

    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Length) return false;
        foreach (char c in code)
            if (!Alphabet.Contains(c)) return false;
        return true;
    }

    /// <summary>
    /// What was typed, as the wire wants it: upper case, and without the
    /// spaces and dashes people put through anything six characters long.
    ///
    /// This does not judge - it tidies. Whatever comes out still has to pass
    /// <see cref="IsWellFormed"/>, so "hello there" tidies to something that is
    /// then refused rather than sent.
    /// </summary>
    public static string Tidy(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";

        var kept = new System.Text.StringBuilder(Length);
        foreach (char c in typed.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '_') continue;
            kept.Append(c);
        }
        return kept.ToString();
    }
}
