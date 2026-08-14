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
}
