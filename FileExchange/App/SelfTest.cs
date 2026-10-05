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
            Check(ExchangeStore.ReadSettings(settings) == new MenuSettings("Файловый обмен"), "Неверные настройки копирования по умолчанию.");
            File.WriteAllText(settings, """{ "ParentName": "Файловый обмен" }""");
            Check(ExchangeStore.ReadSettings(settings) == new MenuSettings("Файловый обмен"), "Старые настройки несовместимы с чекбоксами.");
            ExchangeStore.SaveSettings(new MenuSettings("Файловый обмен", true, true), settings);
            ExchangeStore.SetParentName("  Передать файлы  ", settings);
            Check(ExchangeStore.ReadSettings(settings) == new MenuSettings("Передать файлы", true, true), "Изменение названия сбросило настройки копирования.");
            string savedSettings = File.ReadAllText(settings);
            bool invalidName = false;
            try { ExchangeStore.SetParentName("   ", settings); }
            catch (ArgumentException) { invalidName = true; }
            Check(invalidName && File.ReadAllText(settings) == savedSettings, "Пустое название затёрло настройки.");
            var actionRequest = new CopyRequest(target, []);
            foreach (bool open in new[] { false, true })
            foreach (bool copyPath in new[] { false, true })
            {
                var options = new MenuSettings("Передать файлы", open, copyPath);
                ExchangeStore.SaveSettings(options, settings);
                Check(ExchangeStore.ReadSettings(settings) == options, "Состояния чекбоксов не сохранены.");
                var actions = new List<string>();
                string[] actionErrors = Program.RunPostCopyActions(actionRequest, new CopyResult(1, 0, []),
                    ExchangeStore.ReadSettings(settings), folder => actions.Add("copy:" + folder), folder => actions.Add("open:" + folder));
                string[] expected = (copyPath ? new[] { "copy:" + target } : Array.Empty<string>())
                    .Concat(open ? new[] { "open:" + target } : Array.Empty<string>()).ToArray();
                Check(actionErrors.Length == 0 && actions.SequenceEqual(expected), "Выполнены неверные действия после копирования.");
            }
            var enabled = new MenuSettings("Передать файлы", true, true);
            foreach (var noCopies in new[] { new CopyResult(0, 1, []), new CopyResult(0, 0, ["Ошибка"]) })
                Check(Program.RunPostCopyActions(actionRequest, noCopies, enabled,
                    _ => throw new IOException("Нельзя копировать путь."), _ => throw new IOException("Нельзя открывать папку.")).Length == 0,
                    "Действия запущены без скопированных объектов.");
            foreach (var copies in new[] { new CopyResult(1, 0, ["Ошибка другого файла"]), new CopyResult(0, 0, [], 1) })
            {
                bool opened = false;
                var actionErrors = Program.RunPostCopyActions(actionRequest, copies, enabled,
                    _ => throw new IOException("Буфер недоступен"), _ => opened = true);
                Check(opened && actionErrors.Length == 1 && actionErrors[0].Contains("Буфер недоступен"),
                    "Ошибка буфера остановила открытие папки или потерялась; частичный успех и пустые папки должны учитываться.");
            }
            Check(Program.RunPostCopyActions(actionRequest, new CopyResult(1, 0, []), enabled,
                _ => { }, _ => throw new IOException("Проводник недоступен")).Single().Contains("Проводник недоступен"),
                "Ошибка открытия папки потерялась.");
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
            CopyResult result = FileCopier.Copy(new CopyRequest(target, sources), (_, _) => throw new InvalidOperationException("Запрошена перезапись нового файла."));
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
            result = FileCopier.Copy(new CopyRequest(target, [sources[0], Path.Combine(root, "missing.txt"), root, extra]), (_, path) =>
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
            result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), (_, path) => { prompts++; return path == existingTarget; });
            Check(prompts == 1 && result.Copied == 1 && result.Skipped == 0 && result.Errors.Length == 0 && File.ReadAllText(existingTarget) == "Новая версия",
                "Подтверждённая перезапись не выполнена или не учтена.");
            Check(File.ReadAllText(sources[0]) == "Новая версия", "Перезапись изменила исходный файл.");
            result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), (_, _) => false);
            Check(result.Copied == 0 && result.Skipped == 1 && result.Errors.Length == 0 && Program.FormatCopySummary(result).Contains("Скопировано файлов: 0"),
                "Отказ от всех перезаписей не учтён в итоговом сообщении.");
            result = FileCopier.Copy(new CopyRequest(root, [extra]), (_, _) => throw new InvalidOperationException("Запрошена перезапись исходного файла самим собой."));
            Check(result.Copied == 0 && result.Errors.Length == 1 && File.ReadAllText(extra) == "OK", "Копирование в исходную папку изменило файл.");
            File.SetAttributes(existingTarget, FileAttributes.ReadOnly);
            try
            {
                result = FileCopier.Copy(new CopyRequest(target, [sources[0]]), (_, _) => true);
                Check(result.Copied == 0 && result.Errors.Length == 1 && File.ReadAllText(existingTarget) == "Новая версия",
                    "Неудачная перезапись учтена как успешная или изменила защищённый файл.");
            }
            finally { File.SetAttributes(existingTarget, FileAttributes.Normal); }
            result = FileCopier.Copy(new CopyRequest(extra, [sources[0]]), (_, _) => throw new InvalidOperationException("Запрошена перезапись при недоступной папке назначения."));
            Check(result.Copied == 0 && result.Skipped == 0 && result.Errors.Length == 1 && Program.FormatCopySummary(result).Contains("Скопировано файлов: 0"),
                "Ошибка создания папки назначения не содержит итог копирования.");
            string tree = Path.Combine(root, "Папка [1] & обмен");
            string deep = Path.Combine(tree, "Вложенная", "Глубже");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(Path.Combine(tree, "Пустая"));
            string treeFile = Path.Combine(tree, "корень.txt");
            string hiddenFile = Path.Combine(deep, "скрытый.txt");
            File.WriteAllText(treeFile, "Корень");
            File.WriteAllText(hiddenFile, "Глубокий файл");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);
            string recursiveTarget = Path.Combine(root, "recursive");
            result = FileCopier.Copy(new CopyRequest(recursiveTarget, [tree + Path.DirectorySeparatorChar, extra]),
                (_, _) => throw new InvalidOperationException("Запрошена перезапись в новой структуре."));
            string copiedTree = Path.Combine(recursiveTarget, Path.GetFileName(tree));
            string copiedTreeFile = Path.Combine(copiedTree, "корень.txt");
            string copiedHidden = Path.Combine(copiedTree, "Вложенная", "Глубже", "скрытый.txt");
            Check(result.Copied == 3 && result.CreatedDirectories == 4 && result.Errors.Length == 0 &&
                File.ReadAllText(copiedHidden) == "Глубокий файл" && Directory.Exists(Path.Combine(copiedTree, "Пустая")) &&
                File.ReadAllText(Path.Combine(recursiveTarget, Path.GetFileName(extra))) == "OK", "Папка, пустые каталоги, скрытые файлы или смешанное выделение скопированы не полностью.");
            Check(Program.FormatCopySummary(result).Contains("Создано папок: 4"), "Созданные папки не попали в итог.");
            File.WriteAllText(treeFile, "Обновление");
            File.WriteAllText(Path.Combine(copiedTree, "только в назначении.txt"), "Сохранить");
            prompts = 0;
            result = FileCopier.Copy(new CopyRequest(recursiveTarget, [tree]), (_, path) =>
            {
                prompts++;
                return path != copiedTreeFile;
            });
            Check(prompts == 2 && result.Copied == 1 && result.Skipped == 1 && result.CreatedDirectories == 0 && result.Errors.Length == 0 &&
                File.ReadAllText(copiedTreeFile) == "Корень", "Слияние папок не запросило перезапись каждого файла или проигнорировало отказ.");
            result = FileCopier.Copy(new CopyRequest(recursiveTarget, [tree]), (_, _) => true);
            Check(result.Copied == 2 && result.Errors.Length == 0 && File.ReadAllText(copiedTreeFile) == "Обновление" &&
                File.ReadAllText(Path.Combine(copiedTree, "только в назначении.txt")) == "Сохранить", "Слияние потеряло файлы назначения или не выполнило подтверждённую перезапись.");
            TestOverwriteChoices(root);
            result = FileCopier.Copy(new CopyRequest(deep, [tree]), (_, _) => true);
            Check(result.Copied == 0 && result.Errors.Length == 1 && !Directory.Exists(Path.Combine(deep, Path.GetFileName(tree))),
                "Копирование папки в саму себя запустило рекурсию.");
            string blocked = Path.Combine(root, "blocked");
            Directory.CreateDirectory(blocked);
            string conflict = Path.Combine(blocked, Path.GetFileName(tree));
            File.WriteAllText(conflict, "Существующий файл");
            result = FileCopier.Copy(new CopyRequest(blocked, [tree, extra]), (_, _) => true);
            Check(result.Copied == 1 && result.Errors.Length == 1 && File.ReadAllText(conflict) == "Существующий файл",
                "Конфликт файла с папкой затёр данные или остановил остальные объекты.");
            File.WriteAllText(Path.Combine(root, "MakeJunction.ps1"), """
                param([string]$InstallDirectory)
                $ErrorActionPreference = 'Stop'
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $source = Join-Path $InstallDirectory 'link-source'
                New-Item -ItemType Junction -Path (Join-Path $InstallDirectory 'alias') -Target $source | Out-Null
                New-Item -ItemType Junction -Path (Join-Path $source 'cycle') -Target $source | Out-Null
                """, new System.Text.UTF8Encoding(true));
            string linkSource = Path.Combine(root, "link-source");
            Directory.CreateDirectory(linkSource);
            File.WriteAllText(Path.Combine(linkSource, "inside.txt"), "Проверка соединений");
            Program.RunInstallScript("MakeJunction.ps1", root);
            try
            {
                result = FileCopier.Copy(new CopyRequest(Path.Combine(root, "alias"), [linkSource]), (_, _) => true);
                Check(result.Copied == 0 && result.Errors.Length == 1, "Соединение папок обошло защиту копирования в себя.");
                result = FileCopier.Copy(new CopyRequest(Path.Combine(root, "with-link"), [linkSource]), (_, _) => true);
                Check(result.Copied == 1 && result.Errors.Length == 1 && !Directory.Exists(Path.Combine(root, "with-link", Path.GetFileName(linkSource), "cycle")),
                    "Циклическое соединение запустило бесконечное копирование или остановило обычные файлы.");
            }
            finally
            {
                Directory.Delete(Path.Combine(linkSource, "cycle"));
                Directory.Delete(Path.Combine(root, "alias"));
            }
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
            Console.WriteLine("OK: настройки, 25 файлов, рекурсивные и пустые папки, смешанное выделение, слияние, четыре варианта перезаписи и подробности файлов, защита от рекурсии и ссылок, итоги, журнал регистрации.");
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

    private static void TestOverwriteChoices(string root)
    {
        Check(System.Runtime.InteropServices.Marshal.SizeOf<OverwriteDialog.TaskDialogConfig>() == (IntPtr.Size == 8 ? 160 : 96) &&
            System.Runtime.InteropServices.Marshal.SizeOf<OverwriteDialog.TaskDialogButton>() == IntPtr.Size + 4,
            "Структуры системного диалога не соответствуют ABI Windows.");
        string source = Path.Combine(root, "Новые файлы [1] & $");
        string target = Path.Combine(root, "Существующие файлы");
        Directory.CreateDirectory(Path.Combine(source, "Вложенная"));
        string[] relativePaths = ["первый.txt", "второй.txt", Path.Combine("Вложенная", "третий.txt")];
        foreach (string relative in relativePaths) File.WriteAllText(Path.Combine(source, relative), "Новая версия");
        var request = new CopyRequest(target, [source]);
        FileCopier.Copy(request, (_, _) => throw new InvalidOperationException("Совпадений пока нет."));
        string targetTree = Path.Combine(target, Path.GetFileName(source));
        var modified = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, relativePaths[0]), modified);
        string details = OverwriteDialog.FormatDetails(Path.Combine(source, relativePaths[0]), Path.Combine(targetTree, relativePaths[0]));
        Check(details.Contains("Существующий файл (будет заменён)") && details.Contains("Новый файл (будет скопирован)") &&
            details.Contains(Path.Combine(source, relativePaths[0])) && details.Contains(Path.Combine(targetTree, relativePaths[0])) &&
            details.Contains("Размер: 23 байт") && details.Contains($"Изменён: {modified.ToLocalTime():dd.MM.yyyy HH:mm:ss}") && details.Contains("Создан:"),
            "Подробности не содержат пути, размер или даты обоих файлов.");
        Check(OverwriteDialog.FormatDetails(Path.Combine(root, "missing-details.txt"), Path.Combine(targetTree, relativePaths[0]))
            .Contains("Информация недоступна:"), "Отсутствие метаданных остановило диалог.");
        foreach (OverwriteChoice choice in Enum.GetValues<OverwriteChoice>())
        {
            foreach (string relative in relativePaths) File.WriteAllText(Path.Combine(targetTree, relative), "Старая версия");
            string extra = Path.Combine(source, $"новый-{choice}.txt");
            File.WriteAllText(extra, "Без совпадения");
            int prompts = 0;
            var confirm = Program.CreateOverwriteConfirmation((newFile, oldFile) =>
            {
                prompts++;
                Check(oldFile == Path.Combine(targetTree, Path.GetRelativePath(source, newFile)), "В диалог переданы неверные исходный или конечный пути.");
                return choice;
            });
            CopyResult result = FileCopier.Copy(request, confirm);
            bool overwrite = choice is OverwriteChoice.Yes or OverwriteChoice.YesToAll;
            Check(prompts == (choice is OverwriteChoice.YesToAll or OverwriteChoice.NoToAll ? 1 : 3) &&
                result.Copied == (overwrite ? 4 : 1) && result.Skipped == (overwrite ? 0 : 3) && result.Errors.Length == 0,
                $"Неверно обработан выбор {choice}, в том числе во вложенной папке.");
            foreach (string relative in relativePaths)
                Check(File.ReadAllText(Path.Combine(targetTree, relative)) == (overwrite ? "Новая версия" : "Старая версия"), "Перезаписан или пропущен неверный файл.");
            Check(File.ReadAllText(Path.Combine(targetTree, Path.GetFileName(extra))) == "Без совпадения", "Выбор для всех затронул файл без совпадения.");
            File.Delete(extra);
        }
        var responses = new Queue<OverwriteChoice>([OverwriteChoice.Yes, OverwriteChoice.No, OverwriteChoice.YesToAll]);
        var mixed = Program.CreateOverwriteConfirmation((_, _) => responses.Dequeue());
        Check(mixed("new", "old") && !mixed("new", "old") && mixed("new", "old") && mixed("new", "old") && responses.Count == 0,
            "Одиночный ответ применился ко всем файлам или выбор для всех не запомнился.");
        Check(!Program.CreateOverwriteConfirmation((_, _) => OverwriteChoice.No)("new", "old"), "Выбор для всех сохранился между операциями.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
