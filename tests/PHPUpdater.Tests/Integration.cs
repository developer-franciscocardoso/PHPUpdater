using PHPUpdater;
using System.Diagnostics;
using System.Text.Json;

internal static class Integration
{
    public static async Task Run(string helper, string payload, string originalArchive)
    {
        using var temp = new Temp();
        var root = Path.Combine(temp.Path, "managed app");
        var data = Path.Combine(temp.Path, "data");
        var original = Path.Combine(temp.Path, "original php");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(helper)!))
        {
            File.Copy(file, Path.Combine(root, "bin", Path.GetFileName(file)));
        }
        var engineFiles = SafeFiles.Inventory(Path.Combine(root, "bin"));
        engineFiles.Remove("engine-files.json");
        JsonFile.Write(Path.Combine(root, "bin", "engine-files.json"), engineFiles);
        SafeFiles.Extract(originalArchive, original);
        File.WriteAllText(Path.Combine(original, "php.ini"),
            "memory_limit=384M\nextension_dir=\"" + original.Replace('\\', '/') +
            "/ext\"\nextension=curl\nextension=openssl\n");
        Directory.CreateDirectory(Path.Combine(original, "conf.d"));
        File.WriteAllText(Path.Combine(original, "conf.d", "settings.ini"),
            "precision=13\n");
        var before = SafeFiles.Inventory(original);
        var old = await Discovery.Probe(Path.Combine(original, "php.exe"),
            isolated: true);
        Assert(old.Version == "8.2.1", "Fixture must be PHP 8.2.1");
        var discovery = new DiscoveryReport { Php = [old] };
        var environment = new MemoryEnvironment { Path = original + @";C:\Windows" };
        var baselinePath = environment.Path;
        var catalog = JsonFile.Read<Catalog>(Path.Combine(payload, "manifest.json"));
        var expectedPhp = catalog.Packages.Single(p => p.Branch == "8.2" &&
            p.Variant == "NTS").Version.Split('-')[0];
        var expectedComposer = catalog.Packages.Single(p => p.Kind == "composer").Version;
        var engine = new InstallationEngine(root, data, environment, helper);
        var request = new InstallRequest
        {
            Branches = ["8.2", "8.5"], DefaultBranch = "8.2", Offline = true
        };
        var badIni = Path.Combine(temp.Path, "bad.ini");
        File.WriteAllText(badIni, "extension=missing_phpupdater_test_extension\n");
        var badDiscovery = new DiscoveryReport { Php = [old with { Ini = badIni }] };
        var failed = false;
        try { await engine.Install(catalog, badDiscovery, request, payload); }
        catch (InvalidOperationException) { failed = true; }
        Assert(failed, "An invalid extension must block activation");
        Assert(environment.Path == baselinePath &&
            !File.Exists(Path.Combine(data, "state.json")),
            "Failed migration must preserve PATH and the previous installation");
        await engine.Install(catalog, discovery, request, payload);
        var statePath = Path.Combine(data, "state.json");
        var state = JsonFile.Read<InstallationState>(statePath);
        Assert(state.Php.Count == 2, "Both PHP branches must be installed");
        Assert(state.DefaultBranch == "8.2", "The default branch must be retained");
        var future = JsonFile.Read<InstallationState>(statePath);
        future.Php["8.2"] = future.Php["8.2"] with
        {
            Package = future.Php["8.2"].Package with
                { Version = expectedPhp + "-9999", Sha256 = new string('a', 64) }
        };
        JsonFile.Write(statePath, future);
        await engine.Install(catalog, discovery, request, payload);
        Assert(JsonFile.Read<InstallationState>(statePath).Php["8.2"].Package.Version
            == expectedPhp + "-9999", "A bundled older Windows revision must not downgrade a managed build");
        JsonFile.Write(statePath, state);
        var php = Path.Combine(root, "bin", "php.exe");
        var value = await Processes.Capture(php,
            ["-r", "echo PHP_VERSION, '|', ini_get('memory_limit'), '|', ini_get('precision');"]);
        Assert(value.Code == 0 && value.Output == expectedPhp + "|384M|13",
            "Native launcher must use the migrated 8.2 configuration: " + value.Output + value.Error);
        var alternate = await Processes.Capture(Path.Combine(root, "bin", "php85.exe"),
            ["-r", "echo PHP_MAJOR_VERSION, '.', PHP_MINOR_VERSION;"]);
        Assert(alternate.Code == 0 && alternate.Output == "8.5",
            "Version-specific launcher must select 8.5");
        var composer = await Processes.Capture(Path.Combine(root, "bin", "composer.exe"),
            ["--version", "--no-ansi", "--no-plugins", "--no-scripts"]);
        Assert(composer.Code == 0 && composer.Output.Contains(expectedComposer),
            "Composer launcher must execute the managed PHAR: " + composer.Error);
        var phar = SafeFiles.Resolve(root, state.Composer);
        var pharBytes = File.ReadAllBytes(phar);
        File.WriteAllText(phar, "<?php exit(9);");
        var refusedModifiedComposer = false;
        try { await engine.Install(catalog, discovery,
            request with { DefaultBranch = "8.5" }, payload); }
        catch (InvalidDataException) { refusedModifiedComposer = true; }
        Assert(refusedModifiedComposer &&
            JsonFile.Read<InstallationState>(statePath).DefaultBranch == "8.2",
            "An altered Composer PHAR must not bypass validation on a default switch");
        File.WriteAllBytes(phar, pharBytes);
        var argumentTest = await Processes.Capture(php,
            ["-r", "echo json_encode(array_slice($argv,1)); exit(17);", "--",
             "", "two words", "quote\"inside", @"trailing\", "&%!"]);
        Assert(argumentTest.Code == 17, "Exit code must propagate through native launcher");
        Assert(JsonSerializer.Deserialize<string[]>(argumentTest.Output)!
            .SequenceEqual(new[] { "", "two words", "quote\"inside", @"trailing\", "&%!" }),
            "Arguments must arrive unchanged");
        var stdin = new ProcessStartInfo(php)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = temp.Path
        };
        stdin.ArgumentList.Add("-r");
        stdin.ArgumentList.Add("echo getcwd(), '|', stream_get_contents(STDIN); fwrite(STDERR,'error-stream');");
        using (var child = Process.Start(stdin)!)
        {
            await child.StandardInput.WriteAsync("input-stream");
            child.StandardInput.Close();
            var text = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            Assert(text == temp.Path + "|input-stream" && error == "error-stream",
                "Working directory and standard streams must be preserved");
        }
        await engine.Install(catalog, discovery, request, payload);
        var repeated = JsonFile.Read<InstallationState>(statePath);
        Assert(repeated.Php["8.2"].Directory == state.Php["8.2"].Directory,
            "Repeated install must reuse existing versions");
        Assert(repeated.OriginalPath == baselinePath,
            "Repeated install must retain original baseline");
        var variantRequest = request with
        {
            Branches = ["8.2"], Build = "TS", DefaultBranch = "8.2"
        };
        await engine.Install(catalog, discovery, variantRequest, payload);
        var migrated = await Processes.Capture(php,
            ["-r", "echo PHP_ZTS, '|', ini_get('memory_limit'), '|', ini_get('precision');"]);
        Assert(migrated.Output == "1|384M|13",
            "TS migration must preserve settings from the NTS build");
        var migratedState = JsonFile.Read<InstallationState>(statePath);
        var cleanRequest = variantRequest with { ConfigurationMode = "clean" };
        await engine.Install(catalog, discovery, cleanRequest, payload);
        var clean = await Processes.Capture(php,
            ["-r", "echo PHP_ZTS, '|', ini_get('memory_limit'), '|', ini_get('precision');"]);
        Assert(clean.Code == 0 && clean.Output != migrated.Output,
            "Explicit clean configuration must replace migrated settings");
        var cleanState = JsonFile.Read<InstallationState>(statePath);
        Assert(cleanState.Php["8.2"].Directory !=
            migratedState.Php["8.2"].Directory,
            "Clean configuration must stage a separate generation");
        await engine.Install(catalog, discovery, cleanRequest, payload);
        Assert(JsonFile.Read<InstallationState>(statePath).Php["8.2"].Directory ==
            cleanState.Php["8.2"].Directory,
            "Repeated clean installation must be idempotent");
        Console.WriteLine("PASS real NTS -> TS settings migration and explicit clean configuration");
        await engine.Install(catalog, discovery, request with { DefaultBranch = "8.5" }, payload);
        var switched = await Processes.Capture(php,
            ["-r", "echo PHP_MAJOR_VERSION, '.', PHP_MINOR_VERSION;"]);
        Assert(switched.Output == "8.5", "Changing default must change launcher target");
        var modified = Path.Combine(root, "bin", "php85.exe");
        File.AppendAllText(modified, "user-modification");
        var unknown = Path.Combine(root, "keep-me.txt");
        File.WriteAllText(unknown, "user file");
        environment.Path = @"C:\AddedLater;" + environment.Path;
        engine.Uninstall();
        Assert(environment.Path == @"C:\AddedLater;" + baselinePath,
            "Uninstall must retain subsequent PATH changes and original PHP");
        Assert(File.Exists(modified) && File.Exists(unknown),
            "Uninstall must retain modified and unrecognized files");
        Assert(!File.Exists(php), "Unmodified managed launcher must be removed");
        Assert(SafeFiles.Inventory(original).OrderBy(p => p.Key)
            .SequenceEqual(before.OrderBy(p => p.Key)),
            "Every original PHP file must remain byte-identical");
        var restored = await Discovery.Probe(Path.Combine(original, "php.exe"));
        Assert(restored.Version == "8.2.1", "Original PHP must still execute after uninstall");
        Console.WriteLine("PASS real PHP 8.2.1 -> 8.2.35-1 + 8.5 -> uninstall; " +
            "native launchers, Composer, streams, arguments, idempotency and PATH preservation");
        await ValidateBundles(catalog, payload);
    }

    private static async Task ValidateBundles(Catalog catalog, string payload)
    {
        foreach (var package in catalog.Packages.Where(p => p.Kind == "php"))
        {
            using var temp = new Temp();
            var archive = Path.Combine(payload, package.FileName);
            SafeFiles.Verify(archive, package.Sha256);
            SafeFiles.Extract(archive, temp.Path);
            var executable = Path.Combine(temp.Path, "php.exe");
            var bare = await Discovery.Probe(executable, bare: true);
            Assert(bare.Bits == 64 && bare.Variant == package.Variant,
                "Build architecture or thread safety mismatch");
            Configuration.Prepare(temp.Path, null, bare);
            var configured = await Discovery.Probe(executable, isolated: true);
            Assert(configured.Version == Release.Parse(package.Version).Version.ToString(),
                "Bundled version mismatch");
            Assert(new[] { "curl", "openssl", "mbstring", "zip" }
                .All(configured.Extensions.Contains), "Fresh CLI extensions must load");
            Console.WriteLine("PASS bundled PHP startup/configuration " +
                package.Version + " " + package.Variant);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }
}
