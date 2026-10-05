using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileExchange.Shared;

public sealed record Destination(string Name, string DestinationPath);
public sealed record CopyRequest(string DestinationPath, string[] SourcePaths);
public sealed record MenuSettings(string ParentName, bool OpenFolderAfterCopy = false, bool CopyFolderPathAfterCopy = false);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Destination[]))]
[JsonSerializable(typeof(CopyRequest))]
[JsonSerializable(typeof(MenuSettings))]
public partial class ExchangeJsonContext : JsonSerializerContext;

public static class ExchangeStore
{
    public const string DefaultParentName = "Файловый обмен";
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExchangeMenu");
    public static string ConfigPath => Path.Combine(DataDirectory, "destinations.json");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
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

    public static string ReadParentName(string? settingsPath = null) => ReadSettings(settingsPath).ParentName;

    public static MenuSettings ReadSettings(string? settingsPath = null)
    {
        string path = settingsPath ?? SettingsPath;
        if (!File.Exists(path)) return new MenuSettings(DefaultParentName);
        var settings = JsonSerializer.Deserialize(File.ReadAllText(path), ExchangeJsonContext.Default.MenuSettings)
            ?? throw new InvalidDataException("Настройки меню повреждены.");
        return settings with { ParentName = ValidateParentName(settings.ParentName) };
    }

    public static string ValidateParentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Название родительского меню не должно быть пустым.");
        return name.Trim();
    }

    public static void SetParentName(string name, string? settingsPath = null)
    {
        name = ValidateParentName(name);
        string path = settingsPath ?? SettingsPath;
        WithLock(path, () =>
        {
            var settings = ReadSettings(path) with { ParentName = name };
            WriteAtomic(path, JsonSerializer.Serialize(settings, ExchangeJsonContext.Default.MenuSettings));
        });
    }

    public static void SaveSettings(MenuSettings settings, string? settingsPath = null)
    {
        settings = settings with { ParentName = ValidateParentName(settings.ParentName) };
        string path = settingsPath ?? SettingsPath;
        WithLock(path, () => WriteAtomic(path, JsonSerializer.Serialize(settings, ExchangeJsonContext.Default.MenuSettings)));
    }

    public static void AddOrUpdate(Destination destination, string? configPath = null) => UpdateEntries(configPath, entries =>
    {
        destination = Validate(destination.Name, destination.DestinationPath);
        int index = IndexOf(entries, destination.Name);
        if (index < 0) entries.Add(destination);
        else entries[index] = destination;
    });

    public static void SaveDestination(string? originalName, Destination destination, string? configPath = null) => UpdateEntries(configPath, entries =>
    {
        destination = Validate(destination.Name, destination.DestinationPath);
        int index = originalName == null ? -1 : IndexOf(entries, originalName);
        if (originalName != null && index < 0) throw new InvalidOperationException("Пункт уже удалён. Обновите список.");
        int duplicate = IndexOf(entries, destination.Name);
        if (duplicate >= 0 && duplicate != index) throw new ArgumentException("Пункт с таким названием уже существует.");
        if (index < 0) entries.Add(destination);
        else entries[index] = destination;
    });

    public static void RemoveDestination(string name, string? configPath = null) => UpdateEntries(configPath, entries =>
    {
        int index = IndexOf(entries, name);
        if (index < 0) throw new InvalidOperationException("Пункт уже удалён. Обновите список.");
        entries.RemoveAt(index);
    });

    private static int IndexOf(List<Destination> entries, string name) =>
        entries.FindIndex(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void UpdateEntries(string? configPath, Action<List<Destination>> change)
    {
        string path = configPath ?? ConfigPath;
        WithLock(path, () =>
        {
            var entries = Read(path).ToList();
            change(entries);
            WriteAtomic(path, JsonSerializer.Serialize(entries.ToArray(), ExchangeJsonContext.Default.DestinationArray));
        });
    }

    private static void WithLock(string path, Action change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Общая блокировка сериализует одновременные изменения списка подпунктов.
        using var mutex = new Mutex(false, @"Local\FileExchangeMenu.Settings");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Другой процесс ещё изменяет список подпунктов. Повторите запуск.");
        try { change(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
