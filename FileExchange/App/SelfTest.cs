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
            string settings = Path.Combine(root, "settings.json");
            Check(ExchangeStore.ReadParentName(settings) == "Файловый обмен", "Не задано название меню по умолчанию.");
            ExchangeStore.SetParentName("  Передать файлы  ", settings);
            Check(ExchangeStore.ReadParentName(settings) == "Передать файлы", "Название родителя не сохранено.");
            string savedSettings = File.ReadAllText(settings);
            bool invalidName = false;
            try { ExchangeStore.SetParentName("   ", settings); }
            catch (ArgumentException) { invalidName = true; }
            Check(invalidName && File.ReadAllText(settings) == savedSettings, "Пустое название затёрло настройки.");
            string guiConfig = Path.Combine(root, "gui-destinations.json");
            var first = ExchangeStore.Validate("Первый", Path.Combine(root, "first"));
            ExchangeStore.SaveDestination(null, first, guiConfig);
            ExchangeStore.SaveDestination(null, ExchangeStore.Validate("Второй", Path.Combine(root, "second")), guiConfig);
            ExchangeStore.SaveDestination("Первый", ExchangeStore.Validate("Переименован", target), guiConfig);
            Check(ExchangeStore.Read(guiConfig)[0] == new Destination("Переименован", target), "Редактирование создало новый пункт или потеряло папку.");
            string savedEntries = File.ReadAllText(guiConfig);
            bool duplicate = false;
            try { ExchangeStore.SaveDestination("Переименован", ExchangeStore.Validate("ВТОРОЙ", target), guiConfig); }
            catch (ArgumentException) { duplicate = true; }
            Check(duplicate && File.ReadAllText(guiConfig) == savedEntries, "Переименование затёрло другой пункт.");
            duplicate = false;
            try { ExchangeStore.SaveDestination(null, ExchangeStore.Validate("второй", target), guiConfig); }
            catch (ArgumentException) { duplicate = true; }
            Check(duplicate && File.ReadAllText(guiConfig) == savedEntries, "Добавление затёрло существующий пункт.");
            ExchangeStore.RemoveDestination("переименован", guiConfig);
            Check(ExchangeStore.Read(guiConfig).Length == 1 && ExchangeStore.Read(guiConfig)[0].Name == "Второй", "Удалён неверный пункт.");
            ExchangeStore.RemoveDestination("Второй", guiConfig);
            Check(ExchangeStore.Read(guiConfig).Length == 0, "Последний пункт не удалён.");

            string[] sources = Enumerable.Range(0, 25).Select(index => Path.Combine(root, $"файл [{index}] & $.txt")).ToArray();
            foreach (string source in sources) File.WriteAllText(source, $"Содержимое: {Path.GetFileName(source)}");
            CopyResult result = FileCopier.Copy(new CopyRequest(target, sources), _ => throw new InvalidOperationException("Запрошена перезапись нового файла."));
            Check(result.Copied == 25 && result.Skipped == 0 && result.Errors.Length == 0, "Неверный итог копирования нескольких файлов.");
            Check(Program.FormatCopySummary(result).Contains("Скопировано файлов: 25") && Program.FormatCopySummary(result).Contains("Ошибок: 0"),
                "Итог успешного копирования не содержит количество файлов.");
            foreach (string source in sources)
                Check(File.ReadAllText(source) == File.ReadAllText(Path.Combine(target, Path.GetFileName(source))), "Содержимое копии не совпало.");

            string existingTarget = Path.Combine(target, Path.GetFileName(sources[0]));
            string originalContent = File.ReadAllText(existingTarget);
            File.WriteAllText(sources[0], "Новая версия");
            string extra = Path.Combine(root, "после ошибки.txt");
            File.WriteAllText(extra, "OK");
            int prompts = 0;
            result = FileCopier.Copy(new CopyRequest(target, [sources[0], Path.Combine(root, "missing.txt"), root, extra]), path =>
            {
                prompts++;
                Check(path == existingTarget, "Запрос перезаписи содержит неверный путь.");
                return false;
            });
            Check(prompts == 1 && result.Copied == 1 && result.Skipped == 1 && result.Errors.Length == 2 && File.Exists(Path.Combine(target, Path.GetFileName(extra))),
                "Отказ или ошибка остановили список либо итоговые счётчики неверны.");
            Check(File.ReadAllText(existingTarget) == originalContent, "Файл был перезаписан без согласия.");
            string summary = Program.FormatCopySummary(result);
            Check(summary.Contains("Скопировано файлов: 1") && summary.Contains("Пропущено файлов: 1") && summary.Contains("Ошибок: 2") && summary.Contains("missing.txt"),
                "Итог частичного копирования не содержит счётчики и ошибки.");
            prompts = 0;
            result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), path => { prompts++; return path == existingTarget; });
            Check(prompts == 1 && result.Copied == 1 && result.Skipped == 0 && result.Errors.Length == 0 && File.ReadAllText(existingTarget) == "Новая версия",
                "Подтверждённая перезапись не выполнена или не учтена.");
            Check(File.ReadAllText(sources[0]) == "Новая версия", "Перезапись изменила исходный файл.");
            result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), _ => false);
            Check(result.Copied == 0 && result.Skipped == 1 && result.Errors.Length == 0 && Program.FormatCopySummary(result).Contains("Скопировано файлов: 0"),
                "Отказ от всех перезаписей не учтён в итоговом сообщении.");
            result = FileCopier.Copy(new CopyRequest(root, [extra]), _ => throw new InvalidOperationException("Запрошена перезапись исходного файла самим собой."));
            Check(result.Copied == 0 && result.Errors.Length == 1 && File.ReadAllText(extra) == "OK", "Копирование в исходную папку изменило файл.");
            File.SetAttributes(existingTarget, FileAttributes.ReadOnly);
            try
            {
                result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), _ => true);
                Check(result.Copied == 0 && result.Errors.Length == 1 && File.ReadAllText(existingTarget) == "Новая версия",
                    "Неудачная перезапись учтена как успешная или изменила защищённый файл.");
            }
            finally { File.SetAttributes(existingTarget, FileAttributes.Normal); }
            result = FileCopier.Copy(new CopyRequest(extra, [sources[0]]), _ => throw new InvalidOperationException("Запрошена перезапись при недоступной папке назначения."));
            Check(result.Copied == 0 && result.Skipped == 0 && result.Errors.Length == 1 && Program.FormatCopySummary(result).Contains("Скопировано файлов: 0"),
                "Ошибка создания папки назначения не содержит итог копирования.");
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
            Console.WriteLine("OK: название родителя, добавление/изменение/удаление и дубликаты, 25 файлов, перезапись, итоги, ошибки, защита исходников, журнал регистрации.");
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
