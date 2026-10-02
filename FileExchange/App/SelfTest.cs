using FileExchange.Shared;

namespace FileExchange;

internal static class SelfTest
{
    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileExchange-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "destinations.json");
            string target = Path.Combine(root, "Обмен [1] & O'Brien");
            ExchangeStore.AddOrUpdate(ExchangeStore.Validate("Архив", target), config);
            ExchangeStore.AddOrUpdate(ExchangeStore.Validate("Сервер", Path.Combine(root, "server")), config);
            ExchangeStore.AddOrUpdate(ExchangeStore.Validate("АРХИВ", target), config);
            Destination[] entries = ExchangeStore.Read(config);
            Check(entries.Length == 2 && entries[0].Name == "АРХИВ", "Повторный запуск создал дубликат подпункта.");

            string[] sources = Enumerable.Range(0, 25).Select(index => Path.Combine(root, $"файл [{index}] & $.txt")).ToArray();
            foreach (string source in sources) File.WriteAllText(source, $"Содержимое: {Path.GetFileName(source)}");
            Check(FileCopier.Copy(new CopyRequest(target, sources)).Length == 0, "Копирование нескольких файлов завершилось ошибкой.");
            foreach (string source in sources)
                Check(File.ReadAllText(source) == File.ReadAllText(Path.Combine(target, Path.GetFileName(source))), "Содержимое копии не совпало.");

            string existingTarget = Path.Combine(target, Path.GetFileName(sources[0]));
            string originalContent = File.ReadAllText(existingTarget);
            File.WriteAllText(sources[0], "Новая версия");
            string extra = Path.Combine(root, "после ошибки.txt");
            File.WriteAllText(extra, "OK");
            string[] errors = FileCopier.Copy(new CopyRequest(target, [sources[0], Path.Combine(root, "missing.txt"), root, extra]));
            Check(errors.Length == 3 && File.Exists(Path.Combine(target, Path.GetFileName(extra))), "Ошибка остановила весь список или не была обнаружена.");
            Check(File.ReadAllText(existingTarget) == originalContent, "Существующий файл был перезаписан.");
            Check(FileCopier.Copy(new CopyRequest(root, [extra])).Length == 1 && File.ReadAllText(extra) == "OK", "Копирование в исходную папку изменило файл.");
            File.WriteAllText(config, "повреждённый JSON");
            bool rejected = false;
            try { ExchangeStore.Read(config); }
            catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected && File.ReadAllText(config) == "повреждённый JSON", "Повреждённый конфиг был затёрт.");

            File.WriteAllText(Path.Combine(root, "Fail.ps1"), """
                param([string]$InstallDirectory)
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                [Console]::Out.WriteLine('Вывод регистрации')
                [Console]::Error.WriteLine('Ошибка регистрации')
                exit 7
                """, new System.Text.UTF8Encoding(true));
            string? registrationError = null;
            try { Program.RunInstallScript("Fail.ps1", root); }
            catch (IOException error) { registrationError = error.Message; }
            Check(registrationError != null && registrationError.Contains("код 7") &&
                registrationError.Contains("Вывод регистрации") && registrationError.Contains("Ошибка регистрации"),
                "Ошибка регистрации потеряла код завершения или вывод скрипта.");
            string registrationLog = File.ReadAllText(Path.Combine(root, "Fail.log"));
            Check(registrationLog.Contains("Вывод регистрации") && registrationLog.Contains("Ошибка регистрации"),
                "Журнал регистрации потерял вывод скрипта.");
            Console.WriteLine("OK: 25 файлов, специальные символы, ошибки, защита от перезаписи, добавление и обновление подпунктов, журнал регистрации.");
            return 0;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимая папка проверки.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
