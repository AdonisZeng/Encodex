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

/// <summary>
/// Detects the encoding of a text file from its head bytes. BOMs win first, then
/// BOM-less UTF-32/UTF-16 are recognised from their null-byte layout (only when
/// null bytes exist, so GBK/Big5 text still reaches Ude), and everything else is
/// handed to Ude's statistical detector.
/// </summary>
public class EncodingDetector
{
    // Ude only needs a prefix of the file; BOM/null-byte checks look at the head too,
    // so there is no reason to load the whole file into memory just to detect encoding.
    private const int MaxDetectionBytes = 64 * 1024;

    // BOM-less UTF-16/32 checks only inspect the head: the null-byte layout of a real
    // text file repeats consistently, and an 8 KB prefix gives the strict decoder
    // enough code units for a wrong byte order to produce an invalid surrogate.
    private const int BomlessSampleBytes = 8192;

    // Four UTF-32 code units are the minimum for both the offset histogram and the
    // text score to mean anything; shorter data falls through to the binary verdict.
    private const int MinUtf32SampleBytes = 16;

    // Absolute floor for the UTF-16 null-density gate: a 3-byte sample cannot reach
    // 1% with fewer than one zero byte, and a couple of stray NULs must never pass
    // for text.
    private const int MinUtf16NullBytes = 4;

    /// <summary>Detected UTF-8 display names, mirroring the target dropdown in
    /// EncodingOption so the two lists use the same wording.</summary>
    public const string Utf8NoBomName = "UTF-8 (无 BOM)";
    public const string Utf8BomName = "UTF-8 (带 BOM)";

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
        var bom = DetectByBom(bytes);
        if (bom != null)
            return new DetectionResult { Encoding = bom.Value.Encoding, EncodingName = bom.Value.Name };

