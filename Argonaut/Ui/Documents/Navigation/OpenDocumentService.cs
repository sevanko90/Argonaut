using System;

namespace Argonaut.Ui.Documents.Navigation;

/// <summary>Bytes to open as a document of their own - a value a hint decoded.</summary>
/// <param name="Bytes">The document's content.</param>
/// <param name="DisplayName">What the document is called, in place of a file name.</param>
/// <param name="Description">What is being opened, for the questions asked before replacing the
/// open document ("the decoded value").</param>
public readonly record struct OpenDocumentRequest(byte[] Bytes, string DisplayName, string Description);

/// <summary>
/// App-wide "open these bytes as a document" requests. A view raises one without a reference back
/// to the shell; MainWindow is the sole subscriber. Mirrors <see cref="ArrayTableService"/>.
/// </summary>
public static class OpenDocumentService
{
    public static event Action<OpenDocumentRequest>? Requested;

    public static void Request(OpenDocumentRequest request) => Requested?.Invoke(request);
}
