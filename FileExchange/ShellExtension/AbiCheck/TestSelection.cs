using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using FileExchange.ShellExtension;

// Native vtables with the exact IShellItemArray/IShellItem layout.
[GeneratedComInterface, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
internal unsafe partial interface ITestShellItemArray
{
    [PreserveSig] int BindToHandler(nint context, Guid* handler, Guid* iid, nint* result);
    [PreserveSig] int GetPropertyStore(uint flags, Guid* iid, nint* result);
    [PreserveSig] int GetPropertyDescriptionList(nint key, Guid* iid, nint* result);
    [PreserveSig] int GetAttributes(uint flags, uint mask, uint* result);
    [PreserveSig] int GetCount(uint* count);
    [PreserveSig] int GetItemAt(uint index, nint* result);
    [PreserveSig] int EnumItems(nint* result);
}

[GeneratedComInterface, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal unsafe partial interface ITestShellItem
{
    [PreserveSig] int BindToHandler(nint context, Guid* handler, Guid* iid, nint* result);
    [PreserveSig] int GetParent(nint* result);
    [PreserveSig] int GetDisplayName(uint kind, nint* result);
    [PreserveSig] int GetAttributes(uint mask, uint* result);
    [PreserveSig] int Compare(nint other, uint hint, int* order);
}

[GeneratedComClass]
internal unsafe partial class FakeSelection(uint count, bool folders = false, bool filesystem = true, bool mixed = false) : ITestShellItemArray
{
    internal static readonly Guid InterfaceId = typeof(ITestShellItemArray).GUID;
    internal static string PathAt(int index) => @"C:\выделение\файл " + index + ".txt";
    public int BindToHandler(nint context, Guid* handler, Guid* iid, nint* result) => Com.NotImplemented;
    public int GetPropertyStore(uint flags, Guid* iid, nint* result) => Com.NotImplemented;
    public int GetPropertyDescriptionList(nint key, Guid* iid, nint* result) => Com.NotImplemented;
    public int EnumItems(nint* result) => Com.NotImplemented;
    public int GetCount(uint* result) { *result = count; return Com.Ok; }
    public int GetAttributes(uint flags, uint mask, uint* result)
    {
        *result = mask & ((filesystem ? 0x40000000u : 0) | (folders && (!mixed || flags != 1) ? 0x20000000u : 0));
        return *result == mask ? Com.Ok : Com.False;
    }
    public int GetItemAt(uint index, nint* result) => index >= count ? Com.InvalidArgument :
        Com.Interface(new FakeItem(PathAt((int)index)), typeof(ITestShellItem).GUID, result);
}

[GeneratedComClass]
internal unsafe partial class FakeItem(string path) : ITestShellItem
{
    public int BindToHandler(nint context, Guid* handler, Guid* iid, nint* result) => Com.NotImplemented;
    public int GetParent(nint* result) => Com.NotImplemented;
    public int GetDisplayName(uint kind, nint* result) => kind == 0x80058000 ? Com.String(path, result) : Com.InvalidArgument;
    public int GetAttributes(uint mask, uint* result) { *result = mask & 0x40000000; return Com.Ok; }
    public int Compare(nint other, uint hint, int* order) => Com.NotImplemented;
}
