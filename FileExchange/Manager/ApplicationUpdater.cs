using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileExchange.Shared;

namespace FileExchange.Manager;

internal sealed record GitHubRelease(Version Version, Uri DownloadUrl, long Size, string Sha256);

internal static class ApplicationUpdater
{
    internal const string Repository = "xromen/FileExchange";
    internal static readonly Version CurrentVersion = new(typeof(ApplicationUpdater).Assembly.GetName().Version!.ToString(3));
    internal static readonly string[] PayloadFiles = ["FileExchange.exe", "FileExchange.Manager.exe", "FileExchange.ShellExtension.dll",
        "FileExchange.msix", "FileExchange.cer", "Install.ps1", "Uninstall.ps1", "README.md", "Assets/Logo44.png", "Assets/Logo150.png"];
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string SuppressionPath => Path.Combine(ExchangeStore.DataDirectory, "update-prompts-disabled");

    internal static bool StartupCheckEnabled(string? path = null) => !File.Exists(path ?? SuppressionPath);

    internal static void SetStartupCheck(bool enabled, string? path = null)
    {
        path ??= SuppressionPath;
        if (enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Проверка обновления при запуске отключена пользователем.");
    }

    internal static bool ShouldOffer(Version current, Version latest, bool manual, bool startupEnabled) =>
        latest > current && (manual || startupEnabled);

    internal static async Task<GitHubRelease?> CheckAsync(CancellationToken cancellationToken, HttpClient? client = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("FileExchange/" + CurrentVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await (client ?? Client).SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    internal static GitHubRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Обновление должно быть опубликованным стабильным релизом.");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != versionText)
            throw new InvalidDataException("В релизе указана недопустимая версия.");
        string assetName = $"FileExchange-{version}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == assetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException($"В релизе не найден единственный архив {assetName}.");
        var asset = assets[0];
        string urlText = asset.GetProperty("browser_download_url").GetString() ?? "";
        string expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
        if (urlText != expectedUrl || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
            throw new InvalidDataException("Архив обновления должен находиться в релизах исходного репозитория GitHub.");
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (size <= 0 || size > 256L * 1024 * 1024 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Не указан допустимый размер архива или SHA-256 для проверки обновления.");
        return new GitHubRelease(version, url, size, digest[7..]);
    }

    internal static async Task<string> DownloadAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        string staging = Path.Combine(ExchangeStore.DataDirectory, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string archivePath = Path.Combine(staging, "release.zip");
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("FileExchange/" + CurrentVersion);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(archivePath))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("Размер загруженного архива превышает размер релиза.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (total != release.Size) throw new InvalidDataException("Архив обновления загружен не полностью.");
            }
            await Task.Run(() => ValidateAndExtract(archivePath, Path.Combine(staging, "payload"), release), timeout.Token);
            using var script = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream("FileExchange.Update.ps1")
                ?? throw new IOException("Не найден встроенный сценарий обновления.");
            using var reader = new StreamReader(script, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(staging, "Update.ps1"), await reader.ReadToEndAsync(timeout.Token), new UTF8Encoding(true), timeout.Token);
            return staging;
        }
        catch
        {
            // Удаляется только созданный выше каталог с уникальным именем, до запуска установщика.
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    internal static void ValidateAndExtract(string archivePath, string destination, GitHubRelease release)
    {
        using (var stream = File.OpenRead(archivePath))
        {
            if (stream.Length != release.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма архива обновления не совпадает с SHA-256 в GitHub Releases.");
        }
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != PayloadFiles.Length || PayloadFiles.Any(file => archive.Entries.Count(entry => entry.FullName == file) != 1) ||
            archive.Entries.Any(entry => entry.Length <= 0 || entry.Length > 512L * 1024 * 1024) || archive.Entries.Sum(entry => entry.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("Архив обновления содержит неполный или недопустимый набор файлов.");
        Directory.CreateDirectory(destination);
        foreach (string file in PayloadFiles)
        {
            string path = Path.Combine(destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            archive.GetEntry(file)!.ExtractToFile(path);
        }
        ValidatePayloadVersions(destination, release.Version);
    }

    internal static void ValidatePayloadVersions(string directory, Version version)
    {
        foreach (string file in new[] { "FileExchange.exe", "FileExchange.Manager.exe", "FileExchange.ShellExtension.dll" })
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(directory, file));
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) != new Version(version.Major, version.Minor, version.Build, 0))
                throw new InvalidDataException($"Версия {file} не соответствует релизу {version}.");
        }
        using var package = ZipFile.OpenRead(Path.Combine(directory, "FileExchange.msix"));
        using var manifest = package.GetEntry("AppxManifest.xml")?.Open() ?? throw new InvalidDataException("В MSIX отсутствует манифест.");
        var xml = System.Xml.Linq.XDocument.Load(manifest);
        var identity = xml.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "Identity");
        if ((string?)identity?.Attribute("Name") != "FileExchange" || (string?)identity.Attribute("Publisher") != "CN=FileExchange" ||
            (string?)identity.Attribute("Version") != $"{version}.0")
            throw new InvalidDataException("Имя, издатель или версия MSIX не соответствуют приложению и релизу.");
    }

    internal static void StartUpdate(string staging, string targetDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(staging, "Update.ps1"), "-StageDirectory", staging, "-TargetDirectory", targetDirectory,
            "-ParentProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить обновление.");
    }
}
