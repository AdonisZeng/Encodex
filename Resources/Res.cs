using System.Resources;

namespace Encodex.Resources;

/// <summary>Strongly typed accessor for Strings.resx. The default (and only shipped)
/// culture is Chinese; adding a language variant is a matter of dropping in a
/// Strings.&lt;culture&gt;.resx next to it.</summary>
public static class Res
{
    private static readonly ResourceManager Manager =
        new("Encodex.Resources.Strings", typeof(Res).Assembly);

    private static string Get(string key) => Manager.GetString(key) ?? key;

    // --- UI (XAML) ---
    public static string Ui_Title => Get("Ui_Title");
    public static string Ui_TabConfig => Get("Ui_TabConfig");
    public static string Ui_TabFiles => Get("Ui_TabFiles");
    public static string Ui_TabConvert => Get("Ui_TabConvert");
    public static string Ui_TabReport => Get("Ui_TabReport");
    public static string Ui_SourceFolder => Get("Ui_SourceFolder");
    public static string Ui_Browse => Get("Ui_Browse");
    public static string Ui_TargetEncoding => Get("Ui_TargetEncoding");
    public static string Ui_OverwriteInPlace => Get("Ui_OverwriteInPlace");
    public static string Ui_FileExtensions => Get("Ui_FileExtensions");
    public static string Ui_ExtensionsHint => Get("Ui_ExtensionsHint");
    public static string Ui_AddExtension => Get("Ui_AddExtension");
    public static string Ui_Scan => Get("Ui_Scan");
    public static string Ui_ScannedCount => Get("Ui_ScannedCount");
    public static string Ui_PreviewSelected => Get("Ui_PreviewSelected");
    public static string Ui_Convert => Get("Ui_Convert");
    public static string Ui_Select => Get("Ui_Select");
    public static string Ui_FilePath => Get("Ui_FilePath");
    public static string Ui_Size => Get("Ui_Size");
    public static string Ui_DetectedEncoding => Get("Ui_DetectedEncoding");
    public static string Ui_TargetEncodingCol => Get("Ui_TargetEncodingCol");
    public static string Ui_Status => Get("Ui_Status");
    public static string Ui_Details => Get("Ui_Details");
    public static string Ui_CancelConvert => Get("Ui_CancelConvert");
    public static string Ui_ReportTitle => Get("Ui_ReportTitle");
    public static string Ui_ReportHint => Get("Ui_ReportHint");
    public static string Ui_ReportFiles => Get("Ui_ReportFiles");
    public static string Ui_OutputDir => Get("Ui_OutputDir");
    public static string Ui_OpenFolder => Get("Ui_OpenFolder");
    public static string Ui_Rescan => Get("Ui_Rescan");
    public static string Ui_SelectNewFolder => Get("Ui_SelectNewFolder");
    public static string Ui_CheckUpdate => Get("Ui_CheckUpdate");
    public static string Ui_CheckUpdateTooltip => Get("Ui_CheckUpdateTooltip");
    public static string Ui_ThemeTooltip => Get("Ui_ThemeTooltip");
    public static string Ui_AddExtTitle => Get("Ui_AddExtTitle");
    public static string Ui_AddExtPrompt => Get("Ui_AddExtPrompt");
    public static string Ui_AddExtPlaceholder => Get("Ui_AddExtPlaceholder");
    public static string Ui_Ok => Get("Ui_Ok");
    public static string Ui_Cancel => Get("Ui_Cancel");

    // --- MainViewModel ---
    public static string VM_SelectFolder => Get("VM_SelectFolder");
    public static string VM_SelectValidFolder => Get("VM_SelectValidFolder");
    public static string VM_SelectExtension => Get("VM_SelectExtension");
    public static string VM_Scanning => Get("VM_Scanning");
    public static string VM_Scanned => Get("VM_Scanned");
    public static string VM_ScanFailed => Get("VM_ScanFailed");
    public static string VM_Converting => Get("VM_Converting");
    public static string VM_ConvertDone => Get("VM_ConvertDone");
    public static string VM_ConvertCancelled => Get("VM_ConvertCancelled");
    public static string VM_Selected => Get("VM_Selected");
    public static string VM_InvalidExtension => Get("VM_InvalidExtension");
    public static string VM_AddExtensionTitle => Get("VM_AddExtensionTitle");
    public static string VM_Binary => Get("VM_Binary");
    public static string VM_BackupDir => Get("VM_BackupDir");
    public static string VM_SuccessSection => Get("VM_SuccessSection");
    public static string VM_SkippedSection => Get("VM_SkippedSection");
    public static string VM_FailedSection => Get("VM_FailedSection");
    public static string VM_CopiedSection => Get("VM_CopiedSection");
    public static string VM_CopyFailed => Get("VM_CopyFailed");
    public static string VM_CheckingUpdate => Get("VM_CheckingUpdate");
    public static string VM_UpToDate => Get("VM_UpToDate");
    public static string VM_UpToDateBox => Get("VM_UpToDateBox");
    public static string VM_CheckUpdateTitle => Get("VM_CheckUpdateTitle");
    public static string VM_UpdateNotes => Get("VM_UpdateNotes");
    public static string VM_NewVersion => Get("VM_NewVersion");
    public static string VM_NewVersionTitle => Get("VM_NewVersionTitle");
    public static string VM_UpdateCancelled => Get("VM_UpdateCancelled");
    public static string VM_Downloading => Get("VM_Downloading");
    public static string VM_DownloadingPercent => Get("VM_DownloadingPercent");
    public static string VM_ReadyToUpdate => Get("VM_ReadyToUpdate");
    public static string VM_ReadyToUpdateTitle => Get("VM_ReadyToUpdateTitle");
    public static string VM_ApplyingUpdate => Get("VM_ApplyingUpdate");
    public static string VM_UpdateFailed => Get("VM_UpdateFailed");

