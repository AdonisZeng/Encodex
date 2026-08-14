using System.IO;
using System.Text;
using System.Windows;
using Encodex.Resources;

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

        Encoding? sourceEncoding = null;
        if (!string.IsNullOrEmpty(detectedEncodingName))
        {
            try { sourceEncoding = Encoding.GetEncoding(detectedEncodingName); }
            catch { }
        }

        EncodingInfoText.Text = sourceEncoding == null
            ? string.Format(Res.Prev_UnknownEncoding, detectedEncodingName ?? "未知")
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

        SourcePreview.Text = sourceEncoding.GetString(head);

        // Strict target: unrepresentable characters surface as a warning instead of
        // silently becoming replacement characters.
        var strict = (Encoding)targetEncoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            var bytes = strict.GetBytes(sourceEncoding.GetString(head));
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
