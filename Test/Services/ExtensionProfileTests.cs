using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class ExtensionProfileTests
{
    [Fact]
    public void Constructor_LoadsDefaultExtensions()
    {
        var profile = new ExtensionProfile();

        Assert.Contains(".cs", profile.Extensions);
        Assert.Contains(".java", profile.Extensions);
        Assert.Contains(".py", profile.Extensions);
        Assert.Contains(".md", profile.Extensions);
        Assert.True(profile.Extensions.Count >= 30);
    }

    [Fact]
    public void AddExtension_AddsNewExtension()
    {
        var profile = new ExtensionProfile();

        var result = profile.AddExtension(".xyz");

        Assert.True(result);
        Assert.Contains(".xyz", profile.Extensions);
    }

    [Fact]
    public void AddExtension_NormalizesInput()
    {
        var profile = new ExtensionProfile();

        profile.AddExtension("XYZ");

        Assert.Contains(".xyz", profile.Extensions);
    }

    [Fact]
    public void AddExtension_RejectsDuplicates()
    {
        var profile = new ExtensionProfile();

        var result = profile.AddExtension(".cs");

        Assert.False(result);
    }

    [Fact]
    public void RemoveExtension_RemovesExtension()
    {
        var profile = new ExtensionProfile();

        var result = profile.RemoveExtension(".cs");

        Assert.True(result);
        Assert.DoesNotContain(".cs", profile.Extensions);
    }

    [Fact]
    public void Matches_ReturnsTrueForMatchingFile()
    {
        var profile = new ExtensionProfile();

        Assert.True(profile.Matches("Program.cs"));
        Assert.True(profile.Matches("README.md"));
    }

    [Fact]
    public void Matches_ReturnsFalseForNonMatchingFile()
    {
        var profile = new ExtensionProfile();

        Assert.False(profile.Matches("image.png"));
        Assert.False(profile.Matches("binary.exe"));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOnBothSides()
    {
        var profile = new ExtensionProfile();
        profile.AddExtension(".xyz");

        Assert.True(profile.Matches("Readme.XYZ"));
        Assert.True(profile.Matches("readme.xyz"));
        Assert.False(profile.Matches("readme.xyz.bak"));
    }

    [Fact]
    public void RemoveExtension_IsCaseInsensitive()
    {
        var profile = new ExtensionProfile();

        Assert.True(profile.RemoveExtension(".CS"));
        Assert.False(profile.Matches("Program.cs"));
    }

    // ---- C1: TryNormalize is the shared pure validator ----

    [Theory]
    [InlineData("TXT", ".txt")]
    [InlineData(" txt ", ".txt")]
    [InlineData(".TXT", ".txt")]
    [InlineData("log", ".log")]
    public void TryNormalize_NormalizesInput(string input, string expected)
    {
        Assert.True(ExtensionProfile.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".my ext")]
    [InlineData("my ext")]
    [InlineData(".c++")]
    public void TryNormalize_RejectsInvalidInput(string input)
    {
        Assert.False(ExtensionProfile.TryNormalize(input, out var normalized));
        Assert.Equal("", normalized);
    }

    [Fact]
    public void TryNormalize_AcceptsSingleLetterExtension()
    {
        Assert.True(ExtensionProfile.TryNormalize(".c", out var normalized));
        Assert.Equal(".c", normalized);
    }

    [Fact]
    public void EmptyProfile_MatchesNothing_AndAcceptsAdditions()
    {
        // The snapshot profile used by scans starts empty and only takes the
        // extensions the user actually checked.
        var profile = new ExtensionProfile(loadDefaults: false);

        Assert.Empty(profile.Extensions);
        Assert.False(profile.Matches("Program.cs"));

        Assert.True(profile.AddExtension(".cs"));
        Assert.True(profile.Matches("Program.cs"));
    }

    [Fact]
    public void RemoveExtension_NotPresent_ReturnsFalse()
    {
        var profile = new ExtensionProfile();

        Assert.False(profile.RemoveExtension(".does-not-exist"));
    }

    [Fact]
    public void Matches_ExtensionlessFile_ReturnsFalse()
    {
        var profile = new ExtensionProfile();

        Assert.False(profile.Matches("Makefile"));
        Assert.False(profile.Matches("noextension"));
    }

    [Fact]
    public void GetDefaultGroups_ExtensionsAreUniqueAndComplete()
    {
        // A duplicated default would make AddExtension reject the second copy and
        // desync the grouped view from the flat extension list.
        var groups = ExtensionProfile.GetDefaultGroups();
        var all = groups.SelectMany(g => g.Extensions).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(all.Count, ExtensionProfile.GetDefaultExtensions().Length);
        Assert.All(groups, g => Assert.NotEmpty(g.Extensions));
    }

    [Fact]
    public void AddExtension_OutParameter_ReportsNormalizedForm()
    {
        var profile = new ExtensionProfile(loadDefaults: false);

        Assert.True(profile.AddExtension("LOG", out var normalized));
        Assert.Equal(".log", normalized);

        Assert.False(profile.AddExtension("c++", out var rejected));
        Assert.Equal("", rejected);
    }
}
