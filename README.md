# DiskCleaner — безопасная очистка дисков Windows

Полноценное desktop-приложение на C# / .NET 8 / WPF для анализа и безопасной
очистки кэшей и временных файлов на **всех** локальных дисках Windows (не
только C:), с сильной защитой от удаления важных данных.

## Структура решения

```
DiskCleaner.sln
├── src/
│   ├── DiskCleaner.Core/        — вся бизнес-логика, БЕЗ зависимости от WPF/Windows Desktop.
│   │                              Собирается и тестируется на любой платформе (net8.0).
│   │   ├── Models/              — CleanupRule, CleanupTarget, CleanupCategory, CleanupRiskLevel,
│   │   │                          CleanupResult, CleanupLogEntry, ScanResult, SkippedItem,
│   │   │                          DriveInfoModel, AppSettings.
│   │   └── Services/
│   │       ├── PathSafetyService.cs         — ЕДИНСТВЕННАЯ точка "можно ли трогать путь".
│   │       ├── ReparsePointGuard.cs         — защита от symlink/junction.
│   │       ├── DriveScanner.cs              — список дисков для первого экрана.
│   │       ├── DiskUsageScanner.cs          — размеры/ФС/тип конкретного диска.
│   │       ├── CleanupRuleProvider.cs       — каталог whitelist-правил + разворачивание в реальные пути.
│   │       ├── SizeCalculator.cs            — подсчёт размера без захода в reparse points.
│   │       ├── CleanupService.cs            — собственно удаление содержимого.
│   │       ├── WindowsSystemCleanupService.cs — правила системных категорий (TEMP, thumbnails, Update cache...).
│   │       ├── AppCacheDetector.cs          — правила кэшей инструментов разработки/приложений.
│   │       ├── BrowserCacheDetector.cs      — правила кэшей известных браузеров.
│   │       ├── AdminRightsChecker.cs        — определение прав администратора.
│   │       ├── Logger.cs                    — журнал в %LOCALAPPDATA%\DiskCleaner\logs.
│   │       └── SettingsService.cs           — настройки в %LOCALAPPDATA%\DiskCleaner\settings.json.
│   └── DiskCleaner.App/         — WPF-приложение (net8.0-windows), тонкий UI-слой поверх Core.
│       ├── Views/                MainWindow, ConfirmationWindow, ResultsWindow, SettingsWindow, LogsWindow.
│       ├── ViewModels/            MainViewModel, DriveViewModel, CleanupTargetViewModel,
│       │                          CleanupResultViewModel, RelayCommand, AsyncRelayCommand.
│       └── app.manifest           requestedExecutionLevel="asInvoker" — админка НЕ требуется.
└── tests/
    └── DiskCleaner.Tests/       — xUnit-тесты для PathSafetyService, ReparsePointGuard,
                                    CleanupService, SizeCalculator, dry-run.
```

## Сборка и запуск

Нужен .NET 8 SDK (с поддержкой Windows Desktop для WPF).

```powershell
dotnet restore
dotnet build
dotnet run --project src\DiskCleaner.App
```

Юнит-тесты (не требуют WPF, можно гонять и в CI):

```powershell
dotnet test tests\DiskCleaner.Tests
```

Приложение **не требует** прав администратора (`asInvoker`). Категории,
которым для доступа реально нужны повышенные права (Windows Update cache,
Delivery Optimization, иногда Windows\Temp), помечены в интерфейсе статусом
"Требуются права администратора" и недоступны для выбора, если процесс
запущен без них — без падения программы.

## Какие категории чистятся

