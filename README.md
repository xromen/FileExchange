# Файловый обмен

Программа на C# для Windows 10 и Windows 11 x64. Добавляет в контекстное меню файлов группу **«Файловый обмен»** с настраиваемыми папками назначения, включая новое меню Windows 11. Копирует все выделенные файлы, сохраняя исходники и существующие файлы назначения.

Скачайте `FileExchange-win-x64.zip` в разделе **Releases** своего GitHub-репозитория и распакуйте целиком. Установленная .NET для запуска не нужна.

```powershell
.\FileExchange.exe "На сервер" "\\server\share\Обмен"
.\FileExchange.exe "В архив" "D:\Архив"
```

Каждый запуск добавляет один подпункт или обновляет папку существующего пункта с тем же названием. При ошибке доверия сертификату `0x800B0109` выполните `FileExchange.exe --install` от имени администратора. Каждая сборка подписывается новым локальным сертификатом; при обновлении может потребоваться повторная установка сертификата.

[Подробные инструкции по установке, копированию и удалению](FileExchange/README.md).

## Автоматические релизы

После каждого push в `master` [GitHub Actions](.github/workflows/release.yml) собирает программу и нативное расширение, проверяет копирование, COM и диагностику установки, затем публикует ZIP в **GitHub Releases**. При ошибке сборки или проверки релиз не публикуется. Исходники GitHub также предоставляет в релизе автоматически.

Тег имеет вид `build-<номер запуска>-<номер попытки>`, например `build-1-1`. Повторный запуск workflow создаёт отдельный релиз. Тег указывает на коммит, из которого выполнена сборка. Сборки стоят в очереди; релиз получает отметку Latest, только если его коммит всё ещё находится в вершине `master` на момент публикации. Запуск вручную доступен через **Actions → Сборка и публикация → Run workflow**, для ветки `master`.

Workflow использует встроенный `GITHUB_TOKEN` с разрешением `contents: write`. Добавлять отдельный токен или секреты не требуется. Приватный ключ подписи удаляется после сборки и не попадает в ZIP или Git.

Origin пока не настроен. Создайте пустой репозиторий на GitHub и выполните из этой папки, заменив URL своим:

```powershell
git remote add origin https://github.com/USER/REPOSITORY.git
git push -u origin master
```

Первый push сразу запустит публикацию. GitHub Actions должен быть разрешён в настройках репозитория и организации.

## Локальная сборка

Нужны Windows x64, .NET SDK 10, Windows SDK и C++ Build Tools из Visual Studio для NativeAOT.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FileExchange\Build.ps1
.\FileExchange\artifacts\app\FileExchange.exe --self-test
dotnet run --project .\FileExchange\ShellExtension\AbiCheck\AbiCheck.csproj -c Release -- .\FileExchange\artifacts\app\FileExchange.ShellExtension.dll
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\FileExchange\Packaging\Test-InstallErrors.ps1
```

Результат: `FileExchange/artifacts/FileExchange-win-x64.zip` и распакованная программа в `FileExchange/artifacts/app`. Сборка и проверки не регистрируют меню и не добавляют сертификаты в доверенные.

`Add-FileExchangeMenu.ps1` и `Test-FileExchangeMenu.ps1` — предыдущий вариант для классического контекстного меню; в ZIP программы на C# они не включаются.
