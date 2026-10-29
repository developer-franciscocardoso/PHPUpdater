using System.Text.Json;

namespace PHPUpdater;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(
            Path.GetFullPath(path))!);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("JSON file exceeds size limit");
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Missing JSON content");
    }
}
