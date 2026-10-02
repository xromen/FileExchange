using System.Runtime.InteropServices;
using FileExchange.Shared;
using FileExchange.ShellExtension;

// This calls the real native exports and COM vtables without registering the DLL.
// Do not NativeLibrary.Free: unloading NativeAOT libraries is unsupported.
internal static unsafe class Program
{
    private static readonly Guid ClassId = new("B687DD84-18AA-4A7A-B6D3-3B4A4FBA2F92");
    private static readonly Guid FactoryId = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid CommandId = new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Укажите путь к опубликованной FileExchange.ShellExtension.dll."); return 2; }
        nint factory = 0, command = 0, enumerator = 0, clone = 0;
        try
        {
            CheckSelectionAndSubCommands();
            nint library = NativeLibrary.Load(Path.GetFullPath(args[0]));
            var getClass = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(library, "DllGetClassObject");
            var canUnload = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(library, "DllCanUnloadNow");
            Assert(canUnload() == 1, "DLL должна оставаться загруженной до выхода процесса.");
            Guid clsid = ClassId, factoryId = FactoryId, commandId = CommandId, absent = Guid.NewGuid();
            nint pointer = 0;
            Assert(getClass(&absent, &factoryId, &pointer) == unchecked((int)0x80040111) && pointer == 0, "Неизвестный CLSID.");
            Assert(getClass(&clsid, &absent, &pointer) == unchecked((int)0x80004002) && pointer == 0, "Неизвестный IID.");
            Assert(getClass(&clsid, &factoryId, &factory) == 0 && factory != 0, "IClassFactory.");
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Table(factory)[3];
            Assert(create(factory, factory, &commandId, &pointer) == unchecked((int)0x80040110) && pointer == 0, "Агрегация COM запрещена.");
            Assert(create(factory, 0, &commandId, &command) == 0 && command != 0, "IExplorerCommand.");
            Assert(Title(command) == ExchangeStore.ReadParentName(), "Название родителя из настроек.");
            uint flags = 0, state = 99;
            Assert(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(command)[9])(command, &flags) == 0 && flags == 1, "ECF_HASSUBCOMMANDS.");
            Assert(((delegate* unmanaged[Stdcall]<nint, nint, int, uint*, int>)Table(command)[7])(command, 0, 1, &state) == 0 && state == 2, "Пустое выделение скрыто.");
            Assert(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Table(command)[8])(command, 0, 0) == unchecked((int)0x80004001), "Родитель сам не копирует.");
            Guid canonical = Guid.Empty;
            Assert(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Table(command)[6])(command, &canonical) == 0 && canonical == ClassId, "CanonicalName.");
            nint icon = 0;
            Assert(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Table(command)[4])(command, 0, &icon) == 0 && icon != 0, "Иконка.");
            Marshal.FreeCoTaskMem(icon);
            Assert(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(command)[10])(command, &enumerator) == 0 && enumerator != 0, "EnumSubCommands.");
            Assert(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(enumerator)[6])(enumerator, &clone) == 0 && clone != 0, "Clone.");
            List<string> titles = Titles(enumerator);
            Assert(titles.SequenceEqual(Titles(clone)), "Clone сохраняет позицию и команды.");
            Assert(((delegate* unmanaged[Stdcall]<nint, int>)Table(enumerator)[5])(enumerator) == 0, "Reset.");
            Assert(titles.SequenceEqual(Titles(enumerator)), "Reset возвращает те же команды.");
            Assert(((delegate* unmanaged[Stdcall]<nint, uint, int>)Table(enumerator)[4])(enumerator, uint.MaxValue) == 1, "Skip сообщает конец.");
            Console.WriteLine($"COM ABI проверен: экспорт, фабрика, родитель, {titles.Count} подпунктов, перечисление и HRESULT.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            if (clone != 0) Release(clone);
            if (enumerator != 0) Release(enumerator);
            if (command != 0) Release(command);
            if (factory != 0) Release(factory);
        }
    }

    private static void CheckSelectionAndSubCommands()
    {
        nint items = 0, enumerator = 0;
        try
        {
            Assert(Com.Interface(new FakeSelection(25), FakeSelection.InterfaceId, &items) == 0, "Создание тестового выделения.");
            Assert(Selection.IsFileSystemItems(items), "Выделение файлов.");
            string[] paths = Selection.FilePaths(items);
            Assert(paths.Length == 25 && paths.Select((path, i) => path == FakeSelection.PathAt(i)).All(value => value), "Все 25 путей сохранены.");
            Destination[] destinations = [new("На сервер", @"\\server\share"), new("В архив", @"D:\Архив")];
            Assert(Com.Interface(new CommandEnumerator(destinations), typeof(IEnumExplorerCommand).GUID, &enumerator) == 0, "Динамические подпункты.");
            Assert(Titles(enumerator).SequenceEqual(destinations.Select(value => value.Name)), "Имена и порядок динамических подпунктов.");
            var leaf = new ShellCommand(destinations[0]);
            uint state = 99;
            Assert(leaf.GetState(items, 1, &state) == 0 && state == 0, "25 выделенных файлов включают подпункт.");
            Assert(leaf.GetState(items, 0, &state) == unchecked((int)0x8000000A), "Медленная проверка отложена.");
            Release(items); items = 0;
            Assert(Com.Interface(new FakeSelection(2, folders: true), FakeSelection.InterfaceId, &items) == 0, "Выделение с папкой.");
            Assert(Selection.IsFileSystemItems(items) && leaf.GetState(items, 1, &state) == 0 && state == 0, "Папки включают подпункт.");
            Assert(Selection.FilePaths(items).Length == 2, "Пути папок переданы полностью.");
            Release(items); items = 0;
            Assert(Com.Interface(new FakeSelection(2, folders: true, mixed: true), FakeSelection.InterfaceId, &items) == 0 &&
                Selection.IsFileSystemItems(items) && leaf.GetState(items, 1, &state) == 0 && state == 0, "Смешанное выделение включает подпункт.");
            Release(items); items = 0;
            Assert(Com.Interface(new FakeSelection(2, filesystem: false), FakeSelection.InterfaceId, &items) == 0 && !Selection.IsFileSystemItems(items), "Виртуальные объекты отклонены.");
            Assert(leaf.Invoke(items, 0) == unchecked((int)0x80070057), "Копирование виртуальных объектов отклонено.");
            Console.WriteLine("Выделение проверено: 25 файлов, папки, виртуальные объекты, 2 динамических подпункта.");
        }
        finally
        {
            if (items != 0) Release(items);
            if (enumerator != 0) Release(enumerator);
        }
    }

    private static List<string> Titles(nint enumerator)
    {
        var titles = new List<string>();
        var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Table(enumerator)[3];
        while (true)
        {
            nint command = 0;
            uint fetched = 99;
            int hr = next(enumerator, 1, &command, &fetched);
            if (hr == 1) { Assert(fetched == 0, "S_FALSE без команды."); break; }
            Assert(hr == 0 && fetched == 1 && command != 0, "Next(1).");
            try
            {
                titles.Add(Title(command));
                uint flags = 99;
                Assert(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(command)[9])(command, &flags) == 0 && flags == 0, "Лист без подпунктов.");
                Assert(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Table(command)[8])(command, 0, 0) == unchecked((int)0x80070057), "Копирование без выделения отклонено.");
            }
            finally { Release(command); }
        }
        return titles;
    }

    private static string Title(nint command)
    {
        nint title = 0;
        try
        {
            Assert(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Table(command)[3])(command, 0, &title) == 0 && title != 0, "GetTitle.");
            return Marshal.PtrToStringUni(title)!;
        }
        finally { if (title != 0) Marshal.FreeCoTaskMem(title); }
    }

    private static nint* Table(nint value) => *(nint**)value;
    private static void Release(nint value) => ((delegate* unmanaged[Stdcall]<nint, uint>)Table(value)[2])(value);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
