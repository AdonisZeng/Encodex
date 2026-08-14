using System.Text;

namespace Encodex.Models;

public class EncodingOption
{
    public string DisplayName { get; init; }
    public Encoding Encoding { get; init; }

    public EncodingOption(string displayName, Encoding encoding)
    {
        DisplayName = displayName;
        Encoding = encoding;
    }

    public static List<EncodingOption> GetDefaultEncodings()
    {
        // CodePagesEncodingProvider must be registered before calling this
        // (on .NET Framework the code pages are built in, but the call is harmless).
        return new List<EncodingOption>
        {
            new("UTF-8 (无 BOM)", new UTF8Encoding(false)),
            new("UTF-8 (带 BOM)", new UTF8Encoding(true)),
            new("UTF-16 LE", new UnicodeEncoding(false, false)),
            new("UTF-16 BE", new UnicodeEncoding(true, false)),
            new("UTF-32 LE", new UTF32Encoding(false, false)),
            new("UTF-32 BE", new UTF32Encoding(true, false)),
            new("GBK", Encoding.GetEncoding("GBK")),
            new("GB2312", Encoding.GetEncoding("GB2312")),
            new("GB18030", Encoding.GetEncoding("GB18030")),
            new("Big5", Encoding.GetEncoding("big5")),
            new("EUC-KR", Encoding.GetEncoding("euc-kr")),
            new("Shift-JIS", Encoding.GetEncoding("shift_jis")),
            new("ASCII", Encoding.ASCII),
            new("ISO-8859-1 (Latin-1)", Encoding.GetEncoding("ISO-8859-1")),
        };
    }
}
