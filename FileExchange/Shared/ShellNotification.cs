using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileExchange.Shared;

public static partial class ShellNotification
{
    public static void Refresh()
    {
        // Windows 10 may take the parent title from the registry rather than IExplorerCommand.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\FileExchange.Menu", writable: true);
        key?.SetValue("MUIVerb", ExchangeStore.ReadParentName());
        SHChangeNotify(0x08000000, 0, 0, 0);
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
