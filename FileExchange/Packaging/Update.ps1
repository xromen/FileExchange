#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$StageDirectory,
      [Parameter(Mandatory = $true)][string]$TargetDirectory,
      [Parameter(Mandatory = $true)][int]$ParentProcessId)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Set-PayloadFile([string]$Source, [string]$Target) {
    $directory = [IO.Path]::GetDirectoryName($Target)
    [void][IO.Directory]::CreateDirectory($directory)
    foreach ($old in [IO.Directory]::EnumerateFiles($directory, [IO.Path]::GetFileName($Target) + '.*.old')) {
        if ([IO.Path]::GetFileName($old) -notmatch ('^' + [Regex]::Escape([IO.Path]::GetFileName($Target)) + '\.[a-f0-9]{32}\.old$')) { continue }
        try { [IO.File]::Delete($old) }
        catch [IO.IOException] { }
        catch [UnauthorizedAccessException] { }
    }
    if ([IO.File]::Exists($Target) -and (Get-FileHash -LiteralPath $Source).Hash -eq (Get-FileHash -LiteralPath $Target).Hash) { return }
    $suffix = '.' + [Guid]::NewGuid().ToString('N')
    $temporary = $Target + $suffix + '.tmp'
    $backup = $Target + $suffix + '.old'
    try {
        [IO.File]::Copy($Source, $temporary)
        # Сохраняем загруженную DLL: удаление файла без резервного имени блокируется Проводником.
        if ([IO.File]::Exists($Target)) { [IO.File]::Replace($temporary, $Target, $backup) }
        else { [IO.File]::Move($temporary, $Target) }
    }
    catch {
        if (-not [IO.File]::Exists($Target) -and [IO.File]::Exists($backup)) { [IO.File]::Move($backup, $Target) }
        throw
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    try { [IO.File]::Delete($backup) }
    catch [IO.IOException] { }
    catch [UnauthorizedAccessException] { }
}

function Get-MenuInstalled {
    if ([Environment]::OSVersion.Version.Build -ge 22000) {
        return @(Get-AppxPackage -Name FileExchange | Where-Object { $_.Publisher -eq 'CN=FileExchange' }).Count -gt 0
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\CLSID\{B687DD84-18AA-4A7A-B6D3-3B4A4FBA2F92}\InprocServer32')
    try { return $null -ne $key } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Invoke-MenuInstall([string]$Directory) {
    $output = & (Join-Path $Directory 'FileExchange.exe') --install 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Не удалось обновить расширение. Закройте окна Проводника и повторите обновление.`r`n$output" }
}

function Start-Manager([string]$Directory, [string]$Result) {
    Start-Process -FilePath (Join-Path $Directory 'FileExchange.Manager.exe') -ArgumentList @('--update-result', $Result) -WorkingDirectory $Directory -WindowStyle Normal | Out-Null
}

function Get-InstalledDirectory {
    return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileExchangeMenu\bin'
}

function Invoke-ApplicationUpdate([string]$Stage, [string]$Target, [int]$ProcessId) {
    $files = @('FileExchange.exe', 'FileExchange.Manager.exe', 'FileExchange.ShellExtension.dll', 'FileExchange.msix', 'FileExchange.cer',
               'Install.ps1', 'Uninstall.ps1', 'README.md', 'Assets/Logo44.png', 'Assets/Logo150.png')
    $payload = Join-Path $Stage 'payload'
    $backup = Join-Path $Stage 'backup'
    $installed = Get-InstalledDirectory
    $installedBackup = Join-Path $Stage 'installed-backup'
    if (-not [IO.File]::Exists((Join-Path $Target 'FileExchange.Manager.exe'))) { throw 'Не найдена текущая программа управления.' }
    foreach ($file in $files) { if (-not [IO.File]::Exists((Join-Path $payload $file))) { throw "Не найден файл обновления: $file" } }
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process -and -not $process.WaitForExit(60000)) { throw 'Приложение не закрылось за 60 секунд.' }
    $menuInstalled = Get-MenuInstalled
    $changed = [Collections.Generic.List[string]]::new()
    $installedChanged = $false
    try {
        foreach ($file in $files) {
            $old = Join-Path $Target $file
            if ([IO.File]::Exists($old)) {
                $saved = Join-Path $backup $file
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
                [IO.File]::Copy($old, $saved)
            }
            if ($menuInstalled -and [IO.File]::Exists((Join-Path $installed $file))) {
                $saved = Join-Path $installedBackup $file
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
                [IO.File]::Copy((Join-Path $installed $file), $saved)
            }
        }
        if ($menuInstalled) { $installedChanged = $true; Invoke-MenuInstall $payload }
        foreach ($file in $files) {
            $changed.Add($file)
            Set-PayloadFile (Join-Path $payload $file) (Join-Path $Target $file)
        }
        Start-Manager $Target 'success'
    }
    catch {
        $originalError = $_.Exception.Message
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        foreach ($file in $changed) {
            try {
                $saved = Join-Path $backup $file
                $old = Join-Path $Target $file
                if ([IO.File]::Exists($saved)) { Set-PayloadFile $saved $old }
                elseif ([IO.File]::Exists($old)) { [IO.File]::Delete($old) }
            }
            catch { $rollbackErrors.Add($_.Exception.Message) }
        }
        if ($installedChanged) {
            foreach ($file in $files) {
                $saved = Join-Path $installedBackup $file
                if ([IO.File]::Exists($saved)) {
                    try { Set-PayloadFile $saved (Join-Path $installed $file) }
                    catch { $rollbackErrors.Add($_.Exception.Message) }
                }
            }
            try { Invoke-MenuInstall $installed } catch { $rollbackErrors.Add($_.Exception.Message) }
        }
        if ($rollbackErrors.Count -gt 0) { $originalError += "`r`nНе удалось полностью восстановить предыдущую версию:`r`n" + ($rollbackErrors -join "`r`n") }
        throw $originalError
    }
}

$StageDirectory = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\')
$TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory)
$updatesRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FileExchangeMenu\updates')).TrimEnd('\')
if ([IO.Path]::GetDirectoryName($StageDirectory) -ne $updatesRoot -or [IO.Path]::GetFileName($StageDirectory) -notmatch '^[a-f0-9]{32}$' -or
    $StageDirectory -ne [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -or $TargetDirectory.StartsWith($StageDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $TargetDirectory -eq $StageDirectory -or $ParentProcessId -le 0) { throw 'Недопустимые параметры обновления.' }

$mutex = [Threading.Mutex]::new($false, 'Local\FileExchangeMenu.Update')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Другая копия приложения уже выполняет обновление.' }
    Invoke-ApplicationUpdate $StageDirectory $TargetDirectory $ParentProcessId
    # Удаляется только проверенный выше уникальный каталог обновления, после успешного перезапуска.
    Remove-Item -LiteralPath $StageDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    $log = Join-Path $StageDirectory 'update.log'
    [IO.File]::WriteAllText($log, $_.Exception.ToString(), [Text.UTF8Encoding]::new($true))
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show("Обновление не завершено.`r`n$($_.Exception.Message)`r`n`r`nЖурнал и резервные копии: $StageDirectory", 'Файловый обмен — обновление', 'OK', 'Error')
    Start-Manager $TargetDirectory 'failure'
    exit 1
}
finally { if ($acquired) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
