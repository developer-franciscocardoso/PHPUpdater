namespace PHPUpdater;

public static class OfficialHttp
{
    private static readonly HashSet<string> Hosts =
    [
        "downloads.php.net", "windows.php.net", "www.php.net", "php.net",
        "getcomposer.org", "aka.ms", "download.visualstudio.microsoft.com",
        "download.microsoft.com"
    ];

    public static void Validate(Uri uri)
    {
        if (uri.Scheme != "https" || !Hosts.Contains(uri.IdnHost) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException("Unapproved download origin");
        }
    }

    public static async Task<byte[]> Get(string address,
        long limit = 512L * 1024 * 1024)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PHPUpdater/1.0");
        var uri = new Uri(address);
        for (var redirects = 0; redirects < 6; redirects++)
        {
            Validate(uri);
            using var response = await client.GetAsync(uri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = new Uri(uri, response.Headers.Location ??
                    throw new InvalidDataException("Missing redirect location"));
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var data = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (data.Length + count > limit)
                {
                    throw new InvalidDataException("Download exceeds size limit");
                }
                data.Write(buffer, 0, count);
            }
            return data.ToArray();
        }
        throw new InvalidDataException("Too many redirects");
    }

    public static async Task<string> Text(string address) =>
        System.Text.Encoding.UTF8.GetString(await Get(address, 8 * 1024 * 1024));
}
