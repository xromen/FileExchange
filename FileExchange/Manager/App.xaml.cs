using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileExchange.Shared;

namespace FileExchange.Manager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Read-only check via dotnet DLL; never shows a window or touches real settings.
        if (e.Args.Length > 0 && e.Args[0] == "--self-test")
        {
            try
            {
                var window = new MainWindow();
                if (window.ParentNameBox.Text != ExchangeStore.DefaultParentName || !window.DestinationList.IsReadOnly)
                    throw new InvalidOperationException("Не задано название меню по умолчанию или список допускает несохранённое редактирование.");
                window.DestinationList.ItemsSource = new[] { new Destination("На сервер", @"\\server\share\Обмен"), new Destination("В архив", @"D:\Архив") };
                window.DestinationList.SelectedIndex = 0;
                if (window.ItemNameBox.Text != "На сервер" || window.DestinationBox.Text != @"\\server\share\Обмен")
                    throw new InvalidOperationException("Выбор пункта не заполнил поля редактирования.");
                var content = (FrameworkElement)window.Content;
                content.Margin = new Thickness(0);
                content.Measure(new Size(920, 720));
                content.Arrange(new Rect(0, 0, 920, 720));
                content.UpdateLayout();
                Console.WriteLine("OK: WPF-окно создано, название по умолчанию и выбор пункта проверены; установка не выполнялась.");
                if (e.Args.Length == 2)
                {
                    var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(content);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.GetFullPath(e.Args[1]));
                    png.Save(file);
                }
                Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Shutdown(1);
            }
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Запустите приложение от имени администратора.", "Файловый обмен", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        new MainWindow().Show();
    }
}
