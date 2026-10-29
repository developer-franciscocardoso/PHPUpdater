namespace PHPUpdater;

public sealed record Package
{
    public string Kind { get; init; } = "php";
    public string Version { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Variant { get; init; } = "";
    public string Compiler { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public DateTimeOffset Released { get; init; }
    public DateOnly SupportEnd { get; init; }
}

public sealed record PhpInstallation
{
    public string Executable { get; init; } = "";
    public string Version { get; init; } = "";
    public string Variant { get; init; } = "NTS";
    public string Build { get; init; } = "";
    public int Bits { get; init; }
    public string Ini { get; init; } = "";
    public string[] ScannedIni { get; init; } = [];
    public string ExtensionDirectory { get; init; } = "";
    public string[] Extensions { get; init; } = [];
    public string Branch => Release.Branch(Version);
}

public sealed record DiscoveryReport
{
    public List<PhpInstallation> Php { get; init; } = [];
    public string ComposerPath { get; set; } = "";
    public string ComposerVersion { get; set; } = "";
    public List<string> Warnings { get; init; } = [];
}

public sealed record ManagedPhp
{
    public string ConfigurationMode { get; init; } = "migrate";
    public required Package Package { get; init; }
    public required string Directory { get; init; }
}

public sealed record InstallationState
{
    public int Schema { get; init; } = 1;
    public string DefaultBranch { get; set; } = "";
    public Dictionary<string, ManagedPhp> Php { get; init; } = [];
    public string Composer { get; set; } = "";
    public string ComposerVersion { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public bool PathAdded { get; set; }
    public Dictionary<string, string> Owned { get; set; } = [];
}
