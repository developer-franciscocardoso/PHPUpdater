using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PHPUpdater;

public static class Discovery
{
    public static int PeBits(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        if (reader.ReadUInt16() != 0x5A4D) { return 0; }
        reader.BaseStream.Position = 0x3C;
        var offset = reader.ReadInt32();
        if (offset < 0 || offset > reader.BaseStream.Length - 6) { return 0; }
        reader.BaseStream.Position = offset;
        if (reader.ReadUInt32() != 0x4550) { return 0; }
        return reader.ReadUInt16() switch { 0x8664 => 64, 0x14C => 32, _ => 0 };
    }

    public static async Task<PhpInstallation> Probe(string executable,
        bool bare = false, bool isolated = false)
    {
        WindowsSecurity.RequireUnelevated();
        var script = """
        ob_start(); phpinfo(INFO_GENERAL); $i = ob_get_clean();
        preg_match('/PHP Extension Build\s*=>\s*([^\r\n]+)/', $i, $m);
        echo json_encode([
          'Executable'=>PHP_BINARY, 'Version'=>PHP_VERSION,
          'Variant'=>PHP_ZTS?'TS':'NTS', 'Bits'=>PHP_INT_SIZE*8,
          'Build'=>trim($m[1]??''),
          'Ini'=>php_ini_loaded_file()?:'',
          'ScannedIni'=>array_values(array_filter(array_map('trim',
            explode(',',php_ini_scanned_files()?:'')))),
          'ExtensionDirectory'=>ini_get('extension_dir'),
          'Extensions'=>get_loaded_extensions()]);
        """;
        var arguments = new List<string>();
        if (bare) { arguments.Add("-n"); }
        arguments.AddRange(["-r", script]);
        var result = await Processes.Capture(executable, arguments,
            isolated ? Path.GetDirectoryName(executable) : null);
        if (result.Code != 0 || result.Error.Trim().Length != 0)
        {
            throw new InvalidDataException("PHP startup validation failed for " +
                executable + ". " + result.Error.Trim());
        }
        var installation = JsonSerializer.Deserialize<PhpInstallation>(
            result.Output, JsonFile.Options)
            ?? throw new InvalidDataException("Invalid PHP probe response");
        Release.Parse(installation.Version);
        return installation with
        {
            Executable = Path.GetFullPath(executable),
            ExtensionDirectory = Path.GetFullPath(
                installation.ExtensionDirectory,
                Path.GetDirectoryName(executable)!)
        };
    }

    public static async Task<DiscoveryReport> Find(string? extra)
    {
        WindowsSecurity.RequireUnelevated();
        var result = new DiscoveryReport();
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';').Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (!string.IsNullOrWhiteSpace(extra)) { paths.Add(extra); }
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
            var php = Path.Combine(path, "php.exe");
            if (!File.Exists(php) || File.Exists(Path.Combine(path, "PHPUpdater.dll")))
            {
                continue;
            }
            try { result.Php.Add(await Probe(php)); }
            catch (Exception error)
            {
                result.Warnings.Add("Cannot inspect " + php + ": " + error.Message);
            }
        }
        result.ComposerPath = FindCommand(paths, "composer");
        if (result.ComposerPath.Length != 0)
        {
            try
            {
                var extension = Path.GetExtension(result.ComposerPath);
                var command = result.ComposerPath;
                string[] args = ["--no-plugins", "--no-scripts", "--version", "--no-ansi"];
                (int Code, string Output, string Error) version;
                if (extension is ".bat" or ".cmd")
                {
                    command = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.System), "cmd.exe");
                    version = await Processes.Capture(new ProcessStartInfo(command)
                    {
                        Arguments = "/d /s /c \"\"" + result.ComposerPath +
                            "\" --no-plugins --no-scripts --version --no-ansi\"",
                        WorkingDirectory = Path.GetDirectoryName(result.ComposerPath)!
                    });
                }
                else { version = await Processes.Capture(command, args); }
                var match = Regex.Match(version.Output,
                    @"Composer(?: version)?\s+(\d+\.\d+\.\d+)");
                if (version.Code == 0 && match.Success)
                {
                    result.ComposerVersion = match.Groups[1].Value;
                }
                else { result.Warnings.Add("Existing Composer version is unknown."); }
            }
            catch (Exception error) { result.Warnings.Add(error.Message); }
        }
        return result;
    }

    private static string FindCommand(IEnumerable<string> paths, string name)
    {
        foreach (var path in paths)
        {
            if (File.Exists(Path.Combine(path, "PHPUpdater.dll"))) { continue; }
            foreach (var extension in new[] { ".exe", ".bat", ".cmd" })
            {
                var file = Path.Combine(path.Trim('"'), name + extension);
                if (File.Exists(file)) { return Path.GetFullPath(file); }
            }
        }
        return "";
    }
}
