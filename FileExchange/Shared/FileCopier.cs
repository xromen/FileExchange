namespace FileExchange.Shared;

public static class FileCopier
{
    public static string[] Copy(CopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("Папка назначения должна быть указана полным путём.");
        if (request.SourcePaths.Length == 0) throw new ArgumentException("Не выбраны файлы для копирования.");
        Directory.CreateDirectory(request.DestinationPath);
        var errors = new List<string>();
        foreach (string source in request.SourcePaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || !File.Exists(source))
                    throw new FileNotFoundException("Исходный файл не найден.");
                string target = Path.Combine(request.DestinationPath, Path.GetFileName(source));
                File.Copy(source, target, overwrite: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{source}: {error.Message}");
            }
        }
        return errors.ToArray();
    }
}
