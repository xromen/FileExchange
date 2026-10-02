namespace FileExchange.Shared;

public sealed record CopyResult(int Copied, int Skipped, string[] Errors);

public static class FileCopier
{
    public static CopyResult Copy(CopyRequest request, Func<string, bool> confirmOverwrite)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        ArgumentNullException.ThrowIfNull(confirmOverwrite);
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("Папка назначения должна быть указана полным путём.");
        if (request.SourcePaths.Length == 0) throw new ArgumentException("Не выбраны файлы для копирования.");
        string destination = Path.GetFullPath(request.DestinationPath);
        try { Directory.CreateDirectory(destination); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new CopyResult(0, 0, [$"{destination}: {error.Message}"]);
        }
        var errors = new List<string>();
        int copied = 0, skipped = 0;
        foreach (string source in request.SourcePaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || !File.Exists(source))
                    throw new FileNotFoundException("Исходный файл не найден.");
                string target = Path.Combine(destination, Path.GetFileName(source));
                if (string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Исходный файл и файл назначения совпадают.");
                try { File.Copy(source, target, overwrite: false); }
                catch (IOException) when (File.Exists(target))
                {
                    if (!confirmOverwrite(target)) { skipped++; continue; }
                    File.Copy(source, target, overwrite: true);
                }
                copied++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{source}: {error.Message}");
            }
        }
        return new CopyResult(copied, skipped, errors.ToArray());
    }
}
