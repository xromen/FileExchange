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
            var settings = ExchangeStore.ReadSettings();
            CopyResult result = FileCopier.Copy(request, CreateOverwriteConfirmation(OverwriteDialog.Show));
            string[] actionErrors = RunPostCopyActions(request, result, settings, CopyFolderPath,
                folder => { using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); });
            string summary = FormatCopySummary(result);
            if (actionErrors.Length > 0) summary += "\r\n\r\nНе удалось выполнить действия после копирования:\r\n" + string.Join("\r\n", actionErrors);
            bool success = result.Errors.Length == 0 && actionErrors.Length == 0;
            MessageBoxW(0, summary, "Файловый обмен — результат", 0x00010000u | (success ? 0x40u : 0x30u));
            return success ? 0 : 1;
        }
        finally { File.Delete(path); }
    }

    internal static Func<string, string, bool> CreateOverwriteConfirmation(Func<string, string, OverwriteChoice> showDialog)
    {
        bool? overwriteAll = null;
        return (source, target) =>
        {
            if (overwriteAll.HasValue) return overwriteAll.Value;
            OverwriteChoice choice = showDialog(source, target);
            if (choice == OverwriteChoice.YesToAll) overwriteAll = true;
            if (choice == OverwriteChoice.NoToAll) overwriteAll = false;
            return choice is OverwriteChoice.Yes or OverwriteChoice.YesToAll;
        };
    }

    internal static string[] RunPostCopyActions(CopyRequest request, CopyResult result, MenuSettings settings,
        Action<string> copyPath, Action<string> openFolder)
    {
        if (result.Copied == 0 && result.CreatedDirectories == 0) return [];
        string folder = Path.GetFullPath(request.DestinationPath);
        var errors = new List<string>();
        if (settings.CopyFolderPathAfterCopy)
        {
            try { copyPath(folder); }
            catch (Exception error) { errors.Add($"Копирование пути в буфер обмена: {error.Message}"); }
        }
        if (settings.OpenFolderAfterCopy)
        {
            try { openFolder(folder); }
            catch (Exception error) { errors.Add($"Открытие папки: {error.Message}"); }
        }
        return errors.ToArray();
    }

    private static void CopyFolderPath(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "clip.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, StandardInputEncoding = Encoding.Unicode,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить копирование пути.");
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(folder);
        process.StandardInput.Close();
        process.WaitForExit();
        string diagnostics = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"Код завершения: {process.ExitCode}. {diagnostics}");
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
                try { ReplaceInstalledFile(file, target); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Не удалось обновить установленный файл: {target}\r\n{error.Message}", error);
                }
            }
        }
        RunInstallScript("Install.ps1", installed);
    }

    internal static void ReplaceInstalledFile(string source, string target)
    {
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        // Загруженная DLL остаётся в резервном файле до освобождения её Проводником.
        foreach (string old in Directory.EnumerateFiles(directory, Path.GetFileName(target) + ".*.old"))
        {
            string name = Path.GetFileName(old), prefix = Path.GetFileName(target) + ".";
            if (name.Length != prefix.Length + 36 || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _)) continue;
            try { File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (File.Exists(target) && FilesEqual(source, target)) return;
        string suffix = "." + Guid.NewGuid().ToString("N");
        string temporary = target + suffix + ".tmp", backup = target + suffix + ".old";
        try
        {
            File.Copy(source, temporary);
            if (File.Exists(target)) File.Replace(temporary, target, backup);
            else File.Move(temporary, target);
        }
        catch
        {
            if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        try { File.Delete(backup); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
