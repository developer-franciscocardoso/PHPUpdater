using System.Diagnostics;

namespace PHPUpdater;

public sealed record InstallRequest
{
    public string[] Branches { get; init; } = [];
    public string DefaultBranch { get; init; } = "";
    public string Build { get; init; } = "";
    public bool Offline { get; init; }
    public string ConfigurationMode { get; init; } = "migrate";
}

public sealed class InstallationEngine(string root, string data,
    ISystemEnvironment environment, string helperExecutable)
{
    private readonly Transaction transaction = new(root, data, environment);

    public async Task Install(Catalog catalog, DiscoveryReport discovery,
        InstallRequest request, string bundle)
    {
        if (request.ConfigurationMode is not ("migrate" or "clean"))
        {
            throw new ArgumentException("PHPCONFIG must be migrate or clean");
        }
        using var operation = Lock();
        SaveBootstrap();
        var journal = transaction.Begin();
        var state = transaction.ReadState() ?? new InstallationState
        {
            OriginalPath = environment.Path,
            PathAdded = !environment.Path.Split(';').Any(p =>
                PathChange.Equivalent(p, transaction.Bin))
        };
        try
        {
            var branches = request.Branches.Length != 0 ? request.Branches :
                discovery.Php.Where(p => p.Bits == 64)
                    .Select(p => p.Branch).Concat(state.Php.Keys)
                    .Where(catalog.Branches().Contains).Distinct().ToArray();
            if (branches.Length == 0)
            {
                throw new InvalidOperationException("Select a supported PHP " +
                    "branch using /PHPBRANCHES or the installation wizard.");
            }
            foreach (var branch in branches)
            {
                await InstallPhp(branch, state, journal, catalog, discovery,
                    request, bundle);
            }
            var defaultBranch = request.DefaultBranch;
            if (defaultBranch.Length == 0)
            {
                defaultBranch = state.DefaultBranch.Length != 0
                    ? state.DefaultBranch : discovery.Php.FirstOrDefault()?.Branch ??
                        branches.OrderBy(Version.Parse).Last();
            }
            if (!state.Php.ContainsKey(defaultBranch))
            {
                throw new InvalidOperationException("Choose /DEFAULTPHP from " +
                    "the installed supported branches. Existing default cannot " +
                    "be replaced implicitly.");
            }
            state.DefaultBranch = defaultBranch;
            await InstallComposer(state, journal, catalog, discovery, bundle,
                request.Offline);
            InstallLaunchers(state, journal);
            transaction.Commit(state);
            WriteLog("Installed. Default PHP: " + defaultBranch +
                ". Reopen terminals to refresh PATH.");
        }
        catch
        {
            transaction.Recover();
            throw;
        }
    }

    private async Task InstallPhp(string branch, InstallationState state,
        Journal journal, Catalog catalog, DiscoveryReport discovery,
        InstallRequest request, string bundle)
    {
        if (!catalog.Branches().Contains(branch))
        {
            throw new InvalidDataException("Unsupported branch: " + branch);
        }
        state.Php.TryGetValue(branch, out var managed);
        var source = discovery.Php.FirstOrDefault(p => p.Branch == branch &&
            p.Bits == 64);
        if (managed != null)
        {
            var executable = SafeFiles.Resolve(root, managed.Directory + "/php.exe");
            source = await Probe(executable, false, true);
        }
        var variant = request.Build.Length != 0 ? request.Build :
            source?.Variant ?? "NTS";
        var package = catalog.Packages.Single(p => p.Kind == "php" &&
            p.Branch == branch && p.Variant == variant);
        var installedVersion = managed?.Package.Version ?? source?.Version;
        if (installedVersion != null &&
            Release.Compare(installedVersion, package.Version) > 0)
        {
            if (managed != null) { return; }
            throw new InvalidOperationException("Existing PHP " + installedVersion +
                " is newer than the offered package; refusing a downgrade.");
        }
        if (managed != null && managed.Package.Sha256 == package.Sha256 &&
            managed.ConfigurationMode == request.ConfigurationMode)
        {
            WriteLog("Already installed: PHP " + package.Version + " " + variant);
            return;
        }
        var archive = await Acquire(package, bundle, request.Offline);
        var relative = $"versions/php-{package.Version}-{variant}-" +
            package.Sha256[..12] + "-" + Guid.NewGuid().ToString("N")[..8];
        var directory = SafeFiles.Resolve(root, relative);
        Directory.CreateDirectory(directory);
        try
        {
            var planned = SafeFiles.ArchiveInventory(archive).ToDictionary(
                pair => Path.GetRelativePath(root,
                    SafeFiles.Resolve(directory, pair.Key)), pair => pair.Value);
            transaction.Record(journal, planned);
            SafeFiles.Extract(archive, directory);
            RecordDirectory(directory, state, journal);
            var executable = Path.Combine(directory, "php.exe");
            var target = await Probe(executable, true, false);
            if (target.Bits != 64 || target.Variant != package.Variant ||
                Release.Parse(target.Version).Version !=
                Release.Parse(package.Version).Version)
            {
                throw new InvalidDataException("PHP package identity mismatch");
            }
            var configurationSource = request.ConfigurationMode == "clean"
                ? null : source;
            await Configure(directory, configurationSource, target, journal);
            RecordDirectory(directory, state, journal);
            var actual = await Probe(executable, false, true);
            if (configurationSource != null &&
                configurationSource.Extensions.Except(actual.Extensions,
                StringComparer.OrdinalIgnoreCase).Any())
            {
                throw new InvalidDataException("Migrated PHP is missing required " +
                    "extensions. Previous default has been preserved.");
            }
            state.Php[branch] = new ManagedPhp
            {
                Package = package, Directory = relative,
                ConfigurationMode = request.ConfigurationMode
            };
            WriteLog("Validated PHP " + package.Version + " " + variant);
        }
        finally
        {
            // Record partial extraction/configuration for safe recovery too.
            RecordDirectory(directory, state, journal);
        }
    }

    private async Task InstallComposer(InstallationState state, Journal journal,
        Catalog catalog, DiscoveryReport discovery, string bundle, bool offline)
    {
        var package = catalog.Packages.Single(p => p.Kind == "composer");
        var existing = state.ComposerVersion.Length > 0 ? state.ComposerVersion :
            discovery.ComposerVersion;
        if (existing.Length > 0 && Release.Compare(existing, package.Version) > 0)
        {
            if (state.Composer.Length == 0)
            {
                throw new InvalidOperationException("Existing Composer is newer " +
                    "than the available package; refusing a downgrade.");
            }
        }
        if (discovery.ComposerPath.Length != 0 && existing.Length == 0)
        {
            throw new InvalidOperationException("The existing Composer version " +
                "could not be verified. Repair its command before updating " +
                "to avoid an unintended downgrade.");
        }
        var reuse = state.ComposerVersion.Length != 0 &&
            Release.Compare(state.ComposerVersion, package.Version) >= 0;
        var expectedHash = reuse ? state.Owned[state.Composer] : package.Sha256;
        var archive = reuse ? "" : await Acquire(package, bundle, offline);
        var relative = reuse ? state.Composer :
            $"versions/composer-{package.Version}-{package.Sha256[..12]}/composer.phar";
        var destination = SafeFiles.Resolve(root, relative);
        if (File.Exists(destination))
        {
            SafeFiles.Verify(destination, expectedHash);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            transaction.Record(journal, new() { [relative] = package.Sha256 });
            File.Copy(archive, destination);
        }
        state.Owned[relative] = expectedHash;
        var php = SafeFiles.Resolve(root,
            state.Php[state.DefaultBranch].Directory + "/php.exe");
        await Child(helperExecutable, "validate-composer", "--exe", php, "--phar", destination);
        state.Composer = relative;
        if (!reuse) { state.ComposerVersion = package.Version; }
    }

    private void InstallLaunchers(InstallationState state, Journal journal)
    {
        var binding = SafeFiles.Resolve(root, "bin/installation.json");
        if (File.Exists(binding))
        {
            if (!state.Owned.TryGetValue("bin/installation.json", out var expected))
            {
                throw new IOException("Unmanaged launcher configuration exists");
            }
            SafeFiles.Verify(binding, expected);
            if (!PathChange.Equivalent(JsonFile.Read<LauncherBinding>(binding)
                .DataDirectory, data))
            {
                throw new InvalidDataException("Unexpected launcher state location");
            }
        }
        else
        {
            var value = new LauncherBinding(data);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                value, JsonFile.Options);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            transaction.Record(journal,
                new() { ["bin/installation.json"] = hash });
            JsonFile.Write(binding, value);
        }
        state.Owned["bin/installation.json"] = SafeFiles.Hash(binding);
        var executable = Path.Combine(root, "bin", "PHPUpdater.exe");
        var names = new[] { "php", "composer" }.Concat(state.Php.Keys
            .Select(branch => "php" + branch.Replace(".", "")));
        foreach (var name in names)
        {
            var relative = "bin/" + name + ".exe";
            var target = SafeFiles.Resolve(root, relative);
            if (File.Exists(target))
            {
                if (!state.Owned.TryGetValue(relative, out var hash))
                {
                    throw new IOException("Unmanaged launcher already exists: " + name);
                }
                SafeFiles.Verify(target, hash);
                // All apphosts bind to the same PHPUpdater.dll.
                state.Owned[relative] = SafeFiles.Hash(target);
                continue;
            }
            var fingerprint = SafeFiles.Hash(executable);
            transaction.Record(journal, new() { [relative] = fingerprint });
            File.Copy(executable, target);
            state.Owned[relative] = fingerprint;
        }
        var engineManifest = JsonFile.Read<Dictionary<string, string>>(
            Path.Combine(root, "bin", "engine-files.json"));
        foreach (var (file, hash) in engineManifest)
        {
            var relative = "bin/" + file;
            SafeFiles.Verify(SafeFiles.Resolve(root, relative), hash);
            state.Owned[relative] = hash;
        }
        state.Owned["bin/engine-files.json"] = SafeFiles.Hash(
            Path.Combine(root, "bin", "engine-files.json"));
    }

    public void Uninstall()
    {
        using var operation = Lock();
        transaction.Recover();
        var state = transaction.ReadState();
        var bootstrap = Path.Combine(data, "bootstrap.json");
        if (state == null && !File.Exists(bootstrap)) { return; }
        state ??= new InstallationState
        {
            Owned = JsonFile.Read<Dictionary<string, string>>(bootstrap)
        };
        if (File.Exists(bootstrap))
        {
            foreach (var (file, hash) in
                JsonFile.Read<Dictionary<string, string>>(bootstrap))
            {
                state.Owned[file] = hash;
            }
        }
        SafeFiles.EnsureRemovable(root, state.Owned);
        if (state.PathAdded)
        {
            environment.Path = PathChange.Remove(environment.Path, transaction.Bin);
        }
        var retained = SafeFiles.RemoveOwned(root, state.Owned)
            .Where(p => !p.StartsWith("uninstall" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).ToList();
        File.WriteAllLines(Path.Combine(data, "uninstall-report.txt"),
            new[] { "Original installations were not modified.",
                "Retained modified or unrecognized files:" }.Concat(retained));
        File.Delete(transaction.StatePath);
        File.Delete(bootstrap);
        var cache = Path.Combine(data, "cache");
        if (Directory.Exists(cache))
        {
            foreach (var file in Directory.EnumerateFiles(cache))
            {
                SafeFiles.RejectLinks(file);
                if (Path.GetFileName(file).Equals(SafeFiles.Hash(file),
                    StringComparison.OrdinalIgnoreCase)) { File.Delete(file); }
            }
        }
        WriteLog("Uninstalled managed files. Retained files: " + retained.Count);
    }

    public void Preflight()
    {
        using var operation = Lock();
        transaction.Recover();
        var state = transaction.ReadState();
        if (state == null)
        {
            var bin = Path.Combine(root, "bin");
            if ((Directory.Exists(bin) && Directory.EnumerateFileSystemEntries(bin).Any() &&
                !File.Exists(Path.Combine(data, "bootstrap.json"))) ||
                environment.Path.Split(';').Any(p => PathChange.Equivalent(p, bin)))
            {
                throw new IOException("The destination or PATH entry already exists " +
                    "without managed ownership records. It will not be overwritten.");
            }
            return;
        }
        foreach (var (relative, hash) in state.Owned.Where(p =>
            p.Key.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)))
        {
            var file = SafeFiles.Resolve(root, relative);
            var bootstrap = Path.Combine(data, "bootstrap.json");
            var expected = File.Exists(bootstrap)
                ? JsonFile.Read<Dictionary<string, string>>(bootstrap)
                    .GetValueOrDefault(relative, hash) : hash;
            if (File.Exists(file)) { SafeFiles.Verify(file, expected); }
        }
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(data);
        return new FileStream(Path.Combine(data, "operation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void RecordDirectory(string directory, InstallationState state,
        Journal journal)
    {
        var inventory = SafeFiles.Inventory(directory).ToDictionary(
            pair => Path.GetRelativePath(root, Path.Combine(directory, pair.Key)),
            pair => pair.Value);
        transaction.Record(journal, inventory);
        foreach (var (file, hash) in inventory) { state.Owned[file] = hash; }
    }

    private async Task<string> Acquire(Package package, string bundle, bool offline)
    {
        Catalog.ValidateHash(package.Sha256);
        var bundled = SafeFiles.Resolve(bundle, package.FileName);
        if (File.Exists(bundled))
        {
            SafeFiles.Verify(bundled, package.Sha256);
            return bundled;
        }
        if (offline) { throw new IOException("Package is not bundled: " + package.FileName); }
        var cache = SafeFiles.Resolve(data, "cache/" + package.Sha256);
        if (!File.Exists(cache))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var bytes = await OfficialHttp.Get(package.Url);
            await File.WriteAllBytesAsync(cache + ".part", bytes);
            SafeFiles.Verify(cache + ".part", package.Sha256);
            File.Move(cache + ".part", cache, true);
        }
        SafeFiles.Verify(cache, package.Sha256);
        return cache;
    }

    private async Task<PhpInstallation> Probe(string executable,
        bool bare, bool isolated)
    {
        var response = await Child(helperExecutable, "probe", "--exe", executable,
            "--bare", bare.ToString(), "--isolated", isolated.ToString());
        return System.Text.Json.JsonSerializer.Deserialize<PhpInstallation>(
            response, JsonFile.Options)!;
    }

    private async Task Configure(string directory,
        PhpInstallation? source, PhpInstallation target, Journal journal)
    {
        var work = Path.Combine(Path.GetTempPath(), "PHPUpdater-" + Guid.NewGuid());
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "configuration.json");
        JsonFile.Write(input, new ConfigurationInput(source, target, directory));
        await Child(helperExecutable, "configure", "--input", input, "--work", work);
        var prepared = Path.Combine(work, "prepared");
        SafeFiles.RejectLinks(prepared);
        transaction.Record(journal, new()
        {
            [Path.GetRelativePath(root, Path.Combine(directory, "php.ini"))] =
                SafeFiles.Hash(Path.Combine(prepared, "php.ini"))
        });
        File.Copy(Path.Combine(prepared, "php.ini"),
            Path.Combine(directory, "php.ini"));
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(prepared, "ext"), "*.dll"))
        {
            SafeFiles.RejectLinks(file);
            var destination = Path.Combine(directory, "ext", Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                transaction.Record(journal, new()
                {
                    [Path.GetRelativePath(root, destination)] = SafeFiles.Hash(file)
                });
                File.Copy(file, destination);
            }
        }
        Directory.CreateDirectory(Path.Combine(directory, "conf.d"));
        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(prepared, "conf.d"), "*.ini"))
        {
            SafeFiles.RejectLinks(file);
            var destination = SafeFiles.Resolve(directory,
                "conf.d/" + Path.GetFileName(file));
            transaction.Record(journal, new()
            {
                [Path.GetRelativePath(root, destination)] = SafeFiles.Hash(file)
            });
            File.Copy(file, destination);
        }
        // Scratch files are removed individually; never follow user-created links.
        SafeFiles.RemoveOwned(work, SafeFiles.Inventory(work));
    }

    public static async Task<string> Child(string executable, params string[] arguments)
    {
        var report = Path.Combine(Path.GetTempPath(),
            "PHPUpdater-probe-" + Guid.NewGuid() + ".json");
        var code = await Task.Run(() => WindowsSecurity.RunUnelevated(executable,
            arguments.Concat(["--out", report])));
        try
        {
            if (code != 0)
            {
                var reason = File.Exists(report + ".error") ?
                    File.ReadAllText(report + ".error") : "Probe did not complete";
                throw new InvalidOperationException(reason);
            }
            return File.ReadAllText(report);
        }
        finally
        {
            File.Delete(report);
            File.Delete(report + ".error");
        }
    }

    private void WriteLog(string message)
    {
        File.AppendAllText(Path.Combine(data, "operations.log"),
            $"{DateTimeOffset.UtcNow:O} {message}\n");
        Console.WriteLine(message);
    }

    private void SaveBootstrap()
    {
        var manifest = Path.Combine(root, "bin", "engine-files.json");
        var files = JsonFile.Read<Dictionary<string, string>>(manifest);
        var owned = new Dictionary<string, string>();
        foreach (var (file, hash) in files)
        {
            var relative = "bin/" + file;
            SafeFiles.Verify(SafeFiles.Resolve(root, relative), hash);
            owned.Add(relative, hash);
        }
        owned.Add("bin/engine-files.json", SafeFiles.Hash(manifest));
        JsonFile.Write(Path.Combine(data, "bootstrap.json"), owned);
    }
}

public sealed record ConfigurationInput(PhpInstallation? Source,
    PhpInstallation Target, string Directory);

public sealed record LauncherBinding(string DataDirectory);
