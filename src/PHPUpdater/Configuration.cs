using System.Text.RegularExpressions;

namespace PHPUpdater;

public static class Configuration
{
    public static string Rewrite(string ini, string source, string target)
    {
        return Regex.Replace(ini,
            @"(?im)^\s*(extension_dir|extension|zend_extension)\s*=.*$",
            match => match.Value.Replace(source.Replace('\\', '/'),
                target.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                .Replace(source, target.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanReuseExtensionBinaries(PhpInstallation source,
        PhpInstallation target) => source.Branch == target.Branch &&
        source.Bits == target.Bits && source.Build.Length > 0 &&
        source.Build == target.Build && source.Variant == target.Variant;

    public static void Prepare(string directory, PhpInstallation? source,
        PhpInstallation target)
    {
        Directory.CreateDirectory(Path.Combine(directory, "conf.d"));
        var ini = Path.Combine(directory, "php.ini");
        if (source == null)
        {
            var template = File.ReadAllText(
                Path.Combine(directory, "php.ini-production"));
            template = Regex.Replace(template,
                @"(?m)^;extension=(curl|mbstring|openssl|zip)(?=\r?$)",
                "extension=$1");
            File.WriteAllText(ini, template + "\n" +
                "extension_dir=\"" + directory.Replace('\\', '/') + "/ext\"\n");
            return;
        }
        if (source.Branch != target.Branch)
        {
            throw new InvalidDataException("Configuration migration requires the same PHP branch.");
        }
        var oldRoot = Path.GetDirectoryName(source.Executable)!;
        var content = source.Ini.Length == 0 ? "" : ReadIni(source.Ini);
        File.WriteAllText(ini, MigrateIni(content, oldRoot, directory, source, target));
        for (var index = 0; index < source.ScannedIni.Length; index++)
        {
            var scanned = source.ScannedIni[index];
            var destination = SafeFiles.Resolve(directory,
                $"conf.d/{index:D4}-{Path.GetFileName(scanned)}");
            File.WriteAllText(destination, MigrateIni(ReadIni(scanned),
                oldRoot, directory, source, target));
        }
    }

    private static string MigrateIni(string content, string oldRoot,
        string directory, PhpInstallation source, PhpInstallation target)
    {
        content = NormalizeExtensions(content, directory, source, target);
        content = Rewrite(content, oldRoot, directory);
        var extensionDirectory = "extension_dir=\"" +
            directory.Replace('\\', '/') + "/ext\"";
        content = Regex.Replace(content, @"(?im)^\s*extension_dir\s*=.*$",
            extensionDirectory);
        return extensionDirectory + "\n" + content;
    }

    private static string ReadIni(string path)
    {
        SafeFiles.RejectLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("INI file exceeds size limit");
        }
        return File.ReadAllText(path);
    }

    private static string NormalizeExtensions(string content, string directory,
        PhpInstallation source, PhpInstallation target)
    {
        return Regex.Replace(content,
            @"(?im)^\s*(zend_extension|extension)\s*=\s*([^;\r\n]+)(.*)$",
            match =>
            {
                var value = match.Groups[2].Value.Trim().Trim('"', '\'');
                if (value.Contains('$'))
                {
                    throw new InvalidDataException("Variable extension paths " +
                        "must be resolved before migration.");
                }
                var name = Path.GetFileName(value);
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    name = "php_" + name + ".dll";
                }
                var module = name[..^4];
                if (module.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
                {
                    module = module[4..];
                }
                if (module.Equals("opcache", StringComparison.OrdinalIgnoreCase))
                {
                    module = "Zend OPcache";
                }
                if (target.Extensions.Contains(module,
                    StringComparer.OrdinalIgnoreCase))
                {
                    return "; " + module + " is compiled into this PHP build.";
                }
                var destination = SafeFiles.Resolve(directory, "ext/" + name);
                if (!File.Exists(destination))
                {
                    if (!CanReuseExtensionBinaries(source, target))
                    {
                        throw new InvalidDataException(
                            "Cannot reuse third-party extension " + name +
                            $" from PHP {source.Version} {source.Variant}" +
                            $" ({source.Bits}-bit, {source.Build}) with" +
                            $" PHP {target.Version} {target.Variant}" +
                            $" ({target.Bits}-bit, {target.Build}). " +
                            "Select a matching build, supply a compatible DLL " +
                            "in the source configuration, or explicitly choose " +
                            "clean configuration in setup.");
                    }
                    var original = Path.IsPathRooted(value) ? value :
                        Path.Combine(source.ExtensionDirectory, name);
                    SafeFiles.RejectLinks(original);
                    if (Discovery.PeBits(original) != 64)
                    {
                        throw new InvalidDataException("Extension is not x64: " + name);
                    }
                    File.Copy(original, destination);
                }
                return match.Groups[1].Value + "=\"" +
                    destination.Replace('\\', '/') + "\"";
            });
    }
}
