using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FileExchange.Shared;

namespace FileExchange;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool copyMode = args.Length > 0 && args[0] == "--copy-request";
        try
        {
            if (copyMode)
            {
                if (args.Length != 2) throw new ArgumentException("Не указан файл задания копирования.");
                return RunCopyRequest(args[1]);
            }
            if (args is ["--self-test"]) return SelfTest.Run();
            if (args is ["--version"])
            {
                Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString(3));
                return 0;
            }
            if (args is ["--install"])
            {
                EnsureInstalled();
                Console.WriteLine($"Меню «{ExchangeStore.ReadParentName()}» зарегистрировано для текущего пользователя.");
                return 0;
            }
            if (args is ["--uninstall"])
            {
                string installed = Path.Combine(ExchangeStore.DataDirectory, "bin");
                RunInstallScript("Uninstall.ps1", File.Exists(Path.Combine(installed, "Uninstall.ps1")) ? installed : AppContext.BaseDirectory);
                Console.WriteLine("Регистрация меню удалена. Список подпунктов сохранён.");
                return 0;
            }
            if (args.Length != 2)
            {
                Console.WriteLine("FileExchange.exe \"Название подпункта\" \"Папка назначения\"");
                Console.WriteLine("FileExchange.exe --install | --uninstall | --self-test | --version");
                return args.Length == 0 ? 0 : 1;
            }
            Destination destination = ExchangeStore.Validate(args[0], args[1]);
            EnsureInstalled();
            ExchangeStore.AddOrUpdate(destination);
            ShellNotification.Refresh();
            Console.WriteLine($"Добавлен или обновлён пункт: {ExchangeStore.ReadParentName()} > {destination.Name}");
            Console.WriteLine($"Папка назначения: {destination.DestinationPath}");
            return 0;
        }
        catch (Exception error)
        {
            if (copyMode) MessageBoxW(0, error.Message, "Файловый обмен", 0x10);
            else Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int RunCopyRequest(string requestPath)
    {
        string path = Path.GetFullPath(requestPath);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(ExchangeStore.RequestsDirectory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Задание должно находиться в папке запросов «Файлового обмена».");
        try
        {
            var request = JsonSerializer.Deserialize(File.ReadAllText(path), ExchangeJsonContext.Default.CopyRequest)
                ?? throw new InvalidDataException("Задание копирования повреждено.");
            CopyResult result = FileCopier.Copy(request, target =>
                MessageBoxW(0, $"В папке назначения уже существует файл:\r\n{target}\r\n\r\nПерезаписать его?",
                    "Файловый обмен — перезапись", 0x00010000 | 0x100 | 0x30 | 0x4) == 6); // Foreground, default No, warning, Yes/No; IDYES.
            MessageBoxW(0, FormatCopySummary(result), "Файловый обмен — результат",
                0x00010000u | (result.Errors.Length == 0 ? 0x40u : 0x30u));
            return result.Errors.Length == 0 ? 0 : 1;
        }
        finally { File.Delete(path); }
    }

    internal static string FormatCopySummary(CopyResult result)
    {
        string summary = $"Копирование завершено.\r\n\r\nСкопировано файлов: {result.Copied}\r\nСоздано папок: {result.CreatedDirectories}\r\nПропущено файлов: {result.Skipped}\r\nОшибок: {result.Errors.Length}";
        return result.Errors.Length == 0 ? summary : summary + "\r\n\r\nНе удалось скопировать:\r\n" + string.Join("\r\n", result.Errors);
    }

    private static void EnsureInstalled()
    {
        string source = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string installed = Path.Combine(ExchangeStore.DataDirectory, "bin");
        string[] required = ["FileExchange.exe", "FileExchange.ShellExtension.dll", "FileExchange.msix", "FileExchange.cer", "Install.ps1", "Uninstall.ps1", "Assets\\Logo44.png", "Assets\\Logo150.png"];
        foreach (string name in required)
            if (!File.Exists(Path.Combine(source, name)))
                throw new FileNotFoundException($"Не найден {name}. Сначала запустите Build.ps1 и используйте EXE из artifacts\\app.");
        Directory.CreateDirectory(installed);
        if (!string.Equals(source, installed, StringComparison.OrdinalIgnoreCase))
        {
            // The manager stays portable: updating the extension must not replace its running EXE.
            foreach (string name in required)
            {
                string file = Path.Combine(source, name);
                string target = Path.Combine(installed, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && FilesEqual(file, target)) continue;
                try { File.Copy(file, target, overwrite: true); }
                catch (IOException error) { throw new IOException("Не удалось обновить установленную программу. Закройте Проводник и повторите запуск.", error); }
            }
        }
        RunInstallScript("Install.ps1", installed);
    }

    private static bool FilesEqual(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var first = File.OpenRead(left);
        using var second = File.OpenRead(right);
        return System.Security.Cryptography.SHA256.HashData(first).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }

    internal static void RunInstallScript(string scriptName, string installDirectory)
    {
        string scriptPath = Path.Combine(installDirectory, scriptName);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException($"Не найден {scriptName}. Используйте опубликованную программу из artifacts\\app.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-InstallDirectory", installDirectory })
            start.ArgumentList.Add(argument);
        if (scriptName == "Install.ps1")
        {
            start.ArgumentList.Add("-ParentMenuName");
            start.ArgumentList.Add(ExchangeStore.ReadParentName());
        }
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить регистрацию меню.");
        // Читаем оба потока одновременно: большой вывод не должен заблокировать дочерний процесс.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = outputTask.GetAwaiter().GetResult();
        string errors = errorTask.GetAwaiter().GetResult();
        string logPath = Path.Combine(installDirectory, Path.GetFileNameWithoutExtension(scriptName) + ".log");
        string diagnostics = $"{scriptName}: код завершения {process.ExitCode}{Environment.NewLine}{output}{Environment.NewLine}{errors}";
        string logMessage;
        try
        {
            File.WriteAllText(logPath, diagnostics, Encoding.UTF8);
            logMessage = $"Журнал: {logPath}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logMessage = $"Не удалось сохранить журнал: {error.Message}";
        }
        if (process.ExitCode != 0)
            throw new IOException($"Ошибка выполнения {scriptName} (код {process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{errors}{Environment.NewLine}{logMessage}");
        if (output.Length > 0) Console.Write(output);
        if (errors.Length > 0) Console.Error.Write(errors);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint window, string text, string caption, uint type);

}
