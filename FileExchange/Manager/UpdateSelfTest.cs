using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileExchange.Manager;

internal static class UpdateSelfTest
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileExchange-updates-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string suppressed = Path.Combine(root, "update-prompts-disabled");
            Check(ApplicationUpdater.StartupCheckEnabled(suppressed), "Проверка выключена при первом запуске.");
            ApplicationUpdater.SetStartupCheck(false, suppressed);
            Check(!ApplicationUpdater.StartupCheckEnabled(suppressed), "Отказ от обновления не сохранён.");
            ApplicationUpdater.SetStartupCheck(true, suppressed);
            Check(ApplicationUpdater.StartupCheckEnabled(suppressed) && !File.Exists(suppressed), "Не удалось снова включить проверку обновления.");
            ApplicationUpdater.SetStartupCheck(false, suppressed);
            Check(!ApplicationUpdater.StartupCheckEnabled(suppressed), "Повторное отключение проверки не сохранено.");
            var current = new Version(1, 0, 9);
            var next = new Version(1, 0, 10);
            Check(!ApplicationUpdater.ShouldOffer(current, next, false, false) && !ApplicationUpdater.ShouldOffer(current, new Version(1, 0, 11), false, false) &&
                ApplicationUpdater.ShouldOffer(current, next, true, false) && ApplicationUpdater.ShouldOffer(current, next, false, true) &&
                !ApplicationUpdater.ShouldOffer(current, current, true, true) && !ApplicationUpdater.ShouldOffer(next, current, false, true),
                "Неверно обработаны отказ, ручная проверка или сравнение версий.");
            string json = ReleaseJson(next, "abcdef".PadRight(64, '0'), 123);
            using var client = new HttpClient(new ReleaseHandler(HttpStatusCode.OK, json));
            Check(ApplicationUpdater.CheckAsync(CancellationToken.None, client).GetAwaiter().GetResult()?.Version == next, "Не прочитан релиз из ответа GitHub.");
            using var notFound = new HttpClient(new ReleaseHandler(HttpStatusCode.NotFound, ""));
            Check(ApplicationUpdater.CheckAsync(CancellationToken.None, notFound).GetAwaiter().GetResult() == null, "404 не обработан как отсутствие доступного релиза.");
            using var limited = new HttpClient(new ReleaseHandler(HttpStatusCode.Forbidden, ""));
            Expect<HttpRequestException>(() => ApplicationUpdater.CheckAsync(CancellationToken.None, limited).GetAwaiter().GetResult());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Expect<OperationCanceledException>(() => ApplicationUpdater.CheckAsync(canceled.Token, client).GetAwaiter().GetResult());
            Expect<InvalidDataException>(() => ApplicationUpdater.ParseRelease(json.Replace("\"draft\":false", "\"draft\":true")));
            Expect<InvalidDataException>(() => ApplicationUpdater.ParseRelease(json.Replace("\"prerelease\":false", "\"prerelease\":true")));
            Expect<InvalidDataException>(() => ApplicationUpdater.ParseRelease(json.Replace("v1.0.10", "v1.0.10-preview")));
            Expect<InvalidDataException>(() => ApplicationUpdater.ParseRelease(json.Replace("github.com/xromen/FileExchange/", "example.com/xromen/FileExchange/")));
            Expect<InvalidDataException>(() => ApplicationUpdater.ParseRelease(json.Replace("sha256:", "sha512:")));
            Expect<JsonException>(() => ApplicationUpdater.ParseRelease("not-json"));

            var version = ApplicationUpdater.CurrentVersion;
            string archivePath = Path.Combine(root, "release.zip");
            BuildArchive(archivePath, version);
            var release = ArchiveRelease(archivePath, version);
            string extracted = Path.Combine(root, "extracted");
            ApplicationUpdater.ValidateAndExtract(archivePath, extracted, release);
            Check(ApplicationUpdater.PayloadFiles.All(file => File.Exists(Path.Combine(extracted, file))), "Архив распакован не полностью.");
            Expect<InvalidDataException>(() => ApplicationUpdater.ValidateAndExtract(archivePath, Path.Combine(root, "wrong-hash"), release with { Sha256 = new string('0', 64) }));
            Check(!Directory.Exists(Path.Combine(root, "wrong-hash")), "Архив с неверной суммой был распакован.");
            Expect<InvalidDataException>(() => ApplicationUpdater.ValidatePayloadVersions(extracted, new Version(version.Major, version.Minor, version.Build + 1)));
            BuildArchive(archivePath, version, extra: "../escape.txt");
            Expect<InvalidDataException>(() => ApplicationUpdater.ValidateAndExtract(archivePath, Path.Combine(root, "unsafe"), ArchiveRelease(archivePath, version)));
            Check(!File.Exists(Path.Combine(root, "escape.txt")), "Архив вышел за пределы папки обновления.");
            BuildArchive(archivePath, version, omit: "Install.ps1");
            Expect<InvalidDataException>(() => ApplicationUpdater.ValidateAndExtract(archivePath, Path.Combine(root, "incomplete"), ArchiveRelease(archivePath, version)));
            using var script = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream("FileExchange.Update.ps1");
            Check(script != null && script.Length > 0, "Сценарий обновления не встроен в приложение.");
            Console.WriteLine("OK: обновления — ответы GitHub, версии, сохранение отказа и ручная проверка, SHA-256, состав ZIP, защита путей, версии бинарных файлов и MSIX; сеть и установка не использовались.");
        }
        finally
        {
            string allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимая папка проверки обновления.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReleaseJson(Version version, string hash, long size) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, draft = false, prerelease = false,
        assets = new[] { new { name = $"FileExchange-{version}-win-x64.zip", size, digest = "sha256:" + hash,
            browser_download_url = $"https://github.com/{ApplicationUpdater.Repository}/releases/download/v{version}/FileExchange-{version}-win-x64.zip" } }
    });

    private static GitHubRelease ArchiveRelease(string path, Version version) => new(version,
        new Uri($"https://github.com/{ApplicationUpdater.Repository}/releases/download/v{version}/FileExchange-{version}-win-x64.zip"),
        new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void BuildArchive(string path, Version version, string? extra = null, string? omit = null)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string file in ApplicationUpdater.PayloadFiles.Where(file => file != omit).Concat(extra == null ? [] : new[] { extra }))
        {
            using var entry = archive.CreateEntry(file).Open();
            if (file.EndsWith(".exe") || file.EndsWith(".dll")) entry.Write(File.ReadAllBytes(typeof(ApplicationUpdater).Assembly.Location));
            else if (file.EndsWith(".msix"))
            {
                using var package = new ZipArchive(entry, ZipArchiveMode.Create);
                using var manifest = new StreamWriter(package.CreateEntry("AppxManifest.xml").Open());
                manifest.Write($"<Package><Identity Name=\"FileExchange\" Publisher=\"CN=FileExchange\" Version=\"{version}.0\" /></Package>");
            }
            else entry.Write(Encoding.UTF8.GetBytes("Synthetic updater test file"));
        }
    }

    private sealed class ReleaseHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.RequestUri?.AbsoluteUri == $"https://api.github.com/repos/{ApplicationUpdater.Repository}/releases/latest" && request.Headers.UserAgent.Count > 0,
                "Неверный адрес запроса обновления или отсутствует User-Agent.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Ожидалась ошибка {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
