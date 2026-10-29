namespace PHPUpdater;

public static class PathChange
{
    public static string Add(string path, string entry)
    {
        var rest = Remove(path, entry);
        return rest.Length == 0 ? entry : entry + ";" + rest;
    }

    public static string Remove(string path, string entry) =>
        string.Join(';', path.Split(';').Where(p => !Equivalent(p, entry)));

    public static bool Equivalent(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))
            .TrimEnd('\\', '/');
}
