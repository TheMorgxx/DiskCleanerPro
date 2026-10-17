using DiskCleaner.Core.Models;
using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>Индекс личных папок: поиск, наследование, замена записей корня и потокобезопасность.</summary>
public sealed class PersonalContentIndexTests
{
    // Индекс работает только со строками путей и ничего не читает с диска, поэтому папки не создаются.
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "DiskCleanerIndexTests");

    private static string P(params string[] parts) => Path.Combine(new[] { Base }.Concat(parts).ToArray());

    private static PersonalFolderInfo Info(string path, PersonalFolderReason reasons = PersonalFolderReason.ByShare)
        => new(path, default(PersonalStats).Add(PersonalDataCategory.Video, 10, counted: true), 10, reasons);

    // ------------------------------------------------------------------ поиск

    [Fact]
    public void Empty_index_finds_nothing()
    {
        var index = new PersonalContentIndex();

        Assert.Equal(0, index.Count);
        Assert.False(index.TryGetFolder(P("Video"), out var info));
        Assert.Null(info);
        Assert.Null(index.FindInheritingAncestor(P("Video", "x")));
    }

    [Fact]
    public void Finds_a_folder_regardless_of_trailing_separator_case_and_dot_dot()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[] { Info(P("Video")) });

        Assert.True(index.TryGetFolder(P("Video"), out var exact));
        Assert.Equal(1, index.Count);
        Assert.NotNull(exact);
        Assert.True(index.TryGetFolder(P("Video") + Path.DirectorySeparatorChar, out _));
        Assert.True(index.TryGetFolder(P("VIDEO"), out _));
        Assert.True(index.TryGetFolder(P("Other", "..", "Video"), out _));
        Assert.False(index.TryGetFolder(P("Video2"), out _));
        Assert.False(index.TryGetFolder(P("Video", "sub"), out _));
    }

    [Fact]
    public void Stored_entries_keep_their_stats_and_reasons()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[] { Info(P("Video"), PersonalFolderReason.ByFileCount | PersonalFolderReason.ByTotalSize) });

        Assert.True(index.TryGetFolder(P("Video"), out var info));
        Assert.Equal(PersonalFolderReason.ByFileCount | PersonalFolderReason.ByTotalSize, info!.Reasons);
        Assert.Equal(1, info.Stats.Video.Files);
    }

    // ------------------------------------------------------------------ наследование

    [Fact]
    public void Nearest_inheriting_ancestor_is_returned_and_the_path_itself_is_not_considered()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[] { Info(P("Video")), Info(P("Video", "Films")) });

        Assert.Equal(Path.GetFullPath(P("Video", "Films")), index.FindInheritingAncestor(P("Video", "Films", "a.txt"))!.Path);
        Assert.Equal(Path.GetFullPath(P("Video")), index.FindInheritingAncestor(P("Video", "Films"))!.Path);
        Assert.Null(index.FindInheritingAncestor(P("Video")));
    }

    [Fact]
    public void Ancestor_found_by_count_or_total_size_only_does_not_pass_the_status_down()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[]
        {
            Info(P("ByCount"), PersonalFolderReason.ByFileCount),
            Info(P("BySize"), PersonalFolderReason.ByTotalSize),
        });

        Assert.Null(index.FindInheritingAncestor(P("ByCount", "sub", "a.txt")));
        Assert.Null(index.FindInheritingAncestor(P("BySize", "sub")));
    }

    [Fact]
    public void Non_inheriting_ancestor_is_skipped_and_a_farther_inheriting_one_is_used()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[]
        {
            Info(P("Media")),                                              // доля
            Info(P("Media", "Inner"), PersonalFolderReason.ByFileCount),   // только количество
        });

        var ancestor = index.FindInheritingAncestor(P("Media", "Inner", "deep", "a.txt"));

        Assert.NotNull(ancestor);
        Assert.Equal(Path.GetFullPath(P("Media")), ancestor!.Path);
    }

    [Fact]
    public void Sibling_with_a_common_name_prefix_is_not_a_descendant()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P(), new[] { Info(P("Video")) });

        Assert.Null(index.FindInheritingAncestor(P("Video2", "a.txt")));
        Assert.Null(index.FindInheritingAncestor(P("VideoArchive")));
    }

    // ------------------------------------------------------------------ несколько корней

    [Fact]
    public void Replace_for_a_root_replaces_only_that_roots_entries()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P("R1"), new[] { Info(P("R1", "a")), Info(P("R1", "b")) });
        index.ReplaceForRoot(P("R2"), new[] { Info(P("R2", "x")) });
        Assert.Equal(3, index.Count);

        index.ReplaceForRoot(P("R1"), new[] { Info(P("R1", "c")) });

        Assert.Equal(2, index.Count);
        Assert.False(index.TryGetFolder(P("R1", "a"), out _));
        Assert.False(index.TryGetFolder(P("R1", "b"), out _));
        Assert.True(index.TryGetFolder(P("R1", "c"), out _));
        Assert.True(index.TryGetFolder(P("R2", "x"), out _));
    }

    [Fact]
    public void Replacing_a_root_with_nothing_removes_its_entries_and_clear_removes_everything()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P("R1"), new[] { Info(P("R1", "a")) });
        index.ReplaceForRoot(P("R2"), new[] { Info(P("R2", "x")) });

        index.ReplaceForRoot(P("R1"), Array.Empty<PersonalFolderInfo>());
        Assert.Equal(1, index.Count);

        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.False(index.TryGetFolder(P("R2", "x"), out _));
    }

    [Fact]
    public void Root_key_is_normalized_so_a_trailing_separator_still_addresses_the_same_root()
    {
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P("R1"), new[] { Info(P("R1", "a")) });

        index.ReplaceForRoot(P("R1") + Path.DirectorySeparatorChar, new[] { Info(P("R1", "b")) });

        Assert.Equal(1, index.Count);
        Assert.True(index.TryGetFolder(P("R1", "b"), out _));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var index = new PersonalContentIndex();

        Assert.Throws<ArgumentNullException>(() => index.ReplaceForRoot(null!, Array.Empty<PersonalFolderInfo>()));
        Assert.Throws<ArgumentException>(() => index.ReplaceForRoot("   ", Array.Empty<PersonalFolderInfo>()));
        Assert.Throws<ArgumentNullException>(() => index.ReplaceForRoot(P("R1"), null!));
    }

    // ------------------------------------------------------------------ потокобезопасность

    private static void WaitAll(params Task[] tasks)
        => Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(120)), "Потоки не завершились за 120 секунд");

    [Fact]
    public void Readers_see_either_the_old_or_the_new_snapshot_of_a_root_never_a_mix()
    {
        // Корень R2 не меняется (3 записи). Корень R1 попеременно заменяется то 2, то 4 записями.
        // Чтение Count идёт по одному снимку, поэтому допустимы только суммы 5 и 7.
        var index = new PersonalContentIndex();
        index.ReplaceForRoot(P("R2"), Enumerable.Range(0, 3).Select(i => Info(P("R2", "f" + i))).ToList());

        var two = Enumerable.Range(0, 2).Select(i => Info(P("R1", "f" + i))).ToList();
        var four = Enumerable.Range(0, 4).Select(i => Info(P("R1", "f" + i))).ToList();
        index.ReplaceForRoot(P("R1"), two);

        using var stop = new CancellationTokenSource();
        var bad = new System.Collections.Concurrent.ConcurrentBag<int>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 3000; i++)
                    index.ReplaceForRoot(P("R1"), i % 2 == 0 ? four : two);
            }
            catch (Exception ex) { errors.Add(ex); }
            finally { stop.Cancel(); }
        });

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var count = index.Count;
                    if (count != 5 && count != 7)
                        bad.Add(count);

                    index.TryGetFolder(P("R2", "f1"), out _);
                    index.FindInheritingAncestor(P("R2", "f1", "x.txt"));
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        WaitAll(readers.Append(writer).ToArray());

        Assert.Empty(errors);
        Assert.Empty(bad);
        Assert.True(index.TryGetFolder(P("R2", "f2"), out _)); // записи неизменявшегося корня целы
    }

    [Fact]
    public void Concurrent_writers_of_different_roots_do_not_lose_each_others_entries()
    {
        var index = new PersonalContentIndex();
        const int roots = 8;
        const int rounds = 300;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, roots).Select(r => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    // В каждом раунде корень заменяется целиком набором из r+1 записи.
                    var entries = Enumerable.Range(0, r + 1).Select(k => Info(P("Root" + r, "f" + k))).ToList();
                    index.ReplaceForRoot(P("Root" + r), entries);
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        WaitAll(writers);

        Assert.Empty(errors);
        Assert.Equal(Enumerable.Range(1, roots).Sum(), index.Count); // 1 + 2 + ... + 8 = 36
        for (var r = 0; r < roots; r++)
        {
            for (var k = 0; k <= r; k++)
                Assert.True(index.TryGetFolder(P("Root" + r, "f" + k), out _), $"нет записи Root{r}\\f{k}");
        }
    }

    [Fact]
    public void Readers_and_clear_running_together_do_not_throw()
    {
        var index = new PersonalContentIndex();
        var entries = Enumerable.Range(0, 20).Select(i => Info(P("R", "f" + i))).ToList();
        using var stop = new CancellationTokenSource();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 2000; i++)
                {
                    index.ReplaceForRoot(P("R"), entries);
                    if (i % 3 == 0)
                        index.Clear();
                }
            }
            catch (Exception ex) { errors.Add(ex); }
            finally { stop.Cancel(); }
        });

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var count = index.Count;
                    if (count != 0 && count != 20)
                        errors.Add(new InvalidOperationException("Неполный снимок: " + count));

                    index.TryGetFolder(P("R", "f7"), out _);
                    index.FindInheritingAncestor(P("R", "f7", "x"));
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        WaitAll(readers.Append(writer).ToArray());

        Assert.Empty(errors);
    }
}