        // 2. Null-byte check: marks binaries, but BOM-less UTF-16/32 text legitimately
        //    contains 0x00, so try to tell real text from binary first. UTF-32 must be
        //    attempted before UTF-16: UTF-32LE ASCII (41 00 00 00) also looks like a
        //    null-rich UTF-16LE stream and would otherwise be decoded as UTF-16, which
        //    silently produces NUL-ridden text. The gate itself stays: GBK/Big5 text
        //    carries no null bytes and must reach Ude.
        if (ContainsNullBytes(bytes))
        {
            var utf32 = TryDetectUtf32WithoutBom(bytes);
            if (utf32 != null)
                return new DetectionResult { Encoding = utf32.Value.Encoding, EncodingName = utf32.Value.Name };

            var utf16 = TryDetectUtf16WithoutBom(bytes);
            if (utf16 != null)
                return new DetectionResult { Encoding = utf16.Value.Encoding, EncodingName = utf16.Value.Name };

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
                // Keep a "no BOM" UTF-8 instance for the Ude result: GetEncoding("utf-8")
                // carries the BOM-emitting flag, which contradicts the display name the
                // file list shows and the target semantics the converter uses.
                bool isUtf8 = string.Equals(detector.Charset, "utf-8", StringComparison.OrdinalIgnoreCase);
                var encoding = isUtf8 ? (Encoding)new UTF8Encoding(false) : Encoding.GetEncoding(detector.Charset);
                var name = isUtf8 ? Utf8NoBomName : detector.Charset;
                return new DetectionResult { Encoding = encoding, EncodingName = name };
            }
            catch
            {
                // Charset name not recognized by .NET
            }
        }

        return new DetectionResult();
    }

    /// <summary>
    /// Resolves a detection/candidate encoding name (including the two BOM-aware
    /// UTF-8 display names) to an encoding; returns null when unrecognizable.
    /// </summary>
    public static Encoding? ResolveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (name == Utf8NoBomName)
            return new UTF8Encoding(false);
        if (name == Utf8BomName)
            return new UTF8Encoding(true);
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch
        {
            return null;
        }
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

    private static (Encoding Encoding, string Name)? DetectByBom(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), "utf-32BE");
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), "utf-32LE");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), Utf8BomName);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, true), "utf-16LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, true), "utf-16BE");
        return null;
    }

    private static bool ContainsNullBytes(byte[] bytes)
    {
        int checkLength = Math.Min(bytes.Length, BomlessSampleBytes);
        for (int i = 0; i < checkLength; i++)
        {
            if (bytes[i] == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detects BOM-less UTF-32 from the null-byte layout of its four byte lanes.
    /// In UTF-32LE every BMP code unit ends with two zero bytes (ASCII 41 00 00 00,
    /// CJK 60 4F 00 00), while lane 0 stays mostly non-zero; BE is the mirror image.
    /// The histogram alone is too weak for arbitrary binaries, so the sample is also
    /// decoded strictly and scored as text.
    /// </summary>
    private static (Encoding Encoding, string Name)? TryDetectUtf32WithoutBom(byte[] bytes)
    {
        int checkLength = Math.Min(bytes.Length, BomlessSampleBytes);
        checkLength -= checkLength % 4;
        if (checkLength < MinUtf32SampleBytes)
            return null;

        var sample = checkLength == bytes.Length ? bytes : TakePrefix(bytes, checkLength);
        int units = checkLength / 4;
        int nulls0 = 0, nulls1 = 0, nulls2 = 0, nulls3 = 0;
        for (int i = 0; i < checkLength; i += 4)
        {
            if (sample[i] == 0)
                nulls0++;
            if (sample[i + 1] == 0)
                nulls1++;
            if (sample[i + 2] == 0)
                nulls2++;
            if (sample[i + 3] == 0)
                nulls3++;
        }

        // 0.8 is calibrated on real files: ASCII units zero every lane but the first,
        // and CJK units zero lanes 2/3 for virtually every character, while lane 0 is
        // non-zero except for the few code points whose low byte is 0 (line breaks,
        // mostly) — hence the 0.2 ceiling there. Only the discriminating lanes are
        // bounded: lane 1 is zero for ASCII too, so it cannot be part of the LE test.
        bool leLike = (double)nulls2 / units >= 0.8 && (double)nulls3 / units >= 0.8
            && (double)nulls0 / units <= 0.2;
        bool beLike = (double)nulls0 / units >= 0.8 && (double)nulls1 / units >= 0.8
            && (double)nulls3 / units <= 0.2;

        if (leLike)
        {
            var strictLe = new UTF32Encoding(
                bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
            var score = TryStrictDecodeScore(sample, strictLe);
            if (score != null && IsPlausibleText(score.Value))
                return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), "utf-32LE");
        }

        if (beLike)
        {
            var strictBe = new UTF32Encoding(
                bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);
            var score = TryStrictDecodeScore(sample, strictBe);
            if (score != null && IsPlausibleText(score.Value))
                return (new UTF32Encoding(bigEndian: true, byteOrderMark: false), "utf-32BE");
        }

        return null;
    }

    /// <summary>
    /// Distinguishes BOM-less UTF-16 text from binary data that also contains null
    /// bytes. The old null-share heuristic failed on pure CJK text, whose code units
    /// carry no zero byte at all (U+4F60 = 60 4F), so detection now leans on a
    /// strict decode of both byte orders plus a text score. The strict decode is a
    /// strong signal by itself: a wrong byte order turns any character whose low
    /// byte lands in D8-DF into an unpaired surrogate and throws. When both orders
    /// survive (common for pure CJK, where swapped code units are still CJK), the
    /// text score and null parity decide, defaulting to LE (the Windows byte order).
    /// </summary>
    private static (Encoding Encoding, string Name)? TryDetectUtf16WithoutBom(byte[] bytes)
    {
        int checkLength = Math.Min(bytes.Length, BomlessSampleBytes);
        checkLength -= checkLength % 2;
        if (checkLength < 2)
            return null;

        var sample = checkLength == bytes.Length ? bytes : TakePrefix(bytes, checkLength);

        // A dense null population is the cheapest proof that these bytes are not
        // ordinary single-byte text with a stray NUL: real UTF-16 puts a zero byte
        // in every ASCII code unit (50% of bytes) and still 2-7% in CJK text with
        // line breaks, while a stray NUL in a text file is ~0.1%. Without this gate
        // a plain ASCII file containing one NUL byte passes the strict-decode score
        // (shifted byte pairs still look like text) and gets "converted" as UTF-16,
        // silently rewriting its bytes. Parity is deliberately not used as a second
        // gate: CJK content legitimately puts zeros on both parities (line breaks on
        // odd offsets, U+xx00 characters on even ones), so it would reject real text.
        int nullCount = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            if (sample[i] == 0)
                nullCount++;
        }
        if (nullCount < MinUtf16NullBytes || nullCount * 100 < sample.Length)
            return null;

        var strictLe = new UnicodeEncoding(
            bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        var strictBe = new UnicodeEncoding(
            bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
        var leScore = TryStrictDecodeScore(sample, strictLe);
        var beScore = TryStrictDecodeScore(sample, strictBe);

        bool leOk = leScore != null && IsPlausibleText(leScore.Value);
        bool beOk = beScore != null && IsPlausibleText(beScore.Value);
        if (!leOk && !beOk)
            return null;

        bool bigEndian;
        if (leOk && !beOk)
        {
            bigEndian = false;
        }
        else if (beOk && !leOk)
        {
            bigEndian = true;
        }
        else
        {
            bigEndian = ChooseBigEndian(leScore!.Value, beScore!.Value, sample);
        }

        return bigEndian
            ? (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), "utf-16BE")
            : (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), "utf-16LE");
    }

    /// <summary>
    /// Tie-breaker for the rare case where both byte orders decode to plausible
    /// text: fewer bad characters wins, then a higher good share, then null parity
    /// (LE puts the zeros of ASCII units on odd offsets). LE is the final fallback.
    /// </summary>
    private static bool ChooseBigEndian(
        (int Good, int Bad, int Total) leScore,
        (int Good, int Bad, int Total) beScore,
        byte[] sample)
    {
        if (leScore.Bad != beScore.Bad)
            return beScore.Bad < leScore.Bad;

        double leGoodRatio = (double)leScore.Good / leScore.Total;
        double beGoodRatio = (double)beScore.Good / beScore.Total;
        if (leGoodRatio != beGoodRatio)
            return beGoodRatio > leGoodRatio;

        int nullsOnOdd = 0, nullsOnEven = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            if (sample[i] != 0)
                continue;
            if ((i & 1) == 1)
                nullsOnOdd++;
            else
                nullsOnEven++;
        }
        return nullsOnEven > nullsOnOdd;
    }

    /// <summary>
    /// Strictly decodes the sample and scores the result; returns null when the
    /// decoder rejects the bytes (which is the main signal for a wrong byte order).
    /// </summary>
    private static (int Good, int Bad, int Total)? TryStrictDecodeScore(byte[] sample, Encoding encoding)
    {
        string text;
        try
        {
            text = encoding.GetString(sample);
        }
        catch
        {
            return null;
        }
        return ScoreText(text);
    }

    /// <summary>
    /// Text gate shared by the UTF-16/32 paths: at most 1% of the characters may be
    /// control characters, PUA or unpaired surrogates, and at least 80% must fall in
    /// normal writing-system ranges. 80% leaves room for punctuation variants and a
    /// few unsupported scripts while still rejecting decoded binary noise.
    /// </summary>
    private static bool IsPlausibleText((int Good, int Bad, int Total) score)
    {
        return score.Total > 0
            && score.Bad * 100 <= score.Total
            && score.Good * 10 >= score.Total * 8;
    }

    /// <summary>
    /// Classifies decoded characters as good / bad / neutral. Paired surrogates are
    /// handled before the per-char checks so emoji and astral characters count as
    /// one good character instead of two bad ones.
    /// </summary>
    private static (int Good, int Bad, int Total) ScoreText(string text)
    {
        int good = 0, bad = 0, total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                good++;
                total++;
                i++;
                continue;
            }

            total++;
            if (c == '\t' || c == '\n' || c == '\r' || IsGoodTextChar(c))
                good++;
            else if (char.IsSurrogate(c) || IsBadTextChar(c))
                bad++;
            // Remaining code points (rare scripts, symbols) are neutral: they neither
            // prove nor disprove that the bytes are text.
        }
        return (good, bad, total);
    }

    /// <summary>
    /// Ranges considered normal prose: Latin, Greek/Cyrillic, general punctuation,
    /// CJK symbols/kana, CJK Ext-A, CJK Unified, Hangul and full-width forms.
    /// </summary>
    private static bool IsGoodTextChar(char c)
    {
        return (c >= 0x20 && c <= 0x7E)
            || (c >= 0xA0 && c <= 0x24F)
            || (c >= 0x370 && c <= 0x4FF)
            || (c >= 0x2000 && c <= 0x206F)
            || (c >= 0x3000 && c <= 0x30FF)
            || (c >= 0x3400 && c <= 0x4DBF)
            || (c >= 0x4E00 && c <= 0x9FFF)
            || (c >= 0xAC00 && c <= 0xD7AF)
            || (c >= 0xFF00 && c <= 0xFFEF);
    }

    /// <summary>
    /// Control characters, private-use area and Unicode noncharacters. These are
    /// exactly the shapes that random bytes produce when decoded as UTF-16/32.
    /// </summary>
    private static bool IsBadTextChar(char c)
    {
        if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
            return true;
        if (c >= 0xE000 && c <= 0xF8FF)
            return true;
        if (c >= 0xFDD0 && c <= 0xFDEF)
            return true;
        return (c & 0xFFFE) == 0xFFFE;
    }

    private static byte[] TakePrefix(byte[] bytes, int length)
    {
        var sample = new byte[length];
        Array.Copy(bytes, sample, length);
        return sample;
    }
}
