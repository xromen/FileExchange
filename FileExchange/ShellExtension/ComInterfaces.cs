using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FileExchange.ShellExtension;

// Keep the native HRESULTs, pointer ownership and vtable order explicit.
[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal unsafe partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, Guid* iid, nint* result);
    [PreserveSig] int LockServer(int @lock);
}

[GeneratedComInterface, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
internal unsafe partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(nint items, nint* title);
    [PreserveSig] int GetIcon(nint items, nint* icon);
    [PreserveSig] int GetToolTip(nint items, nint* tooltip);
    [PreserveSig] int GetCanonicalName(Guid* name);
    [PreserveSig] int GetState(nint items, int okToBeSlow, uint* state);
    [PreserveSig] int Invoke(nint items, nint bindContext);
    [PreserveSig] int GetFlags(uint* flags);
    [PreserveSig] int EnumSubCommands(nint* enumerator);
}

[GeneratedComInterface, Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8")]
internal unsafe partial interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint count, nint* commands, uint* fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(nint* enumerator);
}
