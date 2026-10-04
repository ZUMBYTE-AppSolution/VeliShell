namespace VeliShell.Core;

/// <summary>
/// Parses the intentionally narrow command used by the Explorer static verb.
/// The Shell already separates quoted command-line arguments before WPF exposes
/// them, so this class never reparses or concatenates a command line.
/// </summary>
public static class ShellPinCommand
{
    public const string Option = "--pin-to-dock";
    public const int MaximumPathLength = 32767;

    public static bool TryParse(IReadOnlyList<string> arguments, out string path)
    {
        path = "";
        return arguments is { Count: 2 }
               && string.Equals(arguments[0], Option, StringComparison.OrdinalIgnoreCase)
               && TryNormalizeExistingPath(arguments[1], out path);
    }

    public static bool TryNormalizeExistingPath(string? candidate, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaximumPathLength
            || candidate.IndexOf('\0') >= 0
            || !Path.IsPathFullyQualified(candidate))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (fullPath.Length > MaximumPathLength || (!File.Exists(fullPath) && !Directory.Exists(fullPath)))
                return false;

            path = Directory.Exists(fullPath) ? Path.TrimEndingDirectorySeparator(fullPath) : fullPath;
            return path.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
                                          or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool RefersToSameExistingPath(string? first, string? second) =>
        TryNormalizeExistingPath(first, out var normalizedFirst)
        && TryNormalizeExistingPath(second, out var normalizedSecond)
        && string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
}
