using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileExchange.Shared;

public sealed record Destination(string Name, string DestinationPath);
public sealed record CopyRequest(string DestinationPath, string[] SourcePaths);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Destination[]))]
[JsonSerializable(typeof(CopyRequest))]
public partial class ExchangeJsonContext : JsonSerializerContext;

public static class ExchangeStore
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExchangeMenu");
    public static string ConfigPath => Path.Combine(DataDirectory, "destinations.json");
    public static string RequestsDirectory => Path.Combine(DataDirectory, "requests");

    public static Destination[] Read(string? configPath = null)
    {
        string path = configPath ?? ConfigPath;
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize(File.ReadAllText(path), ExchangeJsonContext.Default.DestinationArray)
            ?? throw new InvalidDataException("Список подпунктов повреждён.");
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
                string.IsNullOrWhiteSpace(entry.DestinationPath) || !Path.IsPathFullyQualified(entry.DestinationPath))
                throw new InvalidDataException("В списке подпунктов указано недопустимое имя или путь.");
        }
        return entries;
    }

    public static Destination Validate(string name, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Название подпункта не должно быть пустым.");
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Папка назначения не должна быть пустой.");
        string path = Path.GetFullPath(destinationPath);
        if (File.Exists(path)) throw new ArgumentException("Путь назначения указывает на файл.");
        return new Destination(name.Trim(), path);
    }

    public static void AddOrUpdate(Destination destination, string? configPath = null)
    {
        string path = configPath ?? ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Общая блокировка сериализует одновременные изменения списка подпунктов.
        using var mutex = new Mutex(false, @"Local\FileExchangeMenu.Settings");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Другой процесс ещё изменяет список подпунктов. Повторите запуск.");
        try
        {
            var entries = Read(path).ToList();
            int index = entries.FindIndex(entry => string.Equals(entry.Name, destination.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) entries.Add(destination);
            else entries[index] = destination;
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries.ToArray(), ExchangeJsonContext.Default.DestinationArray));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