    // --- EncodingConverter status messages ---
    public static string Conv_UnknownCopy => Get("Conv_UnknownCopy");
    public static string Conv_SameCopy => Get("Conv_SameCopy");
    public static string Conv_DecodeFailed => Get("Conv_DecodeFailed");
    public static string Conv_EncodeFailed => Get("Conv_EncodeFailed");

    // --- AppUpdateService ---
    public static string Upd_NetworkError => Get("Upd_NetworkError");
    public static string Upd_MissingVersion => Get("Upd_MissingVersion");
    public static string Upd_MissingUrl => Get("Upd_MissingUrl");
    public static string Upd_InvalidXml => Get("Upd_InvalidXml");
    public static string Upd_DownloadFailed => Get("Upd_DownloadFailed");
    public static string Upd_ChecksumFailed => Get("Upd_ChecksumFailed");
    public static string Upd_MissingUpdater => Get("Upd_MissingUpdater");
    public static string Upd_LaunchFailed => Get("Upd_LaunchFailed");
    public static string Upd_FailedUpdateLeftover => Get("Upd_FailedUpdateLeftover");
    public static string Upd_DialogTitle => Get("Upd_DialogTitle");

    // --- PreviewWindow ---
    public static string Prev_Title => Get("Prev_Title");
    public static string Prev_File => Get("Prev_File");
    public static string Prev_UnknownEncoding => Get("Prev_UnknownEncoding");
    public static string Prev_EncodingInfo => Get("Prev_EncodingInfo");
    public static string Prev_ReadFailed => Get("Prev_ReadFailed");
    public static string Prev_NoPreview => Get("Prev_NoPreview");
    public static string Prev_Unrepresentable => Get("Prev_Unrepresentable");
    public static string Prev_Truncated => Get("Prev_Truncated");

    // --- CliRunner ---
    public static string Cli_UnknownArg => Get("Cli_UnknownArg");
    public static string Cli_InvalidSrc => Get("Cli_InvalidSrc");
    public static string Cli_MissingTarget => Get("Cli_MissingTarget");
    public static string Cli_UnsupportedTarget => Get("Cli_UnsupportedTarget");
    public static string Cli_ConflictOut => Get("Cli_ConflictOut");
    public static string Cli_InvalidExt => Get("Cli_InvalidExt");
    public static string Cli_NoValidExt => Get("Cli_NoValidExt");
    public static string Cli_ScanSummary => Get("Cli_ScanSummary");
    public static string Cli_ConvertSummary => Get("Cli_ConvertSummary");
    public static string Cli_BackupDir => Get("Cli_BackupDir");
    public static string Cli_FailedFile => Get("Cli_FailedFile");
    public static string Cli_HelpTitle => Get("Cli_HelpTitle");
    public static string Cli_HelpUsage => Get("Cli_HelpUsage");
    public static string Cli_HelpRequired => Get("Cli_HelpRequired");
    public static string Cli_HelpSrc => Get("Cli_HelpSrc");
    public static string Cli_HelpTarget => Get("Cli_HelpTarget");
    public static string Cli_HelpTarget2 => Get("Cli_HelpTarget2");
    public static string Cli_HelpTarget3 => Get("Cli_HelpTarget3");
    public static string Cli_HelpOptions => Get("Cli_HelpOptions");
    public static string Cli_HelpOut => Get("Cli_HelpOut");
    public static string Cli_HelpOverwrite => Get("Cli_HelpOverwrite");
    public static string Cli_HelpExt => Get("Cli_HelpExt");
    public static string Cli_HelpHelp => Get("Cli_HelpHelp");
}
