using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>
/// Ручное удаление: личные данные (по расширению и по индексу анализа), «Мои проекты», приоритет вердикта
/// и склейка заметок. Подставная «файловая система» во временной папке — реальные системные папки не трогаются.
/// </summary>
public sealed class ManualDeletionPersonalDataTests
{
    private static ManualDeletionPaths MakePaths(string root) => new()
    {
        UserProfile = Path.Combine(root, "Users", "bob"),
        UsersRoot = Path.Combine(root, "Users"),
        AppBaseDirectory = Path.Combine(root, "App"),
        AppExecutablePath = Path.Combine(root, "App", "Tool.exe"),
        AppFileNamePrefix = "Tool",
        ProtectedSubtrees = new[]
        {
            new ProtectedPath(Path.Combine(root, "Windows"), "системная папка Windows"),
        },
        ProtectedExact = new[]
        {
            new ProtectedPath(Path.Combine(root, "ProgramData"), "общая папка данных программ"),
        },
    };

    private sealed class NoRecycleBin : IRecycleBinMover
    {
        public bool TryMoveToRecycleBin(string path, bool isDirectory, out string? error)
        {
            error = "корзина в этих тестах не используется";
            return false;
        }
    }

    private static ManualDeletionService NewService(
        string root,
        Func<IEnumerable<string>>? projects = null,
        PersonalContentIndex? index = null,
        Func<IEnumerable<string>>? excluded = null)
        => new(new ReparsePointGuard(), MakePaths(root), excluded, new NoRecycleBin(), projects, index);

    private static string Norm(string path) => PathSafetyService.Normalize(path);

    private static ManualDeletionOptions Real(bool confirmed) => new() { DryRun = false, ConfirmedSensitive = confirmed };

    private static PersonalFolderInfo FolderInfo(string path, PersonalFolderReason reasons, int videos = 3)
    {
        var stats = default(PersonalStats);
        for (var i = 0; i < videos; i++)
            stats = stats.Add(PersonalDataCategory.Video, 1000, counted: true);
        return new PersonalFolderInfo(path, stats, stats.TotalBytes, reasons);
    }

    // ------------------------------------------------------------------ личные файлы по расширению

    [Theory]
    [InlineData("clip.mkv", "личный файл: видео")]
    [InlineData("song.flac", "личный файл: аудио")]
    [InlineData("photo.heic", "личный файл: изображения")]
    [InlineData("report.docx", "личный файл: документы")]
    [InlineData("backup.7z", "личный файл: архивы")]
    [InlineData("passwords.kdbx", "личный файл: образы ВМ, хранилища паролей, почта")]
    [InlineData("disk.vhdx", "личный файл: образы ВМ, хранилища паролей, почта")]
    [InlineData("mail.pst", "личный файл: образы ВМ, хранилища паролей, почта")]
    [InlineData("CLIP.MKV", "личный файл: видео")]
    public void Personal_file_outside_the_profile_needs_confirmation(string fileName, string expectedNote)
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Media", fileName), "x");

        var verdict = NewService(temp.Path).Evaluate(file);

