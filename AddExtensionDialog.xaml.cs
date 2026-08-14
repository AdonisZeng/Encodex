using System.Windows;

namespace Encodex;

public partial class AddExtensionDialog : Window
{
    /// <summary>The raw text entered by the user (trimmed).</summary>
    public string ExtensionInput { get; private set; } = "";

    public AddExtensionDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ExtensionInputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ExtensionInput = ExtensionInputBox.Text?.Trim() ?? "";
        DialogResult = true;
    }
}
