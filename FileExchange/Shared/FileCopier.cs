using System.IO;

namespace FileExchange.Shared;

public sealed record CopyResult(int Copied, int Skipped, string[] Errors, int CreatedDirectories = 0);

public static class FileCopier
{
    public static CopyResult Copy(CopyRequest request, Func<string, bool> confirmOverwrite)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        ArgumentNullException.ThrowIfNull(confirmOverwrite);
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("Папка назначения должна быть указана полным путём.");
        if (request.SourcePaths.Length == 0) throw new ArgumentException("Не выбраны файлы или папки для копирования.");
        string destination = Path.GetFullPath(request.DestinationPath);
        string? physicalDestination = null;
        try { Directory.CreateDirectory(destination); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new CopyResult(0, 0, [$"{destination}: {error.Message}"]);
        }
        var errors = new List<string>();
        int copied = 0, skipped = 0, createdDirectories = 0;
        var pending = new Queue<(string Source, string Target)>();
        foreach (string source in request.SourcePaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
                    throw new ArgumentException("Исходный путь должен быть полным.");
                string fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                string name = Path.GetFileName(fullSource);
                if (name.Length == 0) throw new IOException("Копирование корня диска или сетевого ресурса не поддерживается.");
                string target = Path.Combine(destination, name);
                if (string.Equals(fullSource, target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Исходный путь и путь назначения совпадают.");
                if (Directory.Exists(fullSource))
                {
                    physicalDestination ??= NativePaths.DirectoryPath(destination);
                    string physicalSource = NativePaths.DirectoryPath(fullSource);
                    string physicalTarget = Directory.Exists(target) ? NativePaths.DirectoryPath(target) :
                        physicalDestination + Path.DirectorySeparatorChar + name;
                    if (string.Equals(physicalSource, physicalTarget, StringComparison.OrdinalIgnoreCase) ||
                        physicalTarget.StartsWith(physicalSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Нельзя копировать папку в саму себя или в её вложенную папку.");
                }
                pending.Enqueue((fullSource, target));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{source}: {error.Message}");
            }
        }
        while (pending.TryDequeue(out var entry))
        {
            try
            {
                bool directory = (File.GetAttributes(entry.Source) & FileAttributes.Directory) != 0;
                FileSystemInfo info = directory ? new DirectoryInfo(entry.Source) : new FileInfo(entry.Source);
                if (info.LinkTarget != null) throw new IOException("Символические ссылки и соединения папок не копируются.");
                if (directory)
                {
                    var targetInfo = new DirectoryInfo(entry.Target);
                    if (targetInfo.LinkTarget != null) throw new IOException("Папка назначения является ссылкой или соединением.");
                    bool existed = targetInfo.Exists;
                    Directory.CreateDirectory(entry.Target);
                    if (!existed) createdDirectories++;
                    foreach (string child in Directory.EnumerateFileSystemEntries(entry.Source))
                        pending.Enqueue((child, Path.Combine(entry.Target, Path.GetFileName(child))));
                    continue;
                }
                if (new FileInfo(entry.Target).LinkTarget != null) throw new IOException("Файл назначения является символической ссылкой.");
                try { File.Copy(entry.Source, entry.Target, overwrite: false); }
                catch (IOException) when (File.Exists(entry.Target))
                {
                    if (!confirmOverwrite(entry.Target)) { skipped++; continue; }
                    File.Copy(entry.Source, entry.Target, overwrite: true);
                }
                copied++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{entry.Source}: {error.Message}");
            }
        }
        return new CopyResult(copied, skipped, errors.ToArray(), createdDirectories);
    }
}