| Категория | Где искать | Риск |
|---|---|---|
| Пользовательский TEMP | `%TEMP%` | Safe |
| Windows Temp | `{диск}\Windows\Temp` (только системный диск) | Safe (может требовать админку) |
| NVIDIA DXCache / DirectX Shader Cache | `%LOCALAPPDATA%\NVIDIA\DXCache`, `%LOCALAPPDATA%\D3DSCache` | Safe |
| Crash Dumps | `%LOCALAPPDATA%\CrashDumps` | Safe |
| Кэш миниатюр Windows | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` (только файлы `thumbcache_*.db`/`iconcache_*.db`) | Safe |
| Браузерные кэши | Chrome/Edge/Brave/Opera/Vivaldi/Firefox — папки Cache/Code Cache/GPUCache/cache2 во всех профилях | Safe |
| Electron app cache | Общий поиск Cache/Code Cache/GPUCache под `%LOCALAPPDATA%` (Discord, VS Code, CapCut и т.п.) | Safe |
| JetBrains caches/index/log/jcef_cache/full-line | `%LOCALAPPDATA%\JetBrains\*` | Safe |
| Gradle caches | `%USERPROFILE%\.gradle\caches` | Safe |
| NuGet cache | `%USERPROFILE%\.nuget\packages`, `%LOCALAPPDATA%\NuGet\v3-cache` | Safe |
| pip cache | `%LOCALAPPDATA%\pip\cache` | Safe |
| npm/yarn/pnpm cache | `%LOCALAPPDATA%\npm-cache`, `%APPDATA%\npm-cache`, `%LOCALAPPDATA%\Yarn\Cache`, `%LOCALAPPDATA%\pnpm-store` | Safe |
| Playwright browser cache | `%LOCALAPPDATA%\ms-playwright` | Safe |
| Android build/cache | `%LOCALAPPDATA%\Android\Sdk\.temp`, `%USERPROFILE%\.android\{cache,build-cache}` | Safe |
| Unity/Unreal (глобальный кэш) | `%LOCALAPPDATA%\Unity\cache`, `%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache` | Safe |
| Roblox downloads/cache | `%LOCALAPPDATA%\Roblox\downloads` | Safe |
| CapCut cache | `%LOCALAPPDATA%\CapCut\...\{Cache,Code Cache,GPUCache}` | Safe |
| Delivery Optimization cache | `{системный диск}\Windows\...\DeliveryOptimization\Cache` | Manual, требует админку |
| Windows Update cache | `{системный диск}\Windows\SoftwareDistribution\Download` | Manual, требует админку |
| Temp/tmp в корне диска | `{диск}\Temp`, `{диск}\tmp` — для ЛЮБОГО выбранного диска | Review |
| ShaderCache (эвристика по диску) | Поиск папок `ShaderCache` по всему выбранному диску | Review |

Safe-категории отмечаются галочкой по умолчанию (если доступны без админки).
Review и Manual — никогда, пользователь должен сам их просмотреть и выбрать.

## Что никогда не удаляется

- Корень любого диска (`C:\`, `D:\` и т.д.) целиком.
- `Windows`, `Windows\System32`, `Program Files`, `Program Files (x86)`, `ProgramData`, `Users` —
  для ЛЮБОГО диска, с единственными явными исключениями: `Windows\Temp`,
  `Windows\SoftwareDistribution\Download`, `Windows\...\DeliveryOptimization\Cache`.
- `$Recycle.Bin`, `System Volume Information`.
- `Documents`, `Desktop`, `Downloads`, `Pictures`, `Videos`, `Music` текущего пользователя.
- Исходники проектов, базы данных, настройки программ, `AppData` целиком, папки программ целиком —
  ничего из этого не попадает под whitelist ни одного правила.
- Содержимое любого symlink/junction/reparse point — такие элементы всегда
  пропускаются (и при подсчёте размера, и при удалении), никогда не удаляются
  и не разворачиваются, только логируются как skipped.
- Сама папка-категория (например сам `%TEMP%`) — удаляется только СОДЕРЖИМОЕ,
  никогда сама папка.
- Любой путь, добавленный пользователем в исключения (окно "Настройки").

## Как работает безопасность

1. **Whitelist, не blacklist.** `CleanupRuleProvider.GetBuiltInRules()` (агрегирует
   `WindowsSystemCleanupService` + `AppCacheDetector` + `BrowserCacheDetector`) —
   единственное место, где перечислены категории, которые в принципе можно
   предложить к удалению.
2. **PathSafetyService** — единственная точка решения "безопасно ли". Путь
   нормализуется в абсолютный (`Path.GetFullPath`, защита от `..\`) и проходит:
   - блок-лист пользовательских исключений;
   - проверку существования;
   - запрет на удаление корня диска;
   - запрет, если сам путь — symlink/junction;
   - запрет пользовательских папок (Documents/Desktop/...);
   - запрет системных директорий (с точечными исключениями для одобренных
     подпапок Windows);
   - **Whitelist A** — путь внутри профильных корней пользователя;
   - **Whitelist B** — путь с известным "кэшевым" именем (Temp/tmp/ShaderCache) на любом диске.

   Проверка выполняется **дважды**: при сканировании и ПОВТОРНО прямо перед
   удалением (`CleanupService.CleanOne`) — на случай, если что-то изменилось
   между сканированием и подтверждением.
3. **ReparsePointGuard** — используется `SizeCalculator` (не заходит внутрь
   symlink/junction при подсчёте размера) и `CleanupService` (не удаляет
   содержимое через ссылку и не трогает саму ссылку) на каждом шаге обхода.
4. **Удаляется только содержимое** — `CleanupService.DeleteDirectoryContentsOnly`
   никогда не вызывает `Directory.Delete` на саму папку-цель.
5. **Обязательное подтверждение** перед реальным (не dry-run) удалением —
   `ConfirmationWindow` показывает точный список путей, размеры и
   предупреждение, если среди выбранного есть категории Review/Manual.
6. **Dry-run включён по умолчанию.**
7. **Ни одна ошибка доступа не роняет приложение** — весь файловый ввод-вывод
   обёрнут в try/catch с логированием в `SkippedItem`/`errors`, обработка
   продолжается.
8. **Отмена.** Сканирование и очистка выполняются в фоне (`Task.Run` +
   `CancellationToken`), кнопка "Cancel" вызывает `CancellationTokenSource.Cancel()`;
   уже найденное/удалённое до отмены сохраняется.

## Известные ограничения

- Правила Unity/Unreal покрывают только ГЛОБАЛЬНЫЙ кэш движка
  (`%LOCALAPPDATA%\Unity\cache`, `...\UnrealEngine\Common\DerivedDataCache`),
  а не кэш конкретных проектов (`ProjectFolder\Library\ShaderCache` и т.п.) —
  безопасно найти такие папки на произвольном диске без риска задеть чужой
  "Library"/"Saved" не представляется возможным без явного указания корня
  проекта, поэтому это осознанно не реализовано.
- Кэш Windows Update реально освобождается полностью только если служба
  `wuauserv` временно остановлена — программа не останавливает системные
  службы автоматически (это отдельное, более рискованное действие), файлы,
  занятые службой, будут аккуратно пропущены как skipped.
- Изменения в списке исключений (окно "Настройки") применяются к активному
  `PathSafetyService` только после перезапуска сканирования в новой сессии
  (настройки сохраняются в JSON немедленно, но сам сервис безопасности
  строится один раз при старте `MainViewModel`).
- Поиск `ShaderCache` по всему диску (категория Review) может быть небыстрым
  на больших/сильно захламлённых дисках — используйте Cancel при необходимости.

## Как добавить новую CleanupRule

1. Откройте один из трёх файлов-каталогов в `DiskCleaner.Core/Services/`:
   `WindowsSystemCleanupService.cs` (системные категории), `AppCacheDetector.cs`
   (инструменты/приложения) или `BrowserCacheDetector.cs` (браузеры) — либо
   создайте новый детектор с тем же интерфейсом (`List<CleanupRule> GetRules()`)
   и зарегистрируйте его в `CleanupRuleProvider.GetBuiltInRules()`.
2. Добавьте новый `CleanupRule`, заполнив:
   - `Category` — при необходимости добавьте новое значение в `CleanupCategory`;
   - `RiskLevel` — `Safe` только для точно известного официального пути;
     `Review` для эвристических/найденных поиском; `Manual` для системных
     категорий, требующих осторожности;
   - `DriveScope` — `UserProfile` (не зависит от выбора дисков), `SystemDriveOnly`
     (существует только на системном диске) или `PerSelectedDrive` (нужно
     разворачивать на каждом выбранном диске — используйте токен `{drive}`
     в `BasePathTemplate`);
   - `Kind` — `FixedPath` (путь известен точно) или `DynamicSearch` (нужно
     искать подпапки по `SubPathPatterns`, с `MaxSearchDepth`);
   - `RequiresAdmin` — если путь обычно недоступен без повышенных прав;
   - `FileNamePatterns` — если удалять нужно не всё содержимое папки, а
     только файлы по маске (как для кэша миниатюр Windows).
3. Больше ничего менять не нужно — `PathSafetyService` проверит путь по
   тем же правилам (профильные корни ИЛИ известное кэш-имя на любом диске),
   `SizeCalculator`/`CleanupService` подхватят новую категорию автоматически.
   Если новый путь не попадает под существующий whitelist (например, лежит
   в совершенно новом месте), нужно добавить соответствующий корень в
   `PathSafetyService._profileAllowedRoots` (для профильных путей) — это
   единственное место, которое иначе заблокирует новое правило.
4. Юнит-тесты в `tests/DiskCleaner.Tests` не нужно менять для новой
   категории — они проверяют общую логику сервисов, а не конкретный список
   правил.
