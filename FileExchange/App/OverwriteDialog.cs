using System.Globalization;
using System.Runtime.InteropServices;

namespace FileExchange;

internal enum OverwriteChoice { Yes = 100, YesToAll, No, NoToAll }

internal static partial class OverwriteDialog
{
    internal static unsafe OverwriteChoice Show(string source, string target)
    {
        string content = $"В папке назначения уже есть файл:\r\n{target}\r\n\r\nЗаменить его копируемым файлом?";
        string details = FormatDetails(source, target);
        fixed (char* title = "Файловый обмен — перезапись", instruction = "Файл с таким именем уже существует",
            text = content, expanded = details, more = "Подробнее", less = "Скрыть подробности",
            yes = "Да", yesAll = "Да для всех", no = "Нет", noAll = "Нет для всех")
        {
            TaskDialogButton* buttons = stackalloc TaskDialogButton[4];
            buttons[0] = new() { Id = (int)OverwriteChoice.Yes, Text = yes };
            buttons[1] = new() { Id = (int)OverwriteChoice.YesToAll, Text = yesAll };
            buttons[2] = new() { Id = (int)OverwriteChoice.No, Text = no };
            buttons[3] = new() { Id = (int)OverwriteChoice.NoToAll, Text = noAll };
            var config = new TaskDialogConfig
            {
                Size = (uint)sizeof(TaskDialogConfig),
                Flags = 0x0008, // TDF_ALLOW_DIALOG_CANCELLATION: Escape/закрытие пропускают текущий файл.
                WindowTitle = title, MainIcon = (nint)0xffff, // TD_WARNING_ICON.
                MainInstruction = instruction, Content = text,
                ButtonCount = 4, Buttons = buttons, DefaultButton = (int)OverwriteChoice.No,
                ExpandedInformation = expanded, CollapsedControlText = more, ExpandedControlText = less,
                Width = 360
            };
            Marshal.ThrowExceptionForHR(TaskDialogIndirect(&config, out int selected, 0, 0));
            return Enum.IsDefined((OverwriteChoice)selected) ? (OverwriteChoice)selected : OverwriteChoice.No;
        }
    }

    internal static string FormatDetails(string source, string target) =>
        FormatFileDetails("Существующий файл (будет заменён)", target) + "\r\n\r\n" +
        FormatFileDetails("Новый файл (будет скопирован)", source);

    private static string FormatFileDetails(string title, string path)
    {
        string heading = $"{title}\r\nИмя: {Path.GetFileName(path)}\r\nПуть: {path}";
        try
        {
            var info = new FileInfo(path);
            string size = info.Length.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
            return $"{heading}\r\nРазмер: {size} байт\r\nИзменён: {info.LastWriteTime:dd.MM.yyyy HH:mm:ss}\r\nСоздан: {info.CreationTime:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{heading}\r\nИнформация недоступна: {error.Message}";
        }
    }

    // CommCtrl.h объявляет обе структуры под #pragma pack(1), в том числе на x64.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct TaskDialogButton
    {
        public int Id;
        public char* Text;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct TaskDialogConfig
    {
        public uint Size;
        public nint Parent, Instance;
        public uint Flags, CommonButtons;
        public char* WindowTitle;
        public nint MainIcon;
        public char* MainInstruction;
        public char* Content;
        public uint ButtonCount;
        public TaskDialogButton* Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public char* VerificationText;
        public char* ExpandedInformation;
        public char* ExpandedControlText;
        public char* CollapsedControlText;
        public nint FooterIcon;
        public char* Footer;
        public nint Callback, CallbackData;
        public uint Width;
    }

    [LibraryImport("comctl32.dll")]
    private static unsafe partial int TaskDialogIndirect(TaskDialogConfig* config, out int button, nint radioButton, nint verificationFlag);
}
