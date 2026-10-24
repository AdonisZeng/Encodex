using System.IO;
using System.Text;
using System.Windows;
using Encodex.Resources;
using Encodex.Services;

namespace Encodex;

/// <summary>Shows what a file's detected encoding decodes to and what the target
/// encoding would emit, so wrong detections surface before the conversion runs.</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(string filePath, string? detectedEncodingName, Encoding targetEncoding)
    {
        InitializeComponent();
        Title = string.Format(Res.Prev_Title, Path.GetFileName(filePath));
        Loaded += (_, _) => LoadPreview(filePath, detectedEncodingName, targetEncoding);
    }

    private void LoadPreview(string filePath, string? detectedEncodingName, Encoding targetEncoding)
    {
        FileNameText.Text = string.Format(Res.Prev_File, filePath);

        Encoding? sourceEncoding = EncodingDetector.ResolveName(detectedEncodingName);

        EncodingInfoText.Text = sourceEncoding == null
            ? string.Format(Res.Prev_UnknownEncoding, detectedEncodingName ?? Res.Prev_Unknown)
            : string.Format(Res.Prev_EncodingInfo, detectedEncodingName, targetEncoding.EncodingName);

        byte[] head;
        try
        {
            using var stream = File.OpenRead(filePath);
            head = new byte[(int)Math.Min(stream.Length, 8192)];
            int offset = 0;
            while (offset < head.Length)
            {
                int read = stream.Read(head, offset, head.Length - offset);
                if (read == 0)
                    break;
                offset += read;
            }
            if (offset < head.Length)
                Array.Resize(ref head, offset);
        }
        catch (Exception ex)
        {
            SourcePreview.Text = string.Format(Res.Prev_ReadFailed, ex.Message);
            TargetPreview.Text = "";
            return;
        }

        if (sourceEncoding == null)
        {
            SourcePreview.Text = Res.Prev_NoPreview;
            TargetPreview.Text = "";
            return;
        }

        // 预览必须与真实转换口径一致：转换端用 ExceptionFallback 严格解码，这里若继续
        // 宽容解码，非法字节会被替换成 '?' 显示为"看似正常"的乱码，用户会误判可以
        // 转换，实际却因解码失败被原样复制。
        var strictSource = (Encoding)sourceEncoding.Clone();
        strictSource.DecoderFallback = DecoderFallback.ExceptionFallback;

        // head 只读取文件前 8192 字节，末尾可能正好切断一个多字节字符。
        // GetString 会把这种截断也算作非法并抛异常；而 GetChars 传 flush: false
        // 会保留末尾未完成的序列、只对完整且非法的序列抛 DecoderFallbackException，
        // 从而既保持严格，又不误伤正常的截断边界。
        char[] chars = new char[head.Length + 1];
        int charCount;
        try
        {
            charCount = strictSource.GetDecoder()
                .GetChars(head, 0, head.Length, chars, 0, flush: false);
        }
        catch (DecoderFallbackException)
        {
            SourcePreview.Text = Res.Prev_DecodeFailed;
            TargetPreview.Text = "";
            return;
        }

        string decoded = new string(chars, 0, charCount);
        SourcePreview.Text = decoded;

        // Strict target: unrepresentable characters surface as a warning instead of
        // silently becoming replacement characters.
        var strict = (Encoding)targetEncoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            var bytes = strict.GetBytes(decoded);
            TargetPreview.Text = HexDump(bytes, 1024);
        }
        catch (EncoderFallbackException)
        {
            TargetPreview.Text = Res.Prev_Unrepresentable;
        }
    }

    private static string HexDump(byte[] bytes, int maxBytes)
    {
        var sb = new StringBuilder();
        int shown = Math.Min(bytes.Length, maxBytes);
        for (int i = 0; i < shown; i++)
        {
            sb.Append(bytes[i].ToString("X2")).Append(' ');
            if ((i + 1) % 16 == 0)
                sb.AppendLine();
        }
        if (bytes.Length > maxBytes)
            sb.AppendLine(string.Format(Res.Prev_Truncated, bytes.Length, maxBytes));
        return sb.ToString();
    }
}
