namespace PHPUpdater;

public sealed record Journal
{
    public InstallationState? Previous { get; init; }
    public bool PathWasPresent { get; init; }
    public Dictionary<string, string> Created { get; set; } = [];
}

public sealed class Transaction(string root, string data,
    ISystemEnvironment environment)
{
    private ISystemEnvironment Environment => environment;
    public string StatePath => Path.Combine(data, "state.json");
    public string JournalPath => Path.Combine(data, "pending.json");
    public string Bin => Path.Combine(root, "bin");

    public InstallationState? ReadState() => File.Exists(StatePath)
        ? JsonFile.Read<InstallationState>(StatePath) : null;

    public Journal Begin()
    {
        Recover();
        var journal = new Journal
        {
            Previous = ReadState(),
            PathWasPresent = Environment.Path.Split(';')
                .Any(p => PathChange.Equivalent(p, Bin))
        };
        JsonFile.Write(JournalPath, journal);
        return journal;
    }

    public void Record(Journal journal, Dictionary<string, string> created)
    {
        foreach (var (file, hash) in created) { journal.Created[file] = hash; }
        JsonFile.Write(JournalPath, journal);
    }

    public void Commit(InstallationState state)
    {
        JsonFile.Write(StatePath, state);
        Environment.Path = PathChange.Add(Environment.Path, Bin);
        File.Delete(JournalPath);
    }

    public void Recover()
    {
        if (!File.Exists(JournalPath)) { return; }
        var journal = JsonFile.Read<Journal>(JournalPath);
        if (journal.Previous == null) { File.Delete(StatePath); }
        else { JsonFile.Write(StatePath, journal.Previous); }
        if (!journal.PathWasPresent)
        {
            Environment.Path = PathChange.Remove(Environment.Path, Bin);
        }
        SafeFiles.RemoveOwned(root, journal.Created);
        File.Delete(JournalPath);
    }
}
