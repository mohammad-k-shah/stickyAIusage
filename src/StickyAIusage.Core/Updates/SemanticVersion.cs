using System.Globalization;

namespace StickyAIusage.Updates;

/// <summary>
/// A release version as Semantic Versioning 2.0 orders it: "2.0.1", "v2.0.0-beta.1".
/// A leading "v" (the tags) and build metadata after "+" (the commit the SDK
/// stamps into a build's informational version) are allowed and ignored.
/// </summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease)
    : IComparable<SemanticVersion>
{
    /// <summary>"2.0.0-beta.1" and the like: published as a pre-release.</summary>
    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>"2.0.0", "2.0.0-beta.1" (no "v").</summary>
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : "");

    /// <summary>The version, or null when the text isn't one (a tag such as "latest",
    /// "2.0", "v2.0.0.1", a leading zero, an empty pre-release part).</summary>
    public static SemanticVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(value[(plus + 1)..], numericRule: false))
                return null;
            value = value[..plus];
        }

        IReadOnlyList<string> preRelease = [];
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            var tail = value[(dash + 1)..];
            if (!ValidIdentifiers(tail, numericRule: true))
                return null;
            preRelease = tail.Split('.');
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length != 3)
            return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!IsNumber(parts[i]) || (parts[i].Length > 1 && parts[i][0] == '0') ||
                !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);
    }

    /// <summary>Dot-separated identifiers of [0-9A-Za-z-]; pre-release numbers have no leading zero.</summary>
    private static bool ValidIdentifiers(string text, bool numericRule)
    {
        if (text.Length == 0)
            return false;
        foreach (var identifier in text.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;
            if (numericRule && IsNumber(identifier) && identifier.Length > 1 && identifier[0] == '0')
                return false;
        }
        return true;
    }

    private static bool IsNumber(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    /// <summary>Semantic Versioning's precedence: numbers first; a pre-release comes before
    /// its release; pre-release parts compare one by one (numbers as numbers and before
    /// words, words by ASCII), and a shorter list that agrees so far comes first.</summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        var byNumbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumbers != 0)
            return byNumbers;
        if (IsPreRelease != other.IsPreRelease)
            return IsPreRelease ? -1 : 1;
        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var mine = PreRelease[i];
            var theirs = other.PreRelease[i];
            var mineNumber = IsNumber(mine);
            var theirsNumber = IsNumber(theirs);
            int byPart;
            if (mineNumber && theirsNumber)
                byPart = mine.Length != theirs.Length ? mine.Length.CompareTo(theirs.Length) : string.CompareOrdinal(mine, theirs);
            else if (mineNumber != theirsNumber)
                byPart = mineNumber ? -1 : 1;
            else
                byPart = string.CompareOrdinal(mine, theirs);
            if (byPart != 0)
                return Math.Sign(byPart);
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    // Records compare lists by reference: equality follows the precedence instead
    // (build metadata is already gone).
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, ToString());
}
