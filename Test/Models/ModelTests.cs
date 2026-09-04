using Encodex.Models;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Models;

public class ReportSectionTests
{
    [Fact]
    public void Count_MatchesFileList()
    {
        var section = new ReportSection("✅", "成功转换", new[] { "a.txt", "b.txt" });

        Assert.Equal(2, section.Count);
        Assert.Equal(2, section.Files.Count);
    }

    [Fact]
    public void CountText_FormatsLocalizedSuffix()
    {
        var section = new ReportSection("✅", "成功转换", new[] { "a.txt", "b.txt" });

        Assert.Equal(string.Format(Encodex.Resources.Res.Ui_ReportFiles, 2), section.CountText);
    }

    [Fact]
    public void IsExpanded_DefaultsToFalse()
    {
        var section = new ReportSection("✅", "成功转换", Array.Empty<string>());

        Assert.False(section.IsExpanded);
    }
}

public class EncodingOptionTests
{
    [Fact]
    public void GetDefaultEncodings_CoversDocumentedSet()
    {
        // CLAUDE.md documents 14 target encodings; dropping one would silently
        // remove it from both the GUI dropdown and the CLI-like folder naming.
        var options = EncodingOption.GetDefaultEncodings();

        Assert.Equal(14, options.Count);
        Assert.All(options, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.DisplayName));
            Assert.NotNull(o.Encoding);
        });
    }

    [Fact]
    public void GetDefaultEncodings_DisplayNamesAreUnique()
    {
        // The settings store persists the DisplayName, so a collision would
        // restore the wrong encoding on the next launch.
        var names = EncodingOption.GetDefaultEncodings().Select(o => o.DisplayName).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void GetDefaultEncodings_BomVariantsOfUtf8_AreDistinct()
    {
        var options = EncodingOption.GetDefaultEncodings();
        var noBom = options.Single(o => o.DisplayName == EncodingDetector.Utf8NoBomName);
        var withBom = options.Single(o => o.DisplayName == EncodingDetector.Utf8BomName);

        Assert.Empty(noBom.Encoding.GetPreamble());
        Assert.Equal(3, withBom.Encoding.GetPreamble().Length);
    }
}

public class ExtensionOptionTests
{
    [Fact]
    public void NewOption_DefaultsToSelected()
    {
        var option = new ExtensionOption(".log");

        Assert.Equal(".log", option.Extension);
        Assert.True(option.IsSelected);
    }
}

public class FileConversionItemTests
{
    [Fact]
    public void NewItem_DefaultsToSelectedAndPending()
    {
        var item = new FileConversionItem();

        Assert.True(item.IsSelected);
        Assert.Equal(ConversionStatus.Pending, item.Status);
        Assert.Null(item.StatusMessage);
    }
}

public class ExtensionGroupTests
{
    [Fact]
    public void Group_HoldsNameAndOptions()
    {
        var options = new[] { new ExtensionOption(".cs"), new ExtensionOption(".java") };

        var group = new ExtensionGroup("编程语言", options);

        Assert.Equal("编程语言", group.Name);
        Assert.Equal(2, group.Extensions.Count);
    }
}
