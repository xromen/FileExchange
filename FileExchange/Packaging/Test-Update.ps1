#Requires -Version 5.1
# Проверяет реальные функции обновления на временных файлах; не меняет меню и не запускает приложения.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Update.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw $parseErrors[0].Message }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    $source = $function.Extent.Text
    if ($function.Name -eq 'Set-PayloadFile') { $source = $source.Replace('function Set-PayloadFile', 'function Set-RealPayloadFile') }
    . ([ScriptBlock]::Create($source))
}

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-MenuInstalled { return $script:menuInstalled }
function Get-InstalledDirectory { return $script:installed }
function Start-Manager([string]$Directory, [string]$Result) {
    if ($script:failStart) { $script:failStart = $false; throw 'Synthetic restart failure' }
    $script:restarted = $Result
}
function Set-PayloadFile([string]$Source, [string]$Target) {
    if ($script:failCopy -and $Target -eq (Join-Path $script:target 'FileExchange.ShellExtension.dll')) {
        $script:failCopy = $false
        throw 'Synthetic file replacement failure'
    }
    Set-RealPayloadFile $Source $Target
}
function Invoke-MenuInstall([string]$Directory) {
    $script:installCalls.Add($Directory)
    if ($Directory -eq (Join-Path $script:stage 'payload')) {
        if ($script:failInstall) {
            [IO.File]::WriteAllText((Join-Path $script:installed 'FileExchange.exe'), 'Partially updated')
            throw 'Synthetic extension installation failure'
        }
        foreach ($file in $script:files) {
            if ($file -notin @('FileExchange.Manager.exe', 'README.md')) {
                Set-RealPayloadFile (Join-Path $Directory $file) (Join-Path $script:installed $file)
            }
        }
    }
}

$root = Join-Path $PSScriptRoot ('.file-exchange-update-test-' + [Guid]::NewGuid().ToString('N'))
$script:files = @('FileExchange.exe', 'FileExchange.Manager.exe', 'FileExchange.ShellExtension.dll', 'FileExchange.msix', 'FileExchange.cer',
                  'Install.ps1', 'Uninstall.ps1', 'README.md', 'Assets/Logo44.png', 'Assets/Logo150.png')
try {
    foreach ($scenario in @('portable', 'installed', 'copy-failure', 'install-failure', 'restart-failure')) {
        $case = Join-Path $root $scenario
        $script:stage = Join-Path $case 'stage'
        $script:target = Join-Path $case 'target'
        $script:installed = Join-Path $case 'installed'
        $script:menuInstalled = $scenario -ne 'portable'
        $script:failCopy = $scenario -eq 'copy-failure'
        $script:failInstall = $scenario -eq 'install-failure'
        $script:failStart = $scenario -eq 'restart-failure'
        $script:restarted = $null
        $script:installCalls = [Collections.Generic.List[string]]::new()
        foreach ($file in $script:files) {
            foreach ($directory in @($script:target, $script:installed, (Join-Path $script:stage 'payload'))) {
                $path = Join-Path $directory $file
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                $prefix = if ($directory -eq (Join-Path $script:stage 'payload')) { 'new:' } else { 'old:' }
                [IO.File]::WriteAllText($path, $prefix + $file)
            }
        }
        $extra = Join-Path $script:target 'user-note.txt'
        [IO.File]::WriteAllText($extra, 'User data')
        $errorMessage = $null
        try { Invoke-ApplicationUpdate $script:stage $script:target 2147483647 }
        catch { $errorMessage = $_.Exception.Message }
        $failure = $scenario.EndsWith('failure')
        Assert ($failure -eq ($null -ne $errorMessage)) "Неверный итог сценария $scenario`: $errorMessage"
        foreach ($file in $script:files) {
            $expected = if ($failure) { 'old:' } else { 'new:' }
            Assert ([IO.File]::ReadAllText((Join-Path $script:target $file)) -eq ($expected + $file)) "Не обновлён или не восстановлен $file ($scenario)."
            if ($script:menuInstalled -and $file -notin @('FileExchange.Manager.exe', 'README.md')) {
                Assert ([IO.File]::ReadAllText((Join-Path $script:installed $file)) -eq ($expected + $file)) "Не восстановлены установленные файлы ($scenario)."
            }
        }
        Assert ([IO.File]::ReadAllText($extra) -eq 'User data') "Обновление затронуло пользовательский файл ($scenario)."
        Assert ($failure -or $script:restarted -eq 'success') "Приложение не было перезапущено ($scenario)."
        if ($script:menuInstalled) { Assert ($script:installCalls.Count -eq $(if ($failure) { 2 } else { 1 })) "Не вызвана установка/восстановление расширения ($scenario)." }
        else { Assert ($script:installCalls.Count -eq 0) 'Обновление установило удалённое меню.' }
    }
    Write-Host 'OK: обновление переносимых и установленных файлов, сохранение пользовательских данных, откат при ошибках копирования/установки/перезапуска; меню и сертификаты не изменялись.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $allowed = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Недопустимая папка проверки обновления.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
