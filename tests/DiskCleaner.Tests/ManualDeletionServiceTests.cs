using System.IO;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

public sealed class ManualDeletionServiceTests
{
    // Подставная «файловая система»: <temp>\Users\bob — профиль, <temp>\Windows и <temp>\Program Files — системные,
    // <temp>\App — папка программы. Реальные системные папки тесты не трогают.
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
            new ProtectedPath(Path.Combine(root, "Program Files"), "установленные программы"),
        },
        ProtectedExact = new[]
        {
            new ProtectedPath(Path.Combine(root, "ProgramData"), "общая папка данных программ"),
        },
    };

    private static ManualDeletionService NewService(
        string root,
        Func<IEnumerable<string>>? excluded = null,
        IRecycleBinMover? recycleBin = null)
        => new(new ReparsePointGuard(), MakePaths(root), excluded, recycleBin ?? new FakeRecycleBin(root));

    private sealed class FakeRecycleBin : IRecycleBinMover
    {
        private readonly string _bin;
        public bool Fail { get; set; }
        public int Calls { get; private set; }

        public FakeRecycleBin(string root)
        {
            _bin = Path.Combine(root, "__bin__");
            Directory.CreateDirectory(_bin);
        }

        public bool TryMoveToRecycleBin(string path, bool isDirectory, out string? error)
        {
            Calls++;
            if (Fail)
            {
                error = "корзина недоступна";
                return false;
            }

            error = null;
            var target = Path.Combine(_bin, Path.GetFileName(path));
            if (isDirectory) Directory.Move(path, target); else File.Move(path, target);
            return true;
        }
    }

    private static ManualDeletionOptions Real(bool recycle = false, bool confirmed = false)
        => new() { DryRun = false, UseRecycleBin = recycle, ConfirmedSensitive = confirmed };

    // ------------------------------------------------------------- жёсткие запреты

    [Fact]
    public void Blocks_drive_root()
    {
        using var temp = new TempTestDirectory();
        var root = Path.GetPathRoot(temp.Path)!;

        var verdict = NewService(temp.Path).Evaluate(root);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("корень диска", verdict.BlockReason!);
    }

    [Fact]
    public void Blocks_missing_path()
    {
        using var temp = new TempTestDirectory();

        var verdict = NewService(temp.Path).Evaluate(Path.Combine(temp.Path, "nope"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("не существует", verdict.BlockReason!);
    }

    [Fact]
    public void Blocks_protected_subtree_and_anything_inside_it()
    {
        using var temp = new TempTestDirectory();
        var dll = temp.CreateFile("Windows/System32/x.dll");
        var service = NewService(temp.Path);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Windows")).IsAllowed);
        Assert.False(service.Evaluate(Path.GetDirectoryName(dll)!).IsAllowed);
        Assert.False(service.Evaluate(dll).IsAllowed);
    }

    [Fact]
    public void Blocks_folder_that_contains_a_protected_folder()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("outer/Windows/a.txt");
        temp.CreateFile("outer/other/b.txt");

        var paths = new ManualDeletionPaths
        {
            UserProfile = Path.Combine(temp.Path, "Users", "bob"),
            UsersRoot = Path.Combine(temp.Path, "Users"),
            AppBaseDirectory = Path.Combine(temp.Path, "App"),
            ProtectedSubtrees = new[] { new ProtectedPath(Path.Combine(temp.Path, "outer", "Windows"), "системная папка Windows") },
        };
        var service = new ManualDeletionService(new ReparsePointGuard(), paths, null, new FakeRecycleBin(temp.Path));

        // outer содержит Windows — рекурсивное удаление снесло бы её.
        var verdict = service.Evaluate(Path.Combine(temp.Path, "outer"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("внутри находится защищённая папка", verdict.BlockReason!);
    }

    [Fact]
    public void Blocks_program_files_programdata_root_but_allows_inside_programdata()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("Program Files/App/app.exe");
        var inner = temp.CreateFile("ProgramData/NVIDIA/update.exe");
        var service = NewService(temp.Path);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Program Files", "App")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "ProgramData")).IsAllowed);
        Assert.True(service.Evaluate(Path.GetDirectoryName(inner)!).IsAllowed);
    }

    [Fact]
    public void Blocks_users_root_profile_and_appdata_roots()
    {
        using var temp = new TempTestDirectory();
        temp.CreateSubDirectory("Users/bob/AppData/Local");
        temp.CreateSubDirectory("Users/bob/AppData/Roaming");
        temp.CreateSubDirectory("Users/bob/AppData/LocalLow");
        temp.CreateSubDirectory("Users/alice/AppData/Local");
        var service = NewService(temp.Path);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Local")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Roaming")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "LocalLow")).IsAllowed);
        // Чужой профиль защищён теми же правилами.
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "alice")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "alice", "AppData", "Local")).IsAllowed);
    }

    [Fact]
    public void Blocks_dpapi_keys_registry_hive_folder_and_their_parents()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("Users/bob/AppData/Roaming/Microsoft/Protect/key.bin");
        temp.CreateFile("Users/bob/AppData/Local/Microsoft/Windows/UsrClass.dat");
        temp.CreateFile("Users/bob/AppData/Local/Microsoft/Edge/data.bin");
        var service = NewService(temp.Path);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Roaming", "Microsoft", "Protect")).IsAllowed);
        // Родитель Protect: удаление Roaming\Microsoft снесло бы ключи.
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Roaming", "Microsoft")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Microsoft", "Windows")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Microsoft")).IsAllowed);
        // Сосед Windows внутри Local\Microsoft можно.
        Assert.True(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Microsoft", "Edge")).IsAllowed);
    }

    [Fact]
    public void Blocks_ntuser_file_in_profile()
    {
        using var temp = new TempTestDirectory();
        var ntuser = temp.CreateFile("Users/bob/NTUSER.DAT");

        Assert.False(NewService(temp.Path).Evaluate(ntuser).IsAllowed);
    }

    [Fact]
    public void Service_file_rule_applies_only_in_drive_root_not_in_subfolders()
    {
        using var temp = new TempTestDirectory();
        var hiber = temp.CreateFile("sub/hiberfil.sys");

        // Файл с таким именем в обычной подпапке — обычный файл.
        Assert.True(NewService(temp.Path).Evaluate(hiber).IsAllowed);
    }

    [Fact]
    public void Real_hiberfil_in_drive_root_is_blocked_when_present()
    {
        using var temp = new TempTestDirectory();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? Path.GetPathRoot(temp.Path)!;
        var hiber = Path.Combine(systemRoot, "hiberfil.sys");
        if (!File.Exists(hiber))
            return; // нет файла гибернации (или не Windows) — проверять нечего. Evaluate только читает.

        var verdict = NewService(temp.Path).Evaluate(hiber);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("powercfg", verdict.BlockReason!);
    }

    [Fact]
    public void Blocks_app_directory_its_parent_and_the_apps_own_files()
    {
        using var temp = new TempTestDirectory();
        temp.CreateFile("App/Tool.exe");
        temp.CreateFile("App/Tool.Core.dll");
        temp.CreateFile("App/notes.txt");
        var service = NewService(temp.Path);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "App")).IsAllowed);        // сама папка
        Assert.False(service.Evaluate(temp.Path).IsAllowed);                              // папка, в которой она лежит
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "App", "Tool.exe")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "App", "Tool.Core.dll")).IsAllowed);
    }

    [Fact]
    public void Does_not_block_other_files_next_to_the_app()
    {
        using var temp = new TempTestDirectory();
        // Программа запущена из Downloads: остальные файлы там удалять можно.
        var paths = new ManualDeletionPaths
        {
            UserProfile = Path.Combine(temp.Path, "Users", "bob"),
            UsersRoot = Path.Combine(temp.Path, "Users"),
            AppBaseDirectory = Path.Combine(temp.Path, "Users", "bob", "Downloads"),
            AppExecutablePath = Path.Combine(temp.Path, "Users", "bob", "Downloads", "Tool.exe"),
            AppFileNamePrefix = "Tool",
        };
        var service = new ManualDeletionService(new ReparsePointGuard(), paths, null, new FakeRecycleBin(temp.Path));
        var model = temp.CreateFile("Users/bob/Downloads/model.gguf", "data");
        var folder = Path.GetDirectoryName(temp.CreateFile("Users/bob/Downloads/old-installers/a.msi", "x"))!;
        var exe = temp.CreateFile("Users/bob/Downloads/Tool.exe");

        Assert.True(service.Evaluate(model).IsAllowed);   // с подтверждением (личные файлы), но не заблокирован
        Assert.True(service.Evaluate(folder).IsAllowed);
        Assert.False(service.Evaluate(exe).IsAllowed);
    }

    [Fact]
    public void Blocks_symlinked_target()
    {
        using var temp = new TempTestDirectory();
        using var outside = new TempTestDirectory();
        outside.CreateFile("data.txt");
        var link = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // среда без прав на создание symlink
        }

        Assert.False(NewService(temp.Path).Evaluate(link).IsAllowed);
    }

    // ------------------------------------------------------------- исключения

    [Fact]
    public void Excluded_paths_apply_immediately_and_protect_their_parents()
    {
        using var temp = new TempTestDirectory();
        var keep = temp.CreateFile("Users/bob/AppData/Local/Tool/keep/important.txt");
        var folder = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Tool");
        var excluded = new List<string>();
        var service = NewService(temp.Path, () => excluded);

        Assert.True(service.Evaluate(folder).IsAllowed);

        excluded.Add(Path.GetDirectoryName(keep)!); // исключили подпапку уже после создания сервиса

        var verdict = service.Evaluate(folder);
        Assert.False(verdict.IsAllowed);
        Assert.Contains("исключений", verdict.BlockReason!);
        Assert.False(service.Evaluate(keep).IsAllowed);
    }

    [Fact]
    public void Unreadable_exclusion_list_blocks_deletion_instead_of_ignoring_exclusions()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/data.bin", "x");
        var service = NewService(temp.Path, () => throw new InvalidOperationException("Collection was modified"));

        var verdict = service.Evaluate(file);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("список исключений", verdict.BlockReason!);
    }

    [Fact]
    public void Exclusion_list_read_is_retried_after_a_transient_failure()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/data.bin", "x");
        var calls = 0;
        var service = NewService(temp.Path, () =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("Collection was modified");
            return new[] { file };
        });

        var verdict = service.Evaluate(file);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("исключений", verdict.BlockReason!); // второе чтение прошло и исключение сработало
        Assert.Equal(2, calls);
    }

    // ------------------------------------------------------------- подтверждения

    [Fact]
    public void Personal_files_are_allowed_only_with_confirmation()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/Downloads/model.gguf", "data");
        var service = NewService(temp.Path);

        var verdict = service.Evaluate(file);
        Assert.True(verdict.IsAllowed);
        Assert.True(verdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.PersonalFiles, verdict.Sensitivity);

        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "Downloads")).IsAllowed);
        Assert.False(service.Evaluate(Path.Combine(temp.Path, "Users", "bob", "Desktop")).IsAllowed);
    }

    [Fact]
    public void Browser_profile_data_requires_confirmation_but_cache_does_not()
    {
        using var temp = new TempTestDirectory();
        var idb = temp.CreateFile("Users/bob/AppData/Roaming/Mozilla/Firefox/Profiles/x.default/storage/default/https+++tiktok.com/idb/1.sqlite");
        var cache = temp.CreateFile("Users/bob/AppData/Local/Mozilla/Firefox/Profiles/x.default/cache2/entries/a");
        var service = NewService(temp.Path);

        var idbVerdict = service.Evaluate(Path.GetDirectoryName(idb)!);
        Assert.True(idbVerdict.RequiresConfirmation);
        Assert.Equal(DeletionSensitivity.BrowserProfile, idbVerdict.Sensitivity);

        var cacheVerdict = service.Evaluate(Path.GetDirectoryName(cache)!);
        Assert.True(cacheVerdict.IsAllowed);
        Assert.False(cacheVerdict.RequiresConfirmation);
    }

    [Fact]
    public void Ordinary_cache_folder_needs_no_confirmation()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/pip/Cache/http/a.body");

        var verdict = NewService(temp.Path).Evaluate(Path.GetDirectoryName(file)!);

        Assert.True(verdict.IsAllowed);
        Assert.False(verdict.RequiresConfirmation);
    }

    // ------------------------------------------------------------- удаление

    [Fact]
    public void Dry_run_deletes_nothing_and_reports_size()
    {
        using var temp = new TempTestDirectory();
        var folder = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Cache");
        temp.CreateFile("Users/bob/AppData/Local/Cache/a.bin", "12345");
        temp.CreateFile("Users/bob/AppData/Local/Cache/sub/b.bin", "1234567890");

        var result = NewService(temp.Path).Delete(new[] { folder }, new ManualDeletionOptions { DryRun = true });

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.DryRun);
        Assert.Equal(15, entry.SizeBeforeBytes);
        Assert.Equal(0, entry.SizeAfterBytes);   // «освободилось бы» всё
        Assert.Equal(15, entry.FreedBytes);
        Assert.Equal(0, entry.FilesDeleted);     // но ничего не удалено
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(folder, "sub", "b.bin")));
    }

    [Fact]
    public void Deletes_directory_tree_including_the_directory_itself()
    {
        using var temp = new TempTestDirectory();
        var folder = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Cache");
        temp.CreateFile("Users/bob/AppData/Local/Cache/a.bin", "12345");
        temp.CreateFile("Users/bob/AppData/Local/Cache/sub/deep/b.bin", "1234567890");
        temp.CreateFile("Users/bob/AppData/Local/Other/keep.txt", "keep");

        var result = NewService(temp.Path).Delete(new[] { folder }, Real());

        var entry = Assert.Single(result.Entries);
        Assert.Equal(15, entry.SizeBeforeBytes);
        Assert.Equal(0, entry.SizeAfterBytes);
        Assert.Equal(2, entry.FilesDeleted);
        Assert.Equal(0, entry.Errors.Count);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Other", "keep.txt")));
    }

    [Fact]
    public void Deletes_single_file_and_clears_read_only_flag()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/ro.bin", "abc");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var result = NewService(temp.Path).Delete(new[] { file }, Real());

        var entry = Assert.Single(result.Entries);
        Assert.Equal(1, entry.FilesDeleted);
        Assert.Equal(3, entry.SizeBeforeBytes);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Blocked_path_is_reported_and_left_untouched()
    {
        using var temp = new TempTestDirectory();
        var dll = temp.CreateFile("Windows/System32/x.dll", "system");

        var result = NewService(temp.Path).Delete(new[] { Path.Combine(temp.Path, "Windows") }, Real());

        var entry = Assert.Single(result.Entries);
        Assert.Equal(0, entry.FilesDeleted);
        Assert.Contains(entry.Errors, e => e.Contains("Заблокировано"));
        Assert.True(File.Exists(dll));
    }

    [Fact]
    public void Sensitive_path_is_skipped_without_confirmation_and_deleted_with_it()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/Downloads/old.rar", "data");
        var service = NewService(temp.Path);

        var skipped = service.Delete(new[] { file }, Real(confirmed: false));
        Assert.Contains(Assert.Single(skipped.Entries).Errors, e => e.Contains("Нужно подтверждение"));
        Assert.True(File.Exists(file));

        var deleted = service.Delete(new[] { file }, Real(confirmed: true));
        Assert.Equal(1, Assert.Single(deleted.Entries).FilesDeleted);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Path_is_rechecked_right_before_deletion()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/data.bin", "x");
        var excluded = new List<string>();
        var service = NewService(temp.Path, () => excluded);

        Assert.True(service.Evaluate(file).IsAllowed);   // «на экране» всё разрешено
        excluded.Add(file);                              // потом пользователь добавил исключение

        var result = service.Delete(new[] { file }, Real());

        Assert.Contains(Assert.Single(result.Entries).Errors, e => e.Contains("Заблокировано"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Removes_symlink_inside_directory_without_touching_its_target()
    {
        using var temp = new TempTestDirectory();
        using var outside = new TempTestDirectory();
        var outsideFile = outside.CreateFile("precious.txt", "keep me");

        var folder = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Tool");
        temp.CreateFile("Users/bob/AppData/Local/Tool/a.bin", "12345");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // среда без прав на создание symlink
        }

        var result = NewService(temp.Path).Delete(new[] { folder }, Real());

        Assert.Equal(0, Assert.Single(result.Entries).Errors.Count);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public void Recycle_bin_mode_uses_mover_and_does_not_delete_permanently()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/big.bin", "123456");
        var bin = new FakeRecycleBin(temp.Path);
        var service = NewService(temp.Path, recycleBin: bin);

        var result = service.Delete(new[] { file }, Real(recycle: true));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(1, bin.Calls);
        Assert.Contains("корзину", entry.TargetDisplayName);
        Assert.Equal(1, entry.FilesDeleted);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(temp.Path, "__bin__", "big.bin")));
    }

    [Fact]
    public void Recycle_bin_failure_is_reported_and_file_stays()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/big.bin", "123456");
        var bin = new FakeRecycleBin(temp.Path) { Fail = true };
        var service = NewService(temp.Path, recycleBin: bin);

        var result = service.Delete(new[] { file }, Real(recycle: true));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(0, entry.FilesDeleted);
        Assert.Equal(6, entry.SizeAfterBytes);
        Assert.Contains(entry.Errors, e => e.Contains("корзину"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Selecting_folder_and_file_inside_processes_only_the_folder_once()
    {
        using var temp = new TempTestDirectory();
        var folder = Path.Combine(temp.Path, "Users", "bob", "AppData", "Local", "Cache");
        var inner = temp.CreateFile("Users/bob/AppData/Local/Cache/a.bin", "12345");

        var result = NewService(temp.Path).Delete(new[] { inner, folder, folder }, Real());

        var entry = Assert.Single(result.Entries);
        Assert.Equal(1, entry.FilesDeleted);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Cancelled_token_marks_result_cancelled_and_deletes_nothing()
    {
        using var temp = new TempTestDirectory();
        var file = temp.CreateFile("Users/bob/AppData/Local/Tool/a.bin", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = NewService(temp.Path).Delete(new[] { file }, Real(), progress: null, cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
        Assert.True(File.Exists(file));
    }
}
