using System.IO;
using System.Text;
using Ude;

namespace Encodex.Services;

public class DetectionResult
{
    public Encoding? Encoding { get; init; }
    public string? EncodingName { get; init; }
    public bool IsBinary { get; init; }
}

public class EncodingDetector
{
    // Ude only needs a prefix of the file; BOM/null-byte checks look at the head too,
    // so there is no reason to load the whole file into memory just to detect encoding.
    private const int MaxDetectionBytes = 64 * 1024;

    public DetectionResult Detect(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(stream.Length, MaxDetectionBytes)];
        ReadExactly(stream, buffer);
        return Detect(buffer);
    }

    public DetectionResult Detect(byte[] bytes)
    {
        if (bytes.Length == 0)
            return new DetectionResult();

        // 1. BOM detection first. Check 4-byte BOMs (UTF-32) before 2-byte ones
        //    (UTF-16), because UTF-32LE starts with FF FE 00 00.
        var bomEncoding = DetectByBom(bytes);
        if (bomEncoding != null)
            return new DetectionResult { Encoding = bomEncoding, EncodingName = bomEncoding.WebName };

        // 2. Null-byte check: marks binaries, but BOM-less UTF-16 text legitimately
        //    contains 0x00 in every other byte, so try to tell the two apart first.
        if (ContainsNullBytes(bytes))
        {
            var utf16 = TryDetectUtf16WithoutBom(bytes);
            if (utf16 != null)
                return new DetectionResult { Encoding = utf16, EncodingName = utf16.WebName };
            return new DetectionResult { IsBinary = true };
        }

        // 3. Ude charset detection for BOM-less text (single/double-byte encodings).
        //    This runs after the null-byte check: Ude happily reports e.g. windows-1252
        //    for binary data, so binaries must be filtered out first.
        using var stream = new MemoryStream(bytes);
        var detector = new CharsetDetector();
        detector.Feed(stream);
        detector.DataEnd();

        if (!string.IsNullOrEmpty(detector.Charset))
        {
            try
            {
                var encoding = Encoding.GetEncoding(detector.Charset);
                return new DetectionResult { Encoding = encoding, EncodingName = detector.Charset };
            }
            catch
            {
                // Charset name not recognized by .NET
            }
        }

        return new DetectionResult();
    }

    /// <summary>.NET Framework has no Stream.ReadExactly: read until the buffer is full.</summary>
    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    private static Encoding? DetectByBom(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(false, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(true, true);
        return null;
    }

    private static bool ContainsNullBytes(byte[] bytes)
    {
        int checkLength = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < checkLength; i++)
        {
            if (bytes[i] == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Distinguishes BOM-less UTF-16 text from binary data that also contains null
    /// bytes. In UTF-16LE the null byte of an ASCII code unit sits on odd offsets,
    /// in UTF-16BE on even offsets; real binaries distribute null bytes evenly.
    /// Heuristic: most nulls must share one parity and the non-null bytes must look
    /// like printable text.
    /// </summary>
    private static Encoding? TryDetectUtf16WithoutBom(byte[] bytes)
    {
        int checkLength = Math.Min(bytes.Length, 8192);
        int nulls = 0, nullsOnOdd = 0, nonNullAscii = 0, nonNullTotal = 0;

        for (int i = 0; i < checkLength; i++)
        {
            if (bytes[i] == 0)
            {
                nulls++;
                if ((i & 1) == 1)
                    nullsOnOdd++;
            }
            else
            {
                nonNullTotal++;
                byte b = bytes[i];
                if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                    nonNullAscii++;
            }
        }

        // Pure-ASCII UTF-16 is exactly 50% null bytes; text mixed with CJK characters
        // (whose code units carry no null byte, e.g. U+4F60 = 60 4F in LE) still holds
        // a substantial share, so require at least 15%.
        if (nulls * 100 < checkLength * 15)
            return null;
        // The non-null half of an ASCII-based UTF-16 file is printable text.
        if (nonNullTotal == 0 || nonNullAscii * 10 < nonNullTotal * 8)
            return null;

        // In UTF-16LE the null byte of an ASCII code unit sits on odd offsets, in
        // UTF-16BE on even offsets. CJK code units like U+4E00 (00 4E in LE) put a
        // few nulls on the "wrong" parity, so 0.6 is a safer bar than 0.7.
        bool leLike = (double)nullsOnOdd / nulls >= 0.6;
        bool beLike = (double)(nulls - nullsOnOdd) / nulls >= 0.6;

        if (leLike)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        if (beLike)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        return null;
    }
}
