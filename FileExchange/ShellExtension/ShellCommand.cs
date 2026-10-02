using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileExchange.Shared;

namespace FileExchange.ShellExtension;

internal static unsafe class Com
{
    public const int Ok = 0, False = 1;
    public const int Pointer = unchecked((int)0x80004003);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArgument = unchecked((int)0x80070057);
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static int Interface(object value, Guid iid, nint* result)
    {
        if (result == null) return Pointer;
        *result = 0;
        nint unknown = 0;
        try
        {
            unknown = Wrappers.GetOrCreateComInterfaceForObject(value, CreateComInterfaceFlags.None);
            return Marshal.QueryInterface(unknown, in iid, out *result);
        }
        catch (Exception e) { return e.HResult; }
        finally { if (unknown != 0) Marshal.Release(unknown); }
    }

    public static int String(string value, nint* result)
    {
        if (result == null) return Pointer;
        *result = 0;
        try { *result = Marshal.StringToCoTaskMemUni(value); return Ok; }
        catch (Exception e) { return e.HResult; }
    }

    public static void Check(int result)
    {
        if (result < 0) throw new COMException("Ошибка интерфейса Проводника.", result);
    }
}

internal static unsafe class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* result)
    {
        if (result == null) return Com.Pointer;
        *result = 0;
        if (clsid == null || iid == null) return Com.Pointer;
        try
        {
            if (*clsid != ComIds.ExplorerCommandClsid) return unchecked((int)0x80040111);
            return Com.Interface(new CommandFactory(), *iid, result);
        }
        catch (Exception e) { return e.HResult; }
    }

    // NativeAOT libraries cannot be unloaded safely; Explorer releases them on exit.
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllCanUnloadNow() => Com.False;
}

[GeneratedComClass]
internal unsafe partial class CommandFactory : IClassFactory
{
    public int CreateInstance(nint outer, Guid* iid, nint* result)
    {
        if (result == null) return Com.Pointer;
        *result = 0;
        if (iid == null) return Com.Pointer;
        if (outer != 0) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        try { return Com.Interface(new ShellCommand(), *iid, result); }
        catch (Exception e) { return e.HResult; }
    }

    public int LockServer(int @lock) => Com.Ok;
}

[GeneratedComClass]
internal unsafe partial class ShellCommand : IExplorerCommand
{
    private readonly Destination? destination;
    public ShellCommand(Destination? destination = null) => this.destination = destination;

    public int GetTitle(nint items, nint* title)
    {
        if (title == null) return Com.Pointer;
        *title = 0;
        try { return Com.String(destination?.Name ?? ExchangeStore.ReadParentName(), title); }
        catch (Exception error) { return error.HResult; }
    }

    public int GetIcon(nint items, nint* icon)
    {
        if (icon == null) return Com.Pointer;
        *icon = 0;
        try { return Com.String(Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",167", icon); }
        catch (Exception e) { return e.HResult; }
    }

    public int GetToolTip(nint items, nint* tooltip)
    {
        if (tooltip == null) return Com.Pointer;
        *tooltip = 0;
        try { return Com.String(destination == null ? "Копирование выделенных файлов и папок" : "Копировать в " + destination.DestinationPath, tooltip); }
        catch (Exception e) { return e.HResult; }
    }

