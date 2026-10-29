using PHPUpdater;
using System.IO.Compression;

var tests = new Dictionary<string, Action>
{
    ["settings and bundled DLLs migrate across TS/NTS builds"] = () =>
    {
        using var temp = new Temp();
        var ini = Path.Combine(temp.Path, "source.ini");
        File.WriteAllText(ini, "memory_limit=384M\nextension=curl\n");
        var destination = Path.Combine(temp.Path, "new");
        Directory.CreateDirectory(Path.Combine(destination, "ext"));
        var bundled = Path.Combine(destination, "ext", "php_curl.dll");
        File.WriteAllText(bundled, "new bundled DLL");
        var source = new PhpInstallation
        {
            Executable = Path.Combine(temp.Path, "php.exe"), Ini = ini,
            Version = "8.2.1", Bits = 64, Variant = "TS",
            Build = "API20220829,TS,VS16"
        };
        Configuration.Prepare(destination, source, source with
        {
            Version = "8.2.35", Variant = "NTS",
            Build = "API20220829,NTS,VS16"
        });
        var migrated = File.ReadAllText(Path.Combine(destination, "php.ini"));
        Check(migrated.Contains("memory_limit=384M"));
        Check(migrated.Contains("php_curl.dll"));
        Equal("new bundled DLL", File.ReadAllText(bundled));
    },
    ["incompatible third-party DLL errors identify the extension"] = () =>
    {
        using var temp = new Temp();
        var ini = Path.Combine(temp.Path, "source.ini");
        File.WriteAllText(ini, "extension=vendor_driver\n");
        var source = new PhpInstallation
        {
            Executable = Path.Combine(temp.Path, "php.exe"), Ini = ini,
            Version = "8.2.1", Bits = 64, Variant = "TS",
            Build = "API20220829,TS,VS16"
        };
        try
        {
            Configuration.Prepare(Path.Combine(temp.Path, "new"), source,
                source with { Variant = "NTS", Build = "API20220829,NTS,VS16" });
        }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("php_vendor_driver.dll"));
            Check(error.Message.Contains("TS"));
            Check(error.Message.Contains("NTS"));
            return;
        }
        throw new Exception("Incompatible DLL must not be silently omitted");
    },
    ["extensions compiled into the target do not require an old DLL"] = () =>
    {
        using var temp = new Temp();
        var ini = Path.Combine(temp.Path, "source.ini");
        File.WriteAllText(ini, "zend_extension=opcache\nmemory_limit=384M\n");
        var source = new PhpInstallation
        {
            Executable = Path.Combine(temp.Path, "php.exe"), Ini = ini,
            Version = "8.5.1", Bits = 64, Variant = "TS", Build = "old"
        };
        var destination = Path.Combine(temp.Path, "new");
        Configuration.Prepare(destination, source,
            source with { Build = "new", Extensions = ["Zend OPcache"] });
        var migrated = File.ReadAllText(Path.Combine(destination, "php.ini"));
        Check(!migrated.Split('\n').Any(line => line.StartsWith("zend_extension=")));
        Check(migrated.Contains("memory_limit=384M"));
    },
    ["shell-parent launch uses the current non-elevated identity"] = () =>
    {
        using var temp = new Temp();
        var report = Path.Combine(temp.Path, "shell-identity.txt");
        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows,
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
        var script = "$i=[Security.Principal.WindowsIdentity]::GetCurrent();" +
            "$p=[Security.Principal.WindowsPrincipal]::new($i);" +
            "$a=$p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);" +
            "[IO.File]::WriteAllText('" + report.Replace("'", "''") +
            "', $i.User.Value+'|'+$a+'|'+[Environment]::CurrentDirectory);exit 17";
        var code = WindowsSecurity.RunWithShellParent(powershell,
            ["-NoProfile", "-NonInteractive", "-Command", script]);
        Check(code == 17);
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        Equal(current.User!.Value + "|False|" + windows,
            File.ReadAllText(report));
    },
    ["unelevated helper uses a stable explicit working directory"] = () =>
    {
        using var temp = new Temp();
        var report = Path.Combine(temp.Path, "working-directory.txt");
        var windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows,
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
        var script = "[IO.File]::WriteAllText('" + report.Replace("'", "''") +
            "', [Environment]::CurrentDirectory)";
        var code = WindowsSecurity.RunUnelevated(powershell,
            ["-NoProfile", "-NonInteractive", "-Command", script]);
        Check(code == 0);
        Equal(windows, File.ReadAllText(report));
    },
    ["fresh Windows INI enables CLI extensions with CRLF line endings"] = () =>
    {
        using var temp = new Temp();
        File.WriteAllText(Path.Combine(temp.Path, "php.ini-production"),
            ";extension=curl\r\n;extension=openssl\r\n" +
            ";extension=mbstring\r\n;extension=zip\r\n");
        Configuration.Prepare(temp.Path, null, new PhpInstallation());
        var ini = File.ReadAllText(Path.Combine(temp.Path, "php.ini"));
        Check(!ini.Contains(";extension="));
        Check(ini.Contains("extension=zip"));
    },
    ["offline catalog does not retain a claim of an online check"] = () =>
    {
        using var temp = new Temp();
        JsonFile.Write(Path.Combine(temp.Path, "manifest.json"),
            new Catalog { BuiltAt = DateTimeOffset.UtcNow,
                Notice = "Checked official sources online.", Online = true });
        var catalog = Catalog.Load(temp.Path, true).GetAwaiter().GetResult();
        Check(!catalog.Online);
        Check(!catalog.Notice.Contains("Checked official sources online"));
        Check(catalog.Notice.Contains("could not be verified"));
    },
    ["migration preserves certificate and application data paths"] = () =>
    {
        var ini = "curl.cainfo=\"C:/Old/cacert.pem\"\n" +
            "session.save_path=\"C:/Old/sessions\"\n" +
            "extension_dir=\"C:/Old/ext\"\n";
        var rewritten = Configuration.Rewrite(ini, @"C:\Old", @"C:\New");
        Check(rewritten.Contains("C:/Old/cacert.pem"));
        Check(rewritten.Contains("C:/Old/sessions"));
        Check(rewritten.Contains("C:/New/ext"));
    },
    ["locked-file uninstall preflight leaves every owned file intact"] = () =>
    {
        using var temp = new Temp();
        var first = Path.Combine(temp.Path, "a");
        var locked = Path.Combine(temp.Path, "z");
        File.WriteAllText(first, "first");
        File.WriteAllText(locked, "locked");
        var owned = SafeFiles.Inventory(temp.Path);
        using var stream = new FileStream(locked, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        Throws(() => SafeFiles.RemoveOwned(temp.Path, owned));
        Check(File.Exists(first));
        Check(File.Exists(locked));
    },
    ["interrupted activation restores baseline and preserves unrelated PATH edits"] = () =>
    {
        using var temp = new Temp();
        var root = Path.Combine(temp.Path, "app");
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(root);
        var environment = new MemoryEnvironment { Path = @"C:\PHP82;C:\Windows" };
        var transaction = new Transaction(root, data, environment);
        JsonFile.Write(transaction.StatePath, new InstallationState
            { DefaultBranch = "8.2", OriginalPath = environment.Path });
        var journal = transaction.Begin();
        var file = Path.Combine(root, "new-package");
        File.WriteAllText(file, "new");
        transaction.Record(journal, SafeFiles.Inventory(root));
        JsonFile.Write(transaction.StatePath, new InstallationState
            { DefaultBranch = "8.5" });
        environment.Path = @"C:\Later;" + transaction.Bin + @";C:\PHP82;C:\Windows";
        transaction.Recover();
        Equal("8.2", transaction.ReadState()!.DefaultBranch);
        Equal(@"C:\Later;C:\PHP82;C:\Windows", environment.Path);
        Check(!File.Exists(file));
        Check(!File.Exists(transaction.JournalPath));
    },
    ["successful activation remains after recovery and keeps original baseline"] = () =>
    {
        using var temp = new Temp();
        var environment = new MemoryEnvironment { Path = @"C:\PHP82" };
        var transaction = new Transaction(Path.Combine(temp.Path, "app"),
            Path.Combine(temp.Path, "data"), environment);
        transaction.Begin();
        transaction.Commit(new InstallationState { DefaultBranch = "8.2",
            OriginalPath = @"C:\PHP82", PathAdded = true });
        transaction.Recover();
        Equal("8.2", transaction.ReadState()!.DefaultBranch);
        Equal(transaction.Bin + @";C:\PHP82", environment.Path);
        Equal(@"C:\PHP82", transaction.ReadState()!.OriginalPath);
    },
    ["PHP feed keeps TS and NTS revisions and excludes x86"] = () =>
    {
        var json = """
        {"8.2":{"version":"8.2.35-1",
          "ts-vs16-x64":{"mtime":"2026-10-26T13:06:48Z",
            "zip":{"path":"php-ts.zip","sha256":"HASH"}},
          "nts-vs16-x64":{"mtime":"2026-10-26T13:06:48Z",
            "zip":{"path":"php-nts.zip","sha256":"HASH"}},
          "nts-vs16-x86":{"mtime":"2026-10-26T13:06:48Z",
            "zip":{"path":"php-x86.zip","sha256":"HASH"}}}}
        """.Replace("HASH", new string('a', 64));
        var packages = Catalog.ParsePhp(json,
            new Dictionary<string, DateOnly> { ["8.2"] = new(2026, 12, 31) });
        Check(packages.Count == 2);
        Equal("8.2.35-1", packages[0].Version);
        Check(packages.Select(p => p.Variant).Order().SequenceEqual(
            new[] { "NTS", "TS" }));
    },
    ["support table supplies security expiration dates"] = () =>
    {
        var html = """
        <tr class="security"><td><a href="/downloads.php?version=8.2">8.2</a>
        </td><td>8 Dec 2022</td><td>x</td><td>31 Dec 2024</td>
        <td>x</td><td>31 Dec 2026</td></tr>
        """;
        Equal("12/31/2026", Catalog.ParseSupport(html)["8.2"].ToString(
            "MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture));
    },
    ["migration rebases extension paths but retains application settings"] = () =>
    {
        var ini = "extension_dir=\"C:/Old/ext\"\n" +
            "extension=curl\nmemory_limit=512M\n";
        var result = Configuration.Rewrite(ini, @"C:\Old", @"C:\New");
        Check(result.Contains("memory_limit=512M"));
        Check(result.Contains("extension=curl"));
        Check(!result.Contains("C:/Old"));
        Check(result.Contains("C:/New/ext"));
    },
    ["cross-branch or different ABI extensions cannot be migrated"] = () =>
    {
        var a = new PhpInstallation { Version = "8.2.1", Build = "API20220829,NTS,VS16", Bits = 64 };
        var b = a with { Version = "8.5.1", Build = "API20250925,NTS,VS17" };
        Check(!Configuration.CanReuseExtensionBinaries(a, b));
        Check(Configuration.CanReuseExtensionBinaries(a, a with { Version = "8.2.35" }));
        Check(!Configuration.CanReuseExtensionBinaries(a, a with { Bits = 32 }));
    },
    ["atomic JSON preserves the last committed state"] = () =>
    {
        using var temp = new Temp();
        var file = Path.Combine(temp.Path, "state.json");
        JsonFile.Write(file, new InstallationState { DefaultBranch = "8.2" });
        File.WriteAllText(file + ".new", "interrupted");
        Equal("8.2", JsonFile.Read<InstallationState>(file).DefaultBranch);
    },
    ["build revisions sort after the original release"] = () =>
        Check(Release.Compare("8.2.35-1", "8.2.35") > 0),
    ["patch releases sort numerically"] = () =>
        Check(Release.Compare("8.2.10", "8.2.9-2") > 0),
    ["newer installed versions cannot be downgraded"] = () =>
        Check(!Release.ShouldInstall("8.2.36", "8.2.35-1")),
    ["preview versions are rejected"] = () =>
        Throws(() => Release.Compare("8.6.0RC1", "8.5.0")),
    ["PATH insertion preserves original entries"] = () =>
        Equal(@"C:\Managed;C:\PHP;;%SystemRoot%", 
            PathChange.Add(@"C:\PHP;;%SystemRoot%", @"C:\Managed")),
    ["PATH removal preserves subsequent unrelated edits"] = () =>
        Equal(@"C:\New;C:\PHP;;%SystemRoot%", PathChange.Remove(
            @"C:\New;C:\Managed;C:\PHP;;%SystemRoot%", @"C:\Managed")),
    ["PATH insertion does not duplicate equivalent entries"] = () =>
        Equal(@"C:\Managed;C:\PHP", PathChange.Add(
            @"C:\PHP;C:\Managed\", @"C:\Managed")),
    ["archive traversal is rejected"] = () =>
    {
        using var temp = new Temp();
        var zip = Path.Combine(temp.Path, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                archive.CreateEntry("../escape.txt").Open());
            writer.Write("bad");
        }
        Throws(() => SafeFiles.Extract(zip, Path.Combine(temp.Path, "out")));
        Check(!File.Exists(Path.Combine(temp.Path, "escape.txt")));
    },
    ["owned-file cleanup preserves changed and unknown files"] = () =>
    {
        using var temp = new Temp();
        File.WriteAllText(Path.Combine(temp.Path, "original"), "a");
        File.WriteAllText(Path.Combine(temp.Path, "changed"), "a");
        var owned = SafeFiles.Inventory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "changed"), "b");
        File.WriteAllText(Path.Combine(temp.Path, "unknown"), "c");
        var retained = SafeFiles.RemoveOwned(temp.Path, owned);
        Check(!File.Exists(Path.Combine(temp.Path, "original")));
        Equal("b", File.ReadAllText(Path.Combine(temp.Path, "changed")));
        Equal("c", File.ReadAllText(Path.Combine(temp.Path, "unknown")));
        Check(retained.Count == 2);
    },
    ["owned paths cannot escape the installation root"] = () =>
    {
        using var temp = new Temp();
        Throws(() => SafeFiles.Resolve(temp.Path, "../outside"));
        Throws(() => SafeFiles.Resolve(temp.Path, @"C:\outside"));
        Throws(() => SafeFiles.Resolve(temp.Path, "file:stream"));
    },
    ["network policy rejects foreign hosts and non-HTTPS"] = () =>
    {
        Throws(() => OfficialHttp.Validate(new Uri("http://getcomposer.org/a")));
        Throws(() => OfficialHttp.Validate(new Uri("https://evil.test/a")));
        OfficialHttp.Validate(new Uri("https://downloads.php.net/~windows/a"));
    }
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
if (failures == 0 && args.Length == 4 && args[0] == "--integration")
{
    try { await Integration.Run(args[1], args[2], args[3]); }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL integration: " + error);
        failures++;
    }
}
return failures == 0 ? 0 : 1;

static void Check(bool condition)
{
    if (!condition) { throw new Exception("Assertion failed"); }
}
static void Equal(string expected, string actual)
{
    if (expected != actual)
    {
        throw new Exception($"Expected '{expected}', got '{actual}'");
    }
}
static void Throws(Action action)
{
    try { action(); }
    catch (Exception) { return; }
    throw new Exception("Expected rejection");
}
sealed class Temp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PHPUpdater-tests-" + Guid.NewGuid());
    public Temp() { Directory.CreateDirectory(Path); }
    public void Dispose() { Directory.Delete(Path, true); }
}
sealed class MemoryEnvironment : ISystemEnvironment
{
    public string Path { get; set; } = "";
}
