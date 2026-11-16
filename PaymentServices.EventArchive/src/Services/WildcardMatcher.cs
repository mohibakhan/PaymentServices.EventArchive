namespace PaymentServices.EventArchive.Services;

/// <summary>
/// Case-insensitive matching for search filters. Without a '*' the pattern
/// must equal the value; '*' matches any run of characters (including none),
/// e.g. <c>ReceiveCredit*</c>, <c>*Failure</c>, <c>*Credit*</c>.
/// </summary>
public static class WildcardMatcher
{
    public static bool IsMatch(string? value, string pattern)
    {
        if (value is null)
        {
            return false;
        }

        if (!pattern.Contains('*'))
        {
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        return Glob(value, pattern);
    }

    /// <summary>Linear-time glob match (no regex, so no pathological patterns).</summary>
    private static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        var t = 0;
        var p = 0;
        var star = -1;
        var mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length &&
                     char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                t++;
                p++;
            }
            else if (star != -1)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