    public int GetCanonicalName(Guid* name)
    {
        if (name == null) return Com.Pointer;
        *name = Guid.Empty;
        try
        {
            *name = destination == null ? ComIds.ExplorerCommandClsid :
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("FileExchange:" + destination.Name.ToUpperInvariant())).AsSpan(0, 16));
            return Com.Ok;
        }
        catch (Exception e) { return e.HResult; }
    }

    public int GetState(nint items, int okToBeSlow, uint* state)
    {
        if (state == null) return Com.Pointer;
        *state = 2; // ECS_HIDDEN
        if (items == 0) return Com.Ok;
        // Defer shell attribute queries and local configuration IO off the UI thread.
        if (okToBeSlow == 0) return unchecked((int)0x8000000A); // E_PENDING
        try
        {
            if ((destination != null || ExchangeStore.Read().Length != 0) && Selection.IsFileSystemItems(items))
                *state = 0; // ECS_ENABLED
            return Com.Ok;
        }
        catch (Exception e) { return e.HResult; }
    }

    public int Invoke(nint items, nint bindContext)
    {
        if (destination == null) return Com.NotImplemented;
        string? requestPath = null;
        try
        {
            if (!Selection.IsFileSystemItems(items)) return Com.InvalidArgument;
            string[] paths = Selection.FilePaths(items);
            string requests = ExchangeStore.RequestsDirectory;
            Directory.CreateDirectory(requests);
            requestPath = Path.Combine(requests, Guid.NewGuid().ToString("N") + ".json");
            var request = new CopyRequest(destination.DestinationPath, paths);
            using (var stream = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, request, ExchangeJsonContext.Default.CopyRequest);
            var start = new ProcessStartInfo(Path.Combine(NativeModule.DirectoryPath(), "FileExchange.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--copy-request");
            start.ArgumentList.Add(requestPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить копирование.");
            return Com.Ok;
        }
        catch (Exception e)
        {
            if (requestPath != null)
            {
                try { File.Delete(requestPath); }
                catch { /* Preserve the actual launch/serialization failure. */ }
            }
            return e.HResult;
        }
    }

    public int GetFlags(uint* flags)
    {
        if (flags == null) return Com.Pointer;
        *flags = destination == null ? 1u : 0u; // ECF_HASSUBCOMMANDS
        return Com.Ok;
    }

    public int EnumSubCommands(nint* enumerator)
    {
        if (enumerator == null) return Com.Pointer;
        *enumerator = 0;
        if (destination != null) return Com.NotImplemented;
        try { return Com.Interface(new CommandEnumerator(ExchangeStore.Read()), typeof(IEnumExplorerCommand).GUID, enumerator); }
        catch (Exception e) { return e.HResult; }
    }
}

[GeneratedComClass]
internal unsafe partial class CommandEnumerator(Destination[] destinations, int position = 0) : IEnumExplorerCommand
{
    private int position = position;

    public int Next(uint count, nint* commands, uint* fetched)
    {
        if (fetched != null) *fetched = 0;
        if (count == 0) return Com.Ok;
        if (commands == null) return Com.Pointer;
        uint written = 0;
        try
        {
            while (written < count && position < destinations.Length)
            {
                commands[written] = 0;
                int hr = Com.Interface(new ShellCommand(destinations[position]), typeof(IExplorerCommand).GUID, &commands[written]);
                Com.Check(hr);
                position++;
                written++;
            }
            if (fetched != null) *fetched = written;
            return written == count ? Com.Ok : Com.False;
        }
        catch (Exception e)
        {
            // On failure no partially returned interface pointers are handed to the caller.
            for (uint i = 0; i < written; i++) { Marshal.Release(commands[i]); commands[i] = 0; }
            position -= (int)written;
            return e.HResult;
        }
    }

    public int Skip(uint count)
    {
        int remaining = destinations.Length - position;
        position += (int)Math.Min(count, (uint)remaining);
        return count <= remaining ? Com.Ok : Com.False;
    }

    public int Reset() { position = 0; return Com.Ok; }

    public int Clone(nint* enumerator)
    {
        if (enumerator == null) return Com.Pointer;
        *enumerator = 0;
        try { return Com.Interface(new CommandEnumerator(destinations, position), typeof(IEnumExplorerCommand).GUID, enumerator); }
        catch (Exception e) { return e.HResult; }
    }
}

internal static unsafe class Selection
{
    private const uint FileSystem = 0x40000000;

    public static bool IsFileSystemItems(nint items)
    {
        if (items == 0) return false;
        nint* table = *(nint**)items;
        uint count = 0, all = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)table[7])(items, &count));
        if (count == 0) return false;
        var attributes = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, int>)table[6];
        Com.Check(attributes(items, 1, FileSystem, &all)); // SIATTRIBFLAGS_AND
        return (all & FileSystem) != 0;
    }

    public static string[] FilePaths(nint items)
    {
        nint* table = *(nint**)items;
        uint count = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)table[7])(items, &count));
        var result = new string[checked((int)count)];
        for (uint i = 0; i < count; i++)
        {
            nint item = 0, name = 0;
            try
            {
                Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)table[8])(items, i, &item));
                if (item == 0) throw new COMException("Проводник не вернул выделенный объект.");
                nint* itemTable = *(nint**)item;
                Com.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)itemTable[5])(item, 0x80058000, &name)); // SIGDN_FILESYSPATH
                result[i] = Marshal.PtrToStringUni(name) ?? throw new COMException("Проводник не вернул путь файла или папки.");
            }
            finally
            {
                if (name != 0) Marshal.FreeCoTaskMem(name);
                if (item != 0) Marshal.Release(item);
            }
        }
        return result;
    }
}

internal static unsafe partial class NativeModule
{
    public static string DirectoryPath()
    {
        nint address = (nint)(delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)&NativeExports.DllGetClassObject;
        if (!GetModuleHandleExW(0x00000006, address, out nint module))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        char* buffer = stackalloc char[32768];
        uint length = GetModuleFileNameW(module, buffer, 32768);
        if (length == 0 || length == 32768)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return Path.GetDirectoryName(new string(buffer, 0, (int)length))!;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint size);
}