        Assert.True(verdict.IsAllowed);
        Assert.True(verdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.PersonalData, verdict.Sensitivity);
        Assert.Equal(expectedNote, verdict.SensitivityNote);
    }

    [Theory]
    [InlineData("app.ts")]
    [InlineData("notes.txt")]
    [InlineData("Program.cs")]
    [InlineData("data.json")]
    [InlineData("setup.msi")]
    [InlineData("lib.jar")]
    public void Technical_file_outside_the_profile_is_allowed_without_confirmation(string fileName)
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Work", fileName), "x");

        var verdict = NewService(temp.Path).Evaluate(file);

        Assert.True(verdict.IsAllowed);
        Assert.False(verdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.None, verdict.Sensitivity);
    }

    // ------------------------------------------------------------------ приоритет и склейка заметок

    [Fact]
    public void Personal_folder_of_the_profile_and_personal_extension_are_both_reported_with_the_stricter_first()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Users", "bob", "Downloads", "movie.mp4"), "x");

        var verdict = NewService(temp.Path).Evaluate(file);

        Assert.Equal(DeletionSensitivity.PersonalFiles, verdict.Sensitivity);
        Assert.Equal("личные файлы в папке «Downloads»; личный файл: видео", verdict.SensitivityNote);
    }

    [Fact]
    public void Personal_extension_wins_over_project_folder_and_notes_are_joined_in_priority_order()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "game");
        var file = temp.CreateFile(Path.Combine("Projects", "game", "intro.mp4"), "x");

        var verdict = NewService(temp.Path, projects: () => new[] { project }).Evaluate(file);

        Assert.Equal(DeletionSensitivity.PersonalData, verdict.Sensitivity);
        Assert.Equal($"личный файл: видео; внутри вашей папки проектов: {Norm(project)}", verdict.SensitivityNote);
    }

    [Fact]
    public void All_three_reasons_give_the_strictest_sensitivity_and_a_note_with_every_reason()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Users", "bob", "Documents", "dev");
        var file = temp.CreateFile(Path.Combine("Users", "bob", "Documents", "dev", "demo.mp4"), "x");

        var verdict = NewService(temp.Path, projects: () => new[] { project }).Evaluate(file);

        Assert.Equal(DeletionSensitivity.PersonalFiles, verdict.Sensitivity);
        Assert.Equal(
            $"личные файлы в папке «Documents»; личный файл: видео; внутри вашей папки проектов: {Norm(project)}",
            verdict.SensitivityNote);
    }

    [Fact]
    public void Personal_extension_wins_over_browser_profile_data_and_both_notes_are_kept()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(
            Path.Combine("Users", "bob", "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "abc.default", "backup.zip"), "x");

        var verdict = NewService(temp.Path).Evaluate(file);

        Assert.Equal(DeletionSensitivity.PersonalData, verdict.Sensitivity);
        Assert.StartsWith("личный файл: архивы; ", verdict.SensitivityNote!);
        Assert.Contains("данные внутри профиля браузера", verdict.SensitivityNote!);
    }

    [Fact]
    public void Project_folder_alone_gives_the_project_sensitivity_with_the_exact_note()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "app");
        var file = temp.CreateFile(Path.Combine("Projects", "app", "src", "Main.cs"), "x");

        var verdict = NewService(temp.Path, projects: () => new[] { project }).Evaluate(file);

        Assert.True(verdict.IsAllowed);
        Assert.True(verdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.ProjectFolder, verdict.Sensitivity);
        Assert.Equal($"внутри вашей папки проектов: {Norm(project)}", verdict.SensitivityNote);
    }

    [Fact]
    public void Inherited_personal_folder_and_project_are_reported_together_with_personal_data_first()
    {
        using var temp = new TempTestDirectory();
        var media = Path.Combine(temp.Path, "Media");
        var project = Path.Combine(media, "edit");
        var file = temp.CreateFile(Path.Combine("Media", "edit", "timeline.xml"), "x");
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(temp.Path, new[] { FolderInfo(media, PersonalFolderReason.ByShare) });

        var verdict = NewService(temp.Path, projects: () => new[] { project }, index: index).Evaluate(file);

        Assert.Equal(DeletionSensitivity.PersonalData, verdict.Sensitivity);
        Assert.Equal($"внутри личной папки {Norm(media)}; внутри вашей папки проектов: {Norm(project)}", verdict.SensitivityNote);
    }

    // ------------------------------------------------------------------ индекс личных папок

    [Fact]
    public void Folder_from_the_index_needs_confirmation_and_the_note_describes_its_contents()
    {
        using var temp = new TempTestDirectory();
        var video = temp.CreateSubDirectory("Video");
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(temp.Path, new[] { FolderInfo(video, PersonalFolderReason.ByFileCount, videos: 20) });

        var verdict = NewService(temp.Path, index: index).Evaluate(video);

        Assert.True(verdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.PersonalData, verdict.Sensitivity);
        Assert.Contains("личные файлы по составу папки", verdict.SensitivityNote!);
        Assert.Contains("видео: 20 шт.", verdict.SensitivityNote!);
    }

    [Fact]
    public void Contents_of_a_folder_found_by_share_inherit_the_status()
    {
        using var temp = new TempTestDirectory();
        var video = temp.CreateSubDirectory("Video");
        var file = temp.CreateFile(Path.Combine("Video", "sub", "notes.txt"), "x");
        var sub = Path.GetDirectoryName(file)!;
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(temp.Path, new[] { FolderInfo(video, PersonalFolderReason.ByShare) });
        var service = NewService(temp.Path, index: index);

        var fileVerdict = service.Evaluate(file);
        var subVerdict = service.Evaluate(sub);

        Assert.Equal(DeletionSensitivity.PersonalData, fileVerdict.Sensitivity);
        Assert.Equal($"внутри личной папки {Norm(video)}", fileVerdict.SensitivityNote);
        Assert.Equal(DeletionSensitivity.PersonalData, subVerdict.Sensitivity);
    }

    [Fact]
    public void Contents_of_a_folder_found_only_by_count_do_not_inherit_the_status()
    {
        using var temp = new TempTestDirectory();
        var video = temp.CreateSubDirectory("Video");
        var file = temp.CreateFile(Path.Combine("Video", "sub", "notes.txt"), "x");
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(temp.Path, new[] { FolderInfo(video, PersonalFolderReason.ByFileCount) });
        var service = NewService(temp.Path, index: index);

        Assert.True(service.Evaluate(video).RequiresConfirmation);          // сама папка — с подтверждением
        var inner = service.Evaluate(file);
        Assert.True(inner.IsAllowed);
        Assert.False(inner.RequiresConfirmation);                           // содержимое — как обычная папка
        Assert.False(service.Evaluate(Path.GetDirectoryName(file)!).RequiresConfirmation);
    }

    [Fact]
    public void Folder_outside_the_index_has_no_content_protection_but_its_personal_files_still_do()
    {
        // Известное ограничение: содержимое папки вне результата анализа не проверяется.
        using var temp = new TempTestDirectory();
        var folder = temp.CreateSubDirectory("Видео");
        var file = temp.CreateFile(Path.Combine("Видео", "film.mkv"), "x");
        var noIndex = NewService(temp.Path);
        var emptyIndex = NewService(temp.Path, index: new PersonalContentIndex());

        Assert.False(noIndex.Evaluate(folder).RequiresConfirmation);
        Assert.False(emptyIndex.Evaluate(folder).RequiresConfirmation);
        Assert.True(noIndex.Evaluate(file).RequiresConfirmation);
        Assert.True(emptyIndex.Evaluate(file).RequiresConfirmation);
    }

    [Fact]
    public void Index_filled_after_the_service_was_created_takes_effect_immediately()
    {
        using var temp = new TempTestDirectory();
        var video = temp.CreateSubDirectory("Video");
        var index = new PersonalContentIndex();
        var service = NewService(temp.Path, index: index);

        Assert.False(service.Evaluate(video).RequiresConfirmation);

        index.ReplaceForRoot(temp.Path, new[] { FolderInfo(video, PersonalFolderReason.ByShare) });
        Assert.True(service.Evaluate(video).RequiresConfirmation);

        index.Clear();
        Assert.False(service.Evaluate(video).RequiresConfirmation);
    }

    // ------------------------------------------------------------------ «Мои проекты»

    [Fact]
    public void Project_root_is_blocked_as_a_whole()
    {
        using var temp = new TempTestDirectory();
        var project = temp.CreateSubDirectory(Path.Combine("Projects", "game"));

        var verdict = NewService(temp.Path, projects: () => new[] { project }).Evaluate(project);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("корень вашей папки проектов", verdict.BlockReason!);
        Assert.Contains(Norm(project), verdict.BlockReason!);
    }

    [Fact]
    public void Project_root_with_trailing_separator_or_other_case_is_still_blocked()
    {
        using var temp = new TempTestDirectory();
        var project = temp.CreateSubDirectory(Path.Combine("Projects", "game"));
        var service = NewService(temp.Path, projects: () => new[] { project.ToUpperInvariant() + Path.DirectorySeparatorChar });

        var verdict = service.Evaluate(project);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("корень вашей папки проектов", verdict.BlockReason!);
    }

    [Fact]
    public void Ancestor_of_a_project_is_blocked()
    {
        using var temp = new TempTestDirectory();
        var project = temp.CreateSubDirectory(Path.Combine("Work", "client", "game"));
        var parent = Path.Combine(temp.Path, "Work");
        var service = NewService(temp.Path, projects: () => new[] { project });

        var verdict = service.Evaluate(parent);
        var closer = service.Evaluate(Path.Combine(temp.Path, "Work", "client"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("внутри лежит ваша папка проектов", verdict.BlockReason!);
        Assert.False(closer.IsAllowed);
    }

    [Fact]
    public void Folder_with_a_common_name_prefix_is_not_part_of_the_project()
    {
        using var temp = new TempTestDirectory();
        var project = temp.CreateSubDirectory("Projects");
        var sibling = temp.CreateFile(Path.Combine("Projects2", "a.cs"), "x");

        var verdict = NewService(temp.Path, projects: () => new[] { project }).Evaluate(sibling);

        Assert.True(verdict.IsAllowed);
        Assert.False(verdict.RequiresConfirmation);
    }

    [Fact]
    public void Project_list_is_read_again_on_every_check()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "game");
        var file = temp.CreateFile(Path.Combine("Projects", "game", "Main.cs"), "x");
        var projects = new List<string>();
        var service = NewService(temp.Path, projects: () => projects);

        Assert.False(service.Evaluate(file).RequiresConfirmation);

        projects.Add(project);                                   // добавили уже после создания сервиса
        var after = service.Evaluate(file);
        Assert.True(after.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.ProjectFolder, after.Sensitivity);
        Assert.False(service.Evaluate(project).IsAllowed);

        projects.Clear();                                        // убрали — защита снята
        Assert.False(service.Evaluate(file).RequiresConfirmation);
        Assert.True(service.Evaluate(project).IsAllowed);
    }

    [Fact]
    public void Project_list_that_keeps_failing_blocks_everything()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Work", "a.txt"), "x");
        var service = NewService(temp.Path, projects: () => throw new InvalidOperationException("изменили коллекцию"));

        var verdict = service.Evaluate(file);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("не удалось прочитать список папок проектов", verdict.BlockReason!);
    }

    [Fact]
    public void Project_list_that_fails_once_is_retried()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "game");
        var file = temp.CreateFile(Path.Combine("Projects", "game", "Main.cs"), "x");
        var attempts = 0;
        var service = NewService(temp.Path, projects: () =>
        {
            if (attempts++ == 0)
                throw new InvalidOperationException("transient");
            return new[] { project };
        });

        var verdict = service.Evaluate(file);

        Assert.True(attempts >= 2);
        Assert.Equal(DeletionSensitivity.ProjectFolder, verdict.Sensitivity);
    }

    [Fact]
    public void Garbage_entries_in_the_project_list_are_ignored()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "game");
        var file = temp.CreateFile(Path.Combine("Projects", "game", "Main.cs"), "x");
        var service = NewService(temp.Path, projects: () => new[] { "", "   ", "bad\0path", project });

        var verdict = service.Evaluate(file);

        Assert.True(verdict.IsAllowed);
        Assert.Equal(DeletionSensitivity.ProjectFolder, verdict.Sensitivity);
    }

    [Fact]
    public void Without_a_projects_provider_nothing_is_treated_as_a_project()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Projects", "game", "Main.cs"), "x");

        Assert.False(NewService(temp.Path).Evaluate(file).RequiresConfirmation);
    }

    // ------------------------------------------------------------------ само удаление

    [Fact]
    public void Personal_file_outside_the_profile_is_skipped_without_confirmation_and_deleted_with_it()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile(Path.Combine("Media", "clip.mkv"), "data");
        var service = NewService(temp.Path);

        var skipped = service.Delete(new[] { file }, Real(confirmed: false));
        Assert.Contains(Assert.Single(skipped.Entries).Errors, e => e.Contains("Нужно подтверждение"));
        Assert.True(File.Exists(file));

        var deleted = service.Delete(new[] { file }, Real(confirmed: true));
        Assert.Equal(1, Assert.Single(deleted.Entries).FilesDeleted);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void File_inside_a_project_is_skipped_without_confirmation_and_deleted_with_it()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "app");
        var file = temp.CreateFile(Path.Combine("Projects", "app", "obj", "cache.bin"), "data");
        var service = NewService(temp.Path, projects: () => new[] { project });

        var skipped = service.Delete(new[] { file }, Real(confirmed: false));
        Assert.Contains(Assert.Single(skipped.Entries).Errors, e => e.Contains("внутри вашей папки проектов"));
        Assert.True(File.Exists(file));

        service.Delete(new[] { file }, Real(confirmed: true));
        Assert.False(File.Exists(file));
        Assert.True(Directory.Exists(project)); // сам проект остался
    }

    [Fact]
    public void Deleting_the_project_root_is_refused_even_when_confirmed()
    {
        using var temp = new TempTestDirectory();
        var project = temp.CreateSubDirectory(Path.Combine("Projects", "app"));
        File.WriteAllText(Path.Combine(project, "Main.cs"), "x");
        var service = NewService(temp.Path, projects: () => new[] { project });

        var result = service.Delete(new[] { project }, Real(confirmed: true));

        Assert.Contains(Assert.Single(result.Entries).Errors, e => e.Contains("Заблокировано") && e.Contains("корень вашей папки проектов"));
        Assert.True(File.Exists(Path.Combine(project, "Main.cs")));
    }

    [Fact]
    public void Verdict_is_checked_again_right_before_deletion_when_the_project_list_changes()
    {
        using var temp = new TempTestDirectory();
        var project = Path.Combine(temp.Path, "Projects", "app");
        var file = temp.CreateFile(Path.Combine("Projects", "app", "a.txt"), "x");
        var projects = new List<string>();
        var service = NewService(temp.Path, projects: () => projects);

        Assert.True(service.Evaluate(file).IsAllowed);   // «на экране» всё разрешено
        projects.Add(project);                           // потом пользователь добавил проект

        var result = service.Delete(new[] { file }, Real(confirmed: false));

        Assert.Contains(Assert.Single(result.Entries).Errors, e => e.Contains("Нужно подтверждение"));
        Assert.True(File.Exists(file));
    }
}
