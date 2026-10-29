namespace PHPUpdater;

public static class Release
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        var result = a.Version.CompareTo(b.Version);
        return result == 0 ? a.Revision.CompareTo(b.Revision) : result;
    }

    public static bool ShouldInstall(string installed, string offered) =>
        Compare(offered, installed) > 0;

    public static (Version Version, int Revision) Parse(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^\d+\.\d+\.\d+(?:-\d+)?$"))
        {
            throw new InvalidDataException("Invalid stable version: " + value);
        }
        var parts = value.Split('-');
        return (Version.Parse(parts[0]),
            parts.Length == 2 ? int.Parse(parts[1]) : 0);
    }

    public static string Branch(string value)
    {
        var version = Parse(value).Version;
        return $"{version.Major}.{version.Minor}";
    }
}
