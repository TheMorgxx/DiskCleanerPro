using DiskCleaner.Core.Services;
using Xunit;

namespace DiskCleaner.Tests;

/// <summary>Подписи групп «папок-пожирателей»: чем пересоздаются и что учесть перед удалением.</summary>
public sealed class BuildArtifactCatalogTests
{
    [Theory]
    [InlineData("Rust target", "cargo build")]
    [InlineData("Maven target", "mvn package")]
    [InlineData("node_modules", "npm install")]
    [InlineData("Pods", "pod install")]
    [InlineData("venv (Python)", "pip install -r requirements.txt")]
    [InlineData("Unity Library", "часы")]
    [InlineData("__pycache__", "создаётся заново")]
    [InlineData(".gradle", "gradle.properties")]
    [InlineData(".m2", "mvn install")]
    [InlineData(".cache", "пересоздаётся")]
    public void Known_group_has_its_own_description(string group, string expectedFragment)
    {
        var description = BuildArtifactCatalog.Describe(group);

        Assert.Contains(expectedFragment, description);
        Assert.False(description == BuildArtifactCatalog.FallbackDescription, $"Для «{group}» нет отдельной подписи");
    }

    [Fact]
    public void Descriptions_of_known_groups_are_distinct()
    {
        var groups = new[]
        {
            "Rust target", "Maven target", "node_modules", "Pods", "venv (Python)",
            "Unity Library", "__pycache__", ".gradle", ".m2", ".cache",
        };

        var descriptions = groups.Select(BuildArtifactCatalog.Describe).ToList();

        Assert.Equal(groups.Length, descriptions.Distinct().Count());
    }

    [Fact]
    public void Unknown_group_gets_the_fallback_description()
    {
        Assert.Equal(BuildArtifactCatalog.FallbackDescription, BuildArtifactCatalog.Describe("Какая-то новая группа"));
        Assert.Equal(BuildArtifactCatalog.FallbackDescription, BuildArtifactCatalog.Describe(""));
        Assert.Contains("пересоздаются", BuildArtifactCatalog.FallbackDescription);
    }

    [Fact]
    public void Lookup_ignores_case()
    {
        Assert.Equal(BuildArtifactCatalog.Describe("node_modules"), BuildArtifactCatalog.Describe("NODE_MODULES"));
        Assert.Equal(BuildArtifactCatalog.Describe("Rust target"), BuildArtifactCatalog.Describe("rust TARGET"));
    }

    [Fact]
    public void Unity_description_warns_about_long_reimport_and_gradle_about_properties()
    {
        Assert.Contains("переимпорт", BuildArtifactCatalog.Describe("Unity Library"));
        Assert.Contains("проверьте gradle.properties", BuildArtifactCatalog.Describe(".gradle"));
        Assert.Contains("не восстановятся", BuildArtifactCatalog.Describe(".m2"));
    }
}
