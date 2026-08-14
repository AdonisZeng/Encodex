using System.IO;
using Encodex.Updater;
using Xunit;

namespace Encodex.Tests.Updater;

public class UpdaterPathTraversalTests
{
    [Fact]
    public void ResolveEntryPath_NormalEntry_ResolvesInsideInstallDirectory()
    {
        var destination = Program.ResolveEntryPath("C:\\app", "sub\\file.dll");

        Assert.Equal(Path.GetFullPath("C:\\app\\sub\\file.dll"), destination);
    }

    [Fact]
    public void ResolveEntryPath_ForwardSlashEntry_ResolvesInside()
    {
        // Zip entries use '/' separators; Path.Combine normalizes them on Windows.
        var destination = Program.ResolveEntryPath("C:\\app", "sub/file.dll");

        Assert.Equal(Path.GetFullPath("C:\\app\\sub\\file.dll"), destination);
    }

    [Fact]
    public void ResolveEntryPath_DotDotTraversal_Throws()
    {
        Assert.Throws<IOException>(() => Program.ResolveEntryPath("C:\\app", "..\\..\\evil.dll"));
    }

    [Fact]
    public void ResolveEntryPath_AbsoluteEntry_Throws()
    {
        Assert.Throws<IOException>(() => Program.ResolveEntryPath("C:\\app", "C:\\Windows\\evil.dll"));
    }
}
