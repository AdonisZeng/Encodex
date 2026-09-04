using System.IO;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class PathHelperTests
{
    [Fact]
    public void GetRelativePath_FileInSubdirectory_ReturnsRelativePath()
    {
        var rel = PathHelper.GetRelativePath(@"C:\root", @"C:\root\sub\file.txt");

        Assert.Equal(Path.Combine("sub", "file.txt"), rel);
    }

    [Fact]
    public void GetRelativePath_SiblingDirectory_UsesDotDot()
    {
        var rel = PathHelper.GetRelativePath(@"C:\root\a", @"C:\root\b\file.txt");

        Assert.Equal(Path.Combine("..", "b", "file.txt"), rel);
    }

    [Fact]
    public void GetRelativePath_IsCaseInsensitive()
    {
        var rel = PathHelper.GetRelativePath(@"C:\ROOT", @"c:\root\sub\file.txt");

        Assert.Equal(Path.Combine("sub", "file.txt"), rel);
    }

    [Fact]
    public void GetRelativePath_TrailingSeparators_AreTolerated()
    {
        var rel = PathHelper.GetRelativePath(@"C:\root\", @"C:\root\sub\");

        Assert.Equal("sub", rel);
    }

    [Fact]
    public void GetRelativePath_SamePath_ReturnsEmpty()
    {
        Assert.Equal("", PathHelper.GetRelativePath(@"C:\root", @"C:\root"));
    }

    [Fact]
    public void GetRelativePath_DriveRoot_KeepsDriveLetterSegment()
    {
        // Split with RemoveEmptyEntries keeps "C:" as a segment; a TrimEnd-based
        // implementation would degrade "C:\" to "C" and misalign the comparison.
        var rel = PathHelper.GetRelativePath(@"C:\", @"C:\sub\file.txt");

        Assert.Equal(Path.Combine("sub", "file.txt"), rel);
    }
}
