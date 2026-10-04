using System.Globalization;

namespace VeliShell.Core;

/// <summary>A strict SemVer 2.0.0 value used for release ordering.</summary>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch, string? preRelease = null, string? buildMetadata = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
            throw new ArgumentOutOfRangeException(nameof(major), "Version numbers cannot be negative.");
        if (preRelease is not null && !IdentifiersAreValid(preRelease, numericLeadingZeroAllowed: false))
            throw new ArgumentException("Invalid SemVer prerelease identifier.", nameof(preRelease));
        if (buildMetadata is not null && !IdentifiersAreValid(buildMetadata, numericLeadingZeroAllowed: true))
            throw new ArgumentException("Invalid SemVer build metadata.", nameof(buildMetadata));

        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }
    public bool IsPrerelease => PreRelease is not null;

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new FormatException($"'{value}' is not a valid SemVer 2.0.0 version.");

    public static bool TryParse(string? value, out SemanticVersion result)
    {
        result = default;
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;

        var source = value[0] is 'v' or 'V' ? value[1..] : value;
        if (source.Length == 0) return false;

        string? build = null;
        var buildIndex = source.IndexOf('+');
        if (buildIndex >= 0)
        {
            if (source.IndexOf('+', buildIndex + 1) >= 0) return false;
            build = source[(buildIndex + 1)..];
            source = source[..buildIndex];
            if (!IdentifiersAreValid(build, numericLeadingZeroAllowed: true)) return false;
        }

        string? preRelease = null;
        var preReleaseIndex = source.IndexOf('-');
        if (preReleaseIndex >= 0)
        {
            preRelease = source[(preReleaseIndex + 1)..];
            source = source[..preReleaseIndex];
            if (!IdentifiersAreValid(preRelease, numericLeadingZeroAllowed: false)) return false;
        }

        var parts = source.Split('.');
        if (parts.Length != 3 ||
            !TryParseCoreNumber(parts[0], out var major) ||
            !TryParseCoreNumber(parts[1], out var minor) ||
            !TryParseCoreNumber(parts[2], out var patch))
            return false;

        result = new SemanticVersion(major, minor, patch, preRelease, build);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = long.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric) comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) comparison = -1;
            else if (rightNumeric) comparison = 1;
            else comparison = string.CompareOrdinal(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null) value += "-" + PreRelease;
        if (BuildMetadata is not null) value += "+" + BuildMetadata;
        return value;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParseCoreNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 &&
               (value.Length == 1 || value[0] != '0') &&
               value.All(character => character is >= '0' and <= '9') &&
               int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IdentifiersAreValid(string value, bool numericLeadingZeroAllowed)
    {
        if (value.Length == 0) return false;
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 ||
                identifier.Any(character => !(character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-')))
                return false;
            if (!numericLeadingZeroAllowed && identifier.Length > 1 && identifier[0] == '0' &&
                identifier.All(character => character is >= '0' and <= '9'))
                return false;
        }
        return true;
    }
}
