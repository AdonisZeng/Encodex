namespace Encodex.Models;

/// <summary>Outcome of validating a user-entered extension, so the UI can explain
/// exactly why the input was refused instead of a generic "invalid" message.</summary>
public enum ExtensionAddResult
{
    /// <summary>The extension was normalized and appended.</summary>
    Added,

    /// <summary>The extension is already in the list.</summary>
    Duplicate,

    /// <summary>The input cannot form an extension (empty, spaces, "..", ".c++").</summary>
    Invalid
}
