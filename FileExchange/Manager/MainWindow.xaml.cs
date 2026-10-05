using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FileExchange.Shared;
using Microsoft.Win32;

namespace FileExchange.Manager;

public partial class MainWindow : Window
{
    private bool busy;
    private bool checkingUpdate;
    private bool closed;
    private readonly CancellationTokenSource lifetime = new();
    internal string? UpdateResult { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Title += $" · версия {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        CurrentVersionText.Text = $"Текущая версия: {ApplicationUpdater.CurrentVersion}";
        PackageBox.Text = AppContext.BaseDirectory;
        UserText.Text = $"Администратор · Настройки для {WindowsIdentity.GetCurrent().Name}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        if (UpdateResult != null) StatusText.Text = UpdateResult == "success" ? "Приложение обновлено." : "Обновление не завершено. Подробности указаны в журнале обновления.";
        await CheckForUpdates(manual: false, allowPrompt: UpdateResult == null);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdates(manual: true);

    private async Task CheckForUpdates(bool manual, bool allowPrompt = true)
    {
        if (checkingUpdate || busy || closed) return;
        if (!manual && !ApplicationUpdater.StartupCheckEnabled())
        {
            ReleaseVersionText.Text = "Версия в GitHub Releases: проверка при запуске выключена";
            return;
        }
        checkingUpdate = true;
        CheckUpdateButton.IsEnabled = false;
        ReleaseVersionText.Text = "Версия в GitHub Releases: проверяется…";
        ReleaseVersionText.ToolTip = null;
        try
        {
            var release = await ApplicationUpdater.CheckAsync(lifetime.Token);
            if (closed) return;
            ReleaseVersionText.Text = release == null ? "Версия в GitHub Releases: опубликованный релиз недоступен" : $"Версия в GitHub Releases: {release.Version}";
            if (busy) return;
            if (release == null)
            {
                if (manual) MessageBox.Show(this, "Опубликованный релиз недоступен. Проверьте доступ к репозиторию GitHub.", "Файловый обмен — обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (release.Version <= ApplicationUpdater.CurrentVersion)
            {
                if (manual) MessageBox.Show(this, "Установлена актуальная версия приложения.", "Файловый обмен — обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!allowPrompt || !ApplicationUpdater.ShouldOffer(ApplicationUpdater.CurrentVersion, release.Version, manual, ApplicationUpdater.StartupCheckEnabled())) return;
            var answer = MessageBox.Show(this,
                $"Доступна версия {release.Version}. Текущая версия: {ApplicationUpdater.CurrentVersion}.\r\n\r\nОбновить приложение? Оно будет закрыто и запущено снова.\r\n\r\nЕсли выбрать «Нет», проверка обновления при запуске будет выключена. Включить её снова можно в настройках; ручная проверка всегда доступна.",
                "Файловый обмен — обновление", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                Execute(() =>
                {
                    SaveStartupUpdateCheck(false);
                    StatusText.Text = "Проверка обновления при запуске выключена. Ручная проверка доступна.";
                });
                return;
            }
            await UpdateApplication(release);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error)
        {
            if (closed) return;
            ReleaseVersionText.Text = "Версия в GitHub Releases: не удалось проверить";
            ReleaseVersionText.ToolTip = error.Message;
            if (manual) MessageBox.Show(this, $"Не удалось проверить обновление.\r\n{error.Message}", "Файловый обмен — обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            checkingUpdate = false;
            if (!closed) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task UpdateApplication(GitHubRelease release)
    {
        busy = true;
        MainPanel.IsEnabled = false;
        StatusText.Text = $"Загружается и проверяется обновление {release.Version}…";
        try
        {
            string staging = await ApplicationUpdater.DownloadAsync(release, lifetime.Token);
            ApplicationUpdater.StartUpdate(staging, AppContext.BaseDirectory);
            busy = false;
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            StatusText.Text = "Обновление не выполнено.";
            MessageBox.Show(this, error.Message, "Файловый обмен — обновление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { busy = false; if (!closed) MainPanel.IsEnabled = true; }
    }

    private void LoadSettings() => Execute(() =>
    {
        StartupUpdateCheckBox.IsChecked = ApplicationUpdater.StartupCheckEnabled();
        var settings = ExchangeStore.ReadSettings();
        ParentNameBox.Text = settings.ParentName;
        OpenFolderAfterCopyBox.IsChecked = settings.OpenFolderAfterCopy;
        CopyFolderPathAfterCopyBox.IsChecked = settings.CopyFolderPathAfterCopy;
        ReloadList();
        StatusText.Text = $"Загружено пунктов: {DestinationList.Items.Count}.";
    });

    private void ReloadList() => DestinationList.ItemsSource = ExchangeStore.Read();

    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            StatusText.Text = "Операция не выполнена.";
            MessageBox.Show(this, error.Message, "Файловый обмен", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => LoadSettings();

    private void BrowsePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка распакованной сборки FileExchange" };
        if (dialog.ShowDialog(this) == true) PackageBox.Text = dialog.FolderName;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка назначения" };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private void SaveParent_Click(object sender, RoutedEventArgs e) => Execute(() =>
    {
        SaveSettings();
        ParentNameBox.Text = ExchangeStore.ReadParentName();
        ShellNotification.Refresh();
        StatusText.Text = "Настройки меню и копирования сохранены.";
    });

    private void SaveSettings() => ExchangeStore.SaveSettings(new MenuSettings(ParentNameBox.Text,
        OpenFolderAfterCopyBox.IsChecked == true, CopyFolderPathAfterCopyBox.IsChecked == true));

    private void StartupUpdateCheck_Click(object sender, RoutedEventArgs e) => Execute(() =>
    {
        SaveStartupUpdateCheck(StartupUpdateCheckBox.IsChecked == true);
        StatusText.Text = "Настройка проверки обновления при запуске сохранена.";
    });

    internal void SaveStartupUpdateCheck(bool enabled, string? path = null)
    {
        try { ApplicationUpdater.SetStartupCheck(enabled, path); }
        finally { StartupUpdateCheckBox.IsChecked = ApplicationUpdater.StartupCheckEnabled(path); }
    }

    private void DestinationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = DestinationList.SelectedItem as Destination;
        ItemNameBox.Text = selected?.Name ?? "";
        DestinationBox.Text = selected?.DestinationPath ?? "";
        SaveButton.IsEnabled = DeleteButton.IsEnabled = selected != null;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => SaveItem(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is Destination selected) SaveItem(selected.Name);
    }

    private void SaveItem(string? originalName) => Execute(() =>
    {
        ExchangeStore.SaveDestination(originalName, ExchangeStore.Validate(ItemNameBox.Text, DestinationBox.Text));
        ReloadList();
        ShellNotification.Refresh();
        StatusText.Text = originalName == null ? "Пункт добавлен." : "Изменения пункта сохранены.";
    });

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is not Destination selected) return;
        if (MessageBox.Show(this, $"Удалить пункт «{selected.Name}»?", "Файловый обмен", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Execute(() =>
        {
            ExchangeStore.RemoveDestination(selected.Name);
            ReloadList();
            ShellNotification.Refresh();
            StatusText.Text = "Пункт удалён.";
        });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        DestinationList.SelectedItem = null;
        ItemNameBox.Clear();
        DestinationBox.Clear();
        ItemNameBox.Focus();
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunRegistration(install: true);

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить меню из Проводника? Список подпунктов и название сохранятся.", "Файловый обмен",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RunRegistration(install: false);
    }

    private async Task RunRegistration(bool install)
    {
        busy = true;
        MainPanel.IsEnabled = false;
        StatusText.Text = install ? "Выполняется установка / обновление…" : "Выполняется удаление меню…";
        try
        {
            string executable = Path.Combine(PackageBox.Text, "FileExchange.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Выберите папку распакованной сборки с FileExchange.exe.");
            if (install) SaveSettings();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(install ? "--install" : "--uninstall");
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установку.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string diagnostics = (await output) + "\r\n" + (await errors);
            if (process.ExitCode != 0) throw new IOException($"Код завершения: {process.ExitCode}\r\n{diagnostics}");
            ShellNotification.Refresh();
            StatusText.Text = install ? "Меню установлено / обновлено. Настройки подпунктов сохранены." : "Меню удалено. Настройки сохранены.";
            MessageBox.Show(this, StatusText.Text, "Файловый обмен", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            StatusText.Text = "Операция не выполнена.";
            MessageBox.Show(this, error.Message, "Файловый обмен", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { busy = false; MainPanel.IsEnabled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) e.Cancel = true;
        else { closed = true; lifetime.Cancel(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        lifetime.Dispose();
        base.OnClosed(e);
    }
}
