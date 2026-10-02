#Requires -Version 5.1
<#
.SYNOPSIS
Добавляет один подпункт в меню «Файловый обмен» для текущего пользователя.
.EXAMPLE
.\Add-FileExchangeMenu.ps1 -ItemName 'На сервер' -DestinationPath '\\server\share\Обмен'
.EXAMPLE
.\Add-FileExchangeMenu.ps1 -ItemName 'В архив' -DestinationPath 'D:\Архив'
.NOTES
Windows 11: меню находится в «Показать дополнительные параметры».
Поддерживается выделение до 15 файлов. Совпадающие имена не перезаписываются.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Install')]
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$ItemName,

    [Parameter(Mandatory = $true, Position = 1, ParameterSetName = 'Install')]
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
    [string]$DestinationPath,

    # Внутренние параметры: их передаёт Проводник, а не пользователь.
    [Parameter(Mandatory = $true, ParameterSetName = 'Copy')]
    [string]$SourcePath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Copy')]
    [string]$DestinationBase64
)

$ErrorActionPreference = 'Stop'

function Copy-ExchangeFile {
    param([string]$SourcePath, [string]$DestinationPath)

    if (-not [System.IO.File]::Exists($SourcePath)) {
        throw "Исходный файл не найден: $SourcePath"
    }

    [void][System.IO.Directory]::CreateDirectory($DestinationPath)
    $target = [System.IO.Path]::Combine($DestinationPath, [System.IO.Path]::GetFileName($SourcePath))
    # false запрещает перезапись, в том числе при одновременных вызовах.
    [System.IO.File]::Copy($SourcePath, $target, $false)
}

if ($PSCmdlet.ParameterSetName -eq 'Copy') {
    try {
        $destination = [System.Text.Encoding]::UTF8.GetString(
            [System.Convert]::FromBase64String($DestinationBase64)
        )
        Copy-ExchangeFile -SourcePath $SourcePath -DestinationPath $destination
    }
    catch {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            "Не удалось скопировать файл:`r`n$SourcePath`r`n`r`n$($_.Exception.Message)",
            'Файловый обмен',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        )
        exit 1
    }
    return
}

$provider = $null
$drive = $null
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $DestinationPath, [ref]$provider, [ref]$drive
)
if ($provider.Name -ne 'FileSystem') {
    throw 'Путь назначения должен указывать на папку в файловой системе.'
}
if ([System.IO.File]::Exists($destination)) {
    throw "Путь назначения указывает на файл: $destination"
}

# Рабочая копия не зависит от дальнейшего перемещения скачанного скрипта.
$installDirectory = Join-Path ([System.Environment]::GetFolderPath('LocalApplicationData')) 'FileExchangeMenu'
[void][System.IO.Directory]::CreateDirectory($installDirectory)
$installedScript = Join-Path $installDirectory 'Add-FileExchangeMenu.ps1'
if (-not [string]::Equals($PSCommandPath, $installedScript, [System.StringComparison]::OrdinalIgnoreCase)) {
    [System.IO.File]::Copy($PSCommandPath, $installedScript, $true)
}

# Хеш позволяет использовать в названии любые символы, включая обратную косую черту.
# Повторный запуск с тем же именем обновляет подпункт, а не создаёт дубликат.
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $hash = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($ItemName.ToUpperInvariant()))
    $itemKeyName = [System.BitConverter]::ToString($hash).Replace('-', '')
}
finally {
    $sha256.Dispose()
}

# Папка передаётся как данные: кавычки и специальные символы не становятся кодом.
$destinationEncoded = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($destination))
$powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$command = '"{0}" -NoLogo -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}" -DestinationBase64 "{2}" -SourcePath "%1"' -f $powershellExe, $installedScript, $destinationEncoded

# .NET Registry работает с ключом * буквально, без подстановки шаблонов.
$menuPath = 'Software\Classes\*\shell\Файловый обмен'
$menuKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($menuPath, $true)
if ($null -eq $menuKey) {
    $menuKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($menuPath)
}
try {
    $menuKey.SetValue('MUIVerb', 'Файловый обмен')
    $menuKey.SetValue('SubCommands', '')
    # ponytail: стандартное меню ограничено 15 файлами; для больших выборок нужен COM-обработчик.
    $menuKey.SetValue('MultiSelectModel', 'Document')

    $itemKey = $menuKey.CreateSubKey("shell\$itemKeyName")
    try {
        $itemKey.SetValue('MUIVerb', $ItemName.Replace('&', '&&'))
        $itemKey.SetValue('MultiSelectModel', 'Document')
        $commandKey = $itemKey.CreateSubKey('command')
        try {
            $commandKey.SetValue('', $command)
        }
        finally {
            $commandKey.Dispose()
        }
    }
    finally {
        $itemKey.Dispose()
    }
}
finally {
    $menuKey.Dispose()
}

Write-Host "Добавлен или обновлён пункт: Файловый обмен > $ItemName"
Write-Host "Папка назначения: $destination"
