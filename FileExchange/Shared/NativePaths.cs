using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileExchange.Shared;

internal static unsafe partial class NativePaths
{
    // Resolve existing directory aliases before rejecting a copy into its own source tree.
    internal static string DirectoryPath(string path)
    {
        using var handle = CreateFileW(path, 0, 7, 0, 3, 0x02000000, 0); // OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS.
        if (handle.IsInvalid) throw new IOException($"Не удалось проверить путь папки: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        char* buffer = stackalloc char[32768];
        uint length = GetFinalPathNameByHandleW(handle, buffer, 32768, 0);
        if (length == 0) throw new IOException($"Не удалось получить полный путь папки: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (length >= 32768) throw new IOException("Полный путь папки слишком длинный.");
        return new string(buffer, 0, (int)length).TrimEnd(Path.DirectorySeparatorChar);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);
}
