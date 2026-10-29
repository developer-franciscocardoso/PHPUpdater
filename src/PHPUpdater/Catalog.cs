using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PHPUpdater;

public sealed record Catalog
{
    public DateTimeOffset BuiltAt { get; init; }
    public bool Online { get; set; }
    public string Notice { get; set; } = "";
    public List<Package> Packages { get; init; } = [];
    public static List<Package> ParsePhp(string json,
        Dictionary<string, DateOnly> supported)
    {
        using var document = JsonDocument.Parse(json);
        var packages = new List<Package>();
        foreach (var (branch, end) in supported)
        {
            var release = document.RootElement.GetProperty(branch);
            var version = release.GetProperty("version").GetString()!;
            Release.Parse(version);
            if (Release.Branch(version) != branch)
            {
                throw new InvalidDataException("PHP release branch mismatch");
            }
            foreach (var build in release.EnumerateObject())
            {
                var match = Regex.Match(build.Name, @"^(nts|ts)-(v[cs]\d+)-x64$");
                if (!match.Success) { continue; }
                var zip = build.Value.GetProperty("zip");
                var name = zip.GetProperty("path").GetString()!;
                if (!Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+\.zip$"))
                {
                    throw new InvalidDataException("Invalid PHP archive name");
                }
                var hash = zip.GetProperty("sha256").GetString()!;
                ValidateHash(hash);
                packages.Add(new Package
                {
                    Version = version, Branch = branch, SupportEnd = end,
                    Variant = match.Groups[1].Value.ToUpperInvariant(),
                    Compiler = match.Groups[2].Value,
                    Released = DateTimeOffset.Parse(
                        build.Value.GetProperty("mtime").GetString()!,
                        CultureInfo.InvariantCulture),
                    FileName = name, Sha256 = hash,
                    Url = "https://downloads.php.net/~windows/releases/" + name
                });
            }
            if (packages.Count(p => p.Branch == branch) != 2)
            {
                throw new InvalidDataException("Missing TS/NTS PHP builds");
            }
        }
        return packages;
    }

    public static Dictionary<string, DateOnly> ParseSupport(string html)
    {
        var result = new Dictionary<string, DateOnly>();
        foreach (Match row in Regex.Matches(html,
            "<tr class=\"(?:stable|security)\">(.*?)</tr>",
            RegexOptions.Singleline))
        {
            var branch = Regex.Match(row.Value, @"version=(\d+\.\d+)");
            var dates = Regex.Matches(row.Value,
                @"<td>\s*(\d{1,2} [A-Za-z]{3} \d{4})\s*</td>");
            if (!branch.Success || dates.Count != 3) { continue; }
            result.Add(branch.Groups[1].Value, DateOnly.ParseExact(
                dates[2].Groups[1].Value.Trim(), "d MMM yyyy",
                CultureInfo.InvariantCulture));
        }
        if (result.Count == 0)
        {
            throw new InvalidDataException("Unrecognized PHP support table");
        }
        return result;
    }

    public static async Task<Catalog> Fetch()
    {
        var support = ParseSupport(await OfficialHttp.Text(
            "https://www.php.net/supported-versions.php"));
        var packages = ParsePhp(await OfficialHttp.Text(
            "https://downloads.php.net/~windows/releases/releases.json"), support);
        using var composer = JsonDocument.Parse(await OfficialHttp.Text(
            "https://getcomposer.org/versions"));
        var stable = composer.RootElement.GetProperty("stable")
            .EnumerateArray().First(p => !p.GetProperty("lts").GetBoolean());
        var version = stable.GetProperty("version").GetString()!;
        Release.Parse(version);
        var url = $"https://getcomposer.org/download/{version}/composer.phar";
        var checksum = (await OfficialHttp.Text(url + ".sha256sum"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        ValidateHash(checksum);
        packages.Add(new Package
        {
            Kind = "composer", Version = version, Url = url,
            FileName = $"composer-{version}.phar", Sha256 = checksum
        });
        return new Catalog
        {
            BuiltAt = DateTimeOffset.UtcNow, Online = true, Packages = packages,
            Notice = "Checked official sources online."
        };
    }

    public static async Task<Catalog> Load(string bundle, bool offline)
    {
        var bundled = JsonFile.Read<Catalog>(Path.Combine(bundle, "manifest.json"));
        if (!offline)
        {
            try
            {
                var live = await Fetch();
                live.Packages.AddRange(bundled.Packages.Where(p => p.Kind == "runtime"));
                return live;
            }
            catch (Exception error) when (error is HttpRequestException or
                TaskCanceledException)
            {
                bundled.Notice = "Online check unavailable. ";
            }
        }
        bundled.Online = false;
        bundled.Notice = $"Offline bundle from {bundled.BuiltAt:yyyy-MM-dd}. " +
            "Latest releases could not be verified.";
        return bundled;
    }

    public string[] Branches() => Packages.Where(p => p.Kind == "php" &&
        p.SupportEnd >= DateOnly.FromDateTime(DateTime.UtcNow))
        .Select(p => p.Branch).Distinct().OrderBy(Version.Parse).ToArray();

    public static void ValidateHash(string hash)
    {
        if (!Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$"))
        {
            throw new InvalidDataException("Invalid SHA-256 checksum");
        }
    }
}
