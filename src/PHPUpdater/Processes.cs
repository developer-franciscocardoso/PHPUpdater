using System.Diagnostics;

namespace PHPUpdater;

public static class Processes
{
    public static async Task<(int Code, string Output, string Error)> Capture(
        string executable, IEnumerable<string> arguments,
        string? iniDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        if (iniDirectory != null) { Isolate(info, iniDirectory); }
        return await Capture(info);
    }

    public static async Task<(int Code, string Output, string Error)> Capture(
        ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info)
            ?? throw new IOException("Unable to start process");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = ReadLimited(process.StandardOutput, timeout.Token);
        var error = ReadLimited(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); }
            throw;
        }
    }

    private static async Task<string> ReadLimited(StreamReader reader,
        CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > 2 * 1024 * 1024)
            {
                throw new InvalidDataException("Process output exceeds limit");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    public static void Isolate(ProcessStartInfo info, string directory)
    {
        info.Environment["PHPRC"] = Path.Combine(directory, "php.ini");
        info.Environment["PHP_INI_SCAN_DIR"] = Path.Combine(directory, "conf.d");
    }
}
