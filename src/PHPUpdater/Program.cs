using System.Diagnostics;
using System.Text.Json;

namespace PHPUpdater;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var commandName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        if (commandName.Equals("composer", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(commandName, @"^php\d*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return Launch(commandName.ToLowerInvariant(), args);
        }
        var output = "";
        try
        {
            var options = ParseOptions(args.Skip(1).ToArray());
            output = options.GetValueOrDefault("out", "");
            var command = args.FirstOrDefault() ?? "help";
            await Execute(command, options);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (output.Length != 0)
            {
                File.WriteAllText(output + ".error", error.Message);
            }
            return 1;
        }
    }

    private static async Task Execute(string command,
        Dictionary<string, string> options)
    {
        string Get(string key) => options.TryGetValue(key, out var value)
            ? value : throw new ArgumentException("Missing --" + key);
        bool Flag(string key) => options.GetValueOrDefault(key, "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var output = options.GetValueOrDefault("out", "");
        switch (command)
        {
            case "bundle":
                await Bundle(Get("directory"), Get("runtime"));
                break;
            case "catalog":
                JsonFile.Write(output, await Catalog.Load(Get("bundle"), Flag("offline")));
                break;
            case "discover":
                if (WindowsSecurity.Elevated)
                {
                    var report = await InstallationEngine.Child(Environment.ProcessPath!, "discover",
                        "--extra", options.GetValueOrDefault("extra", ""));
                    File.WriteAllText(output, report);
                }
                else
                {
                    JsonFile.Write(output, await Discovery.Find(
                        options.GetValueOrDefault("extra")));
                }
                break;
            case "wizard":
                Wizard(Get("catalog"), Get("discovery"), output);
                break;
            case "probe":
                JsonFile.Write(output, await Discovery.Probe(Get("exe"),
                    Flag("bare"), Flag("isolated")));
                break;
            case "configure":
                PrepareConfiguration(Get("input"), Get("work"));
                File.WriteAllText(output, "{}");
                break;
            case "validate-composer":
                WindowsSecurity.RequireUnelevated();
                var check = await Processes.Capture(Get("exe"),
                    [Get("phar"), "--no-plugins", "--no-scripts", "--version", "--no-ansi"],
                    Path.GetDirectoryName(Get("exe")));
                if (check.Code != 0 ||
                    !check.Output.StartsWith("Composer version "))
                {
                    throw new InvalidDataException("Composer validation failed: " + check.Error);
                }
                File.WriteAllText(output, "{}");
                break;
            case "install":
            case "uninstall":
            case "preflight":
                if (!WindowsSecurity.Elevated)
                {
                    throw new InvalidOperationException("Administrator elevation is required.");
                }
                var root = InstallRoot;
                var data = DataRoot;
                WindowsSecurity.ProtectDirectory(root);
                WindowsSecurity.ProtectDirectory(data);
                var engine = new InstallationEngine(root, data, new SystemEnvironment(),
                    Environment.ProcessPath!);
                if (command == "preflight") { engine.Preflight(); break; }
                if (command == "uninstall") { engine.Uninstall(); break; }
                var request = new InstallRequest
                {
                    Branches = Get("branches").Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    DefaultBranch = Get("default"), Build = Get("build").ToUpperInvariant(),
                    Offline = Flag("offline"),
                    ConfigurationMode = options.GetValueOrDefault(
                        "configuration", "migrate").ToLowerInvariant()
                };
                if (request.Build is not ("" or "TS" or "NTS"))
                {
                    throw new ArgumentException("PHPBUILD must be TS or NTS");
                }
                await engine.Install(JsonFile.Read<Catalog>(Get("catalog")),
                    JsonFile.Read<DiscoveryReport>(Get("discovery")), request, Get("bundle"));
                break;
            default:
                Console.WriteLine("PHPUpdater: use the Inno Setup installer to install, " +
                    "update, or select PHP. Commands: php, php82, php85, composer.");
                break;
        }
    }

    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PHPUpdater");
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PHPUpdater");

    private static void PrepareConfiguration(string input, string work)
    {
        WindowsSecurity.RequireUnelevated();
        var config = JsonFile.Read<ConfigurationInput>(input);
        var prepared = Path.Combine(work, "prepared");
        Directory.CreateDirectory(Path.Combine(prepared, "ext"));
        File.Copy(Path.Combine(config.Directory, "php.ini-production"),
            Path.Combine(prepared, "php.ini-production"));
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(config.Directory, "ext"), "*.dll"))
        {
            File.Copy(file, Path.Combine(prepared, "ext", Path.GetFileName(file)));
        }
        Configuration.Prepare(prepared, config.Source, config.Target);
        foreach (var ini in Directory.EnumerateFiles(prepared, "*.ini",
            SearchOption.AllDirectories))
        {
            File.WriteAllText(ini, Configuration.Rewrite(File.ReadAllText(ini),
                prepared, config.Directory));
        }
    }

    private static int Launch(string name, string[] arguments)
    {
        try
        {
            var root = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\'))!;
            var binding = JsonFile.Read<LauncherBinding>(Path.Combine(
                AppContext.BaseDirectory, "installation.json"));
            var state = JsonFile.Read<InstallationState>(Path.Combine(
                binding.DataDirectory, "state.json"));
            var branch = name is "php" or "composer" ? state.DefaultBranch :
                state.Php.Keys.Single(b => "php" + b.Replace(".", "") == name);
            var directory = SafeFiles.Resolve(root, state.Php[branch].Directory);
            var start = new ProcessStartInfo(Path.Combine(directory, "php.exe"))
            {
                UseShellExecute = false
            };
            Processes.Isolate(start, directory);
            if (name == "composer")
            {
                start.ArgumentList.Add(SafeFiles.Resolve(root, state.Composer));
            }
            foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("PHPUpdater: " + error.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--") || index + 1 == args.Length)
            {
                throw new ArgumentException("Options require --name value pairs");
            }
            result.Add(args[index][2..], args[index + 1]);
        }
        return result;
    }

    private static void Wizard(string catalogPath, string discoveryPath, string output)
    {
        var catalog = JsonFile.Read<Catalog>(catalogPath);
        var discovery = JsonFile.Read<DiscoveryReport>(discoveryPath);
        var statePath = Path.Combine(DataRoot, "state.json");
        var state = File.Exists(statePath) ? JsonFile.Read<InstallationState>(statePath) : null;
        var branches = catalog.Branches();
        var detected = discovery.Php.Where(p => p.Bits == 64 && branches.Contains(p.Branch))
            .Select(p => p.Branch).Concat(state?.Php.Keys ?? Enumerable.Empty<string>())
            .Where(branches.Contains).Distinct().ToArray();
        var defaultBranch = state?.DefaultBranch ?? discovery.Php.FirstOrDefault()?.Branch ?? "";
        var lines = new List<string>
        {
            "[Wizard]", "Branches=" + string.Join(',', branches),
            "Detected=" + string.Join(',', detected), "Default=" + defaultBranch,
            "Notice=" + catalog.Notice.Replace('\n', ' '),
            "Composer=" + discovery.ComposerVersion,
            "Warnings=" + string.Join(" | ", discovery.Warnings).Replace('\n', ' ')
        };
        foreach (var branch in branches)
        {
            var package = catalog.Packages.First(p => p.Branch == branch);
            lines.Add(branch + "=PHP " + package.Version +
                $" ({package.Released:yyyy-MM-dd})");
        }
        File.WriteAllLines(output, lines);
    }

    private static async Task Bundle(string directory, string runtime)
    {
        Directory.CreateDirectory(directory);
        var catalog = await Catalog.Fetch();
        foreach (var package in catalog.Packages)
        {
            var destination = SafeFiles.Resolve(directory, package.FileName);
            if (!File.Exists(destination))
            {
                Console.WriteLine("Downloading " + package.FileName);
                await File.WriteAllBytesAsync(destination,
                    await OfficialHttp.Get(package.Url));
            }
            SafeFiles.Verify(destination, package.Sha256);
        }
        var runtimeName = "vc_redist.x64.exe";
        var runtimeTarget = Path.Combine(directory, runtimeName);
        if (!Path.GetFullPath(runtime).Equals(Path.GetFullPath(runtimeTarget),
            StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(runtime, runtimeTarget, true);
        }
        catalog.Packages.Add(new Package
        {
            Kind = "runtime", FileName = runtimeName,
            Sha256 = SafeFiles.Hash(runtimeTarget),
            Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            Version = FileVersionInfo.GetVersionInfo(runtimeTarget).FileVersion ?? "unknown"
        });
        JsonFile.Write(Path.Combine(directory, "manifest.json"), catalog);
    }
}
