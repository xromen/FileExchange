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

    public MainWindow()
    {
        InitializeComponent();
        PackageBox.Text = AppContext.BaseDirectory;
        UserText.Text = $"Администратор · Настройки для {WindowsIdentity.GetCurrent().Name}";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => LoadSettings();

    private void LoadSettings() => Execute(() =>
    {
        ParentNameBox.Text = ExchangeStore.ReadParentName();
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
        ExchangeStore.SetParentName(ParentNameBox.Text);
        ParentNameBox.Text = ExchangeStore.ReadParentName();
        ShellNotification.Refresh();
        StatusText.Text = "Название меню сохранено.";
    });

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
            if (install) ExchangeStore.SetParentName(ParentNameBox.Text);
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
    }
}
