namespace DiskCleaner.Core.Models;

/// <summary>Почему удаление разрешено, но требует отдельного подтверждения пользователя.</summary>
public enum DeletionSensitivity
{
    /// <summary>Обычный путь — отдельное подтверждение не нужно.</summary>
    None,

    /// <summary>Внутри Documents / Desktop / Downloads / Pictures / Videos / Music / OneDrive — личные файлы.</summary>
    PersonalFiles,

    /// <summary>Данные внутри профиля браузера (не кэш): можно потерять сохранённые данные сайтов.</summary>
    BrowserProfile,
}

/// <summary>Вердикт «можно ли удалить этот путь вручную».</summary>
public sealed record DeletionVerdict(
    bool IsAllowed,
    string? BlockReason,
    DeletionSensitivity Sensitivity = DeletionSensitivity.None,
    string? SensitivityNote = null)
{
    /// <summary>Удаление разрешено, но только после явного подтверждения пользователя.</summary>
    public bool RequiresConfirmation => IsAllowed && Sensitivity != DeletionSensitivity.None;

    public static DeletionVerdict Allowed() => new(true, null);

    public static DeletionVerdict Blocked(string reason) => new(false, reason);

    public static DeletionVerdict AllowedWithConfirmation(DeletionSensitivity sensitivity, string note)
        => new(true, null, sensitivity, note);
}

/// <summary>Параметры ручного удаления.</summary>
public sealed class ManualDeletionOptions
{
    /// <summary>Как и в основной очистке: по умолчанию ничего не удаляется, только проверка и подсчёт.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// true — отправлять в Корзину. Важно: из Корзины место на диске НЕ освобождается,
    /// пока она не очищена. false (по умолчанию) — удаление безвозвратное.
    /// </summary>
    public bool UseRecycleBin { get; init; }

    /// <summary>
    /// Пользователь явно подтвердил удаление чувствительных путей (см. <see cref="DeletionSensitivity"/>).
    /// Без этого такие пути пропускаются.
    /// </summary>
    public bool ConfirmedSensitive { get; init; }
}

/// <summary>Защищённый путь с пояснением, которое увидит пользователь.</summary>
public sealed record ProtectedPath(string Path, string Reason);

/// <summary>
/// Расположение системных и пользовательских папок, от которых зависят правила безопасности
/// ручного удаления. Вынесено отдельно, чтобы правила можно было проверять тестами на
/// подставных папках, а в программе использовать <see cref="FromSystem"/>.
/// </summary>
public sealed class ManualDeletionPaths
{
    /// <summary>Профиль текущего пользователя, например C:\Users\name.</summary>
    public required string UserProfile { get; init; }

    /// <summary>Папка, в которой лежат профили, например C:\Users.</summary>
    public required string UsersRoot { get; init; }

    /// <summary>
    /// Папка самой программы. Саму папку и любую папку, в которой она лежит, удалять нельзя.
    /// Остальное внутри неё (например, чужие файлы в Downloads, если программа запущена оттуда) удалять можно —
    /// защищены только файлы программы, см. <see cref="AppExecutablePath"/> и <see cref="AppFileNamePrefix"/>.
    /// </summary>
    public required string AppBaseDirectory { get; init; }

    /// <summary>Полный путь к запущенному exe программы — удалять нельзя.</summary>
    public string? AppExecutablePath { get; init; }

    /// <summary>
    /// Файлы в папке программы, имя которых начинается с «&lt;префикс&gt;.» (например, DiskCleaner.exe, DiskCleaner.Core.dll,
    /// DiskCleaner.pdb), считаются файлами программы и не удаляются.
    /// </summary>
    public string? AppFileNamePrefix { get; init; }

    /// <summary>Путь, всё внутри него и любая папка, в которой он лежит, защищены.</summary>
    public IReadOnlyList<ProtectedPath> ProtectedSubtrees { get; init; } = Array.Empty<ProtectedPath>();

    /// <summary>Защищён только сам путь; удалять отдельные вещи внутри можно (например, C:\ProgramData).</summary>
    public IReadOnlyList<ProtectedPath> ProtectedExact { get; init; } = Array.Empty<ProtectedPath>();

    public static ManualDeletionPaths FromSystem()
    {
        static string N(string p) => System.IO.Path.GetFullPath(p).TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        var profileRaw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfile = N(string.IsNullOrWhiteSpace(profileRaw) ? System.IO.Path.GetTempPath() : profileRaw);
        var usersRoot = System.IO.Path.GetDirectoryName(userProfile) ?? userProfile;

        var subtrees = new List<ProtectedPath>();
        var exact = new List<ProtectedPath>();

        const string programs = "установленные программы — удаляй через «Приложения и возможности»";
        const string windows = "системная папка Windows";

        void AddSubtree(string? path, string reason)
        {
            if (!string.IsNullOrWhiteSpace(path))
                subtrees.Add(new ProtectedPath(N(path), reason));
        }

        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.Windows), windows);
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), programs);
        AddSubtree(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), programs);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            exact.Add(new ProtectedPath(N(programData), "общая папка данных программ — удаляй конкретные вложенные папки"));
            AddSubtree(System.IO.Path.Combine(programData, "Microsoft", "Crypto"), "ключи шифрования системы");
            AddSubtree(System.IO.Path.Combine(programData, "Microsoft", "Windows"), "данные Windows");
            AddSubtree(System.IO.Path.Combine(programData, "Microsoft", "Windows Defender"), "антивирус Windows");
            AddSubtree(System.IO.Path.Combine(programData, "Package Cache"), "кэш установщиков — без него ломается ремонт и удаление программ");
        }

        // Системные папки в корне каждого диска (как и в PathSafetyService — на любом диске).
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;
                AddSubtree(System.IO.Path.Combine(root, "Windows"), windows);
                AddSubtree(System.IO.Path.Combine(root, "Program Files"), programs);
                AddSubtree(System.IO.Path.Combine(root, "Program Files (x86)"), programs);
                AddSubtree(System.IO.Path.Combine(root, "$Recycle.Bin"), "корзина — очищай средствами Windows");
                AddSubtree(System.IO.Path.Combine(root, "System Volume Information"), "служебные данные Windows (точки восстановления)");
                AddSubtree(System.IO.Path.Combine(root, "Recovery"), "среда восстановления Windows");
                AddSubtree(System.IO.Path.Combine(root, "Boot"), "загрузчик Windows");
                AddSubtree(System.IO.Path.Combine(root, "Config.Msi"), "служебные данные установщика Windows");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Список дисков не прочитался — остаются защиты, собранные выше.
        }

        return new ManualDeletionPaths
        {
            UserProfile = userProfile,
            UsersRoot = usersRoot,
            AppBaseDirectory = N(AppContext.BaseDirectory),
            AppExecutablePath = string.IsNullOrWhiteSpace(Environment.ProcessPath) ? null : N(Environment.ProcessPath),
            AppFileNamePrefix = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
            ProtectedSubtrees = subtrees
                .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList(),
            ProtectedExact = exact,
        };
    }
}
