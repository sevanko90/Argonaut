using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Argonaut.Engine.Http;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Requests;

/// <summary>
/// The request pane's state: the method, the URL as the user typed it, and the URL's query
/// arguments as an editable list, kept in step both ways. Typing or pasting a URL replaces the
/// list; editing, adding or removing a row rewrites the URL's query - and only the argument
/// touched, since every other row keeps the text it had (<see cref="UrlQuery"/>). The URL is never
/// rewritten while the user is typing in it.
///
/// The start of the request manager: headers, folders and history grow from here, so it knows
/// nothing of the shell - sending and the body editor are handed to it. UI thread only.
/// </summary>
public sealed class RequestEditorViewModel : ObservableObject, IDisposable
{
    private readonly IRequestSender? sender;
    private HttpRequestMethod method = HttpRequestMethod.Get;
    private string urlText = string.Empty;
    private string? statusText;
    private bool isCollapsed;
    private bool isSending;

    /// <param name="sender">Sends what Send builds - the shell, which downloads and opens the
    /// response; null sends nothing (tests of the editing alone).</param>
    /// <param name="body">The POST body box; null gives a pane whose body is always empty.</param>
    public RequestEditorViewModel(IRequestSender? sender = null, RequestBodyEditor? body = null)
    {
        this.sender = sender;
        Body = body;
    }

    /// <summary>The hosted raw editor for a POST body, or null when the pane has none.</summary>
    public RequestBodyEditor? Body { get; }

    /// <summary>What the pane says under the URL: why a request cannot be sent, that one is on its
    /// way, or how old the response below is. Null for nothing to say.</summary>
    public string? StatusText
    {
        get => statusText;
        set => SetField(ref statusText, value);
    }

    /// <summary>Folded to its URL line, to give the response the height.</summary>
    public bool IsCollapsed
    {
        get => isCollapsed;
        set => SetField(ref isCollapsed, value);
    }

    /// <summary>A request is on its way; Send is unavailable until it lands or is stopped.</summary>
    public bool IsSending
    {
        get => isSending;
        private set
        {
            if (SetField(ref isSending, value))
                OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool CanSend => !isSending && sender is not null;

    /// <summary>
    /// Builds the request and hands it to the sender, or says why it cannot be built. What happens
    /// to the response - the progress, the document it opens as, a failure - is the sender's.
    /// </summary>
    public async Task SendAsync()
    {
        if (!CanSend)
            return;

        if (BuildRequest(HasBody ? Body?.ReadBody() : null, out string? problem) is not { } request)
        {
            StatusText = problem;
            return;
        }

        IsSending = true;
        StatusText = null;
        try
        {
            await sender!.SendAsync(request);
        }
        finally
        {
            IsSending = false;
        }
    }

    // Set while the rows are being rebuilt from the URL, so their own change notifications do not
    // write the URL back.
    private bool rebuildingRows;

    /// <summary>The methods the dropdown offers.</summary>
    public IReadOnlyList<HttpRequestMethod> Methods { get; } = [HttpRequestMethod.Get, HttpRequestMethod.Post];

    public HttpRequestMethod Method
    {
        get => method;
        set
        {
            if (!SetField(ref method, value))
                return;

            OnPropertyChanged(nameof(HasBody));
            OnPropertyChanged(nameof(MethodIndex));
        }
    }

    /// <summary>The dropdown's labels, in <see cref="Methods"/> order.</summary>
    public IReadOnlyList<string> MethodNames { get; } = ["GET", "POST"];

    /// <summary>Bound to the dropdown: <see cref="Method"/> as an index into <see cref="Methods"/>.</summary>
    public int MethodIndex
    {
        get => Methods.ToList().IndexOf(method);
        set
        {
            if (value >= 0 && value < Methods.Count)
                Method = Methods[value];
        }
    }

    /// <summary>Whether the method sends a body, so the body editor is shown.</summary>
    public bool HasBody => method == HttpRequestMethod.Post;

    /// <summary>The URL as typed. Setting it replaces <see cref="Arguments"/>.</summary>
    public string UrlText
    {
        get => urlText;
        set
        {
            if (!SetField(ref urlText, value ?? string.Empty))
                return;

            RebuildRows();
        }
    }

    /// <summary>The URL's query arguments, in order, plus any row added and not yet filled in.</summary>
    public ObservableCollection<QueryArgumentRowViewModel> Arguments { get; } = [];

    /// <summary>Adds an empty row at the end; the URL changes once it has a key or a value.</summary>
    public QueryArgumentRowViewModel AddArgument()
    {
        var row = new QueryArgumentRowViewModel(null, OnRowChanged);
        Arguments.Add(row);
        return row;
    }

    public void RemoveArgument(QueryArgumentRowViewModel row)
    {
        if (Arguments.Remove(row))
            WriteUrlFromRows();
    }

    /// <summary>Fills the pane from a request - a saved one being reopened - body included.</summary>
    public Task LoadAsync(HttpRequestSpec request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Method = request.Method;
        UrlText = request.Url.OriginalString;
        return Body?.LoadAsync(request.Body ?? []) ?? Task.CompletedTask;
    }

    public void Dispose() => Body?.Dispose();

    /// <summary>The request to send, with <paramref name="body"/> for a POST, or null and a
    /// sentence in <paramref name="problem"/> saying why there is none.</summary>
    public HttpRequestSpec? BuildRequest(byte[]? body, out string? problem)
        => HttpRequestSpec.TryCreate(method, urlText, body, out var request, out problem) ? request : null;

    private void RebuildRows()
    {
        rebuildingRows = true;
        try
        {
            Arguments.Clear();
            foreach (var part in UrlQuery.Parse(urlText).Parts)
                Arguments.Add(new QueryArgumentRowViewModel(part, OnRowChanged));
        }
        finally
        {
            rebuildingRows = false;
        }
    }

    private void OnRowChanged(QueryArgumentRowViewModel row)
    {
        if (!rebuildingRows)
            WriteUrlFromRows();
    }

    /// <summary>Writes the rows back into the URL's query, leaving its base and fragment as typed.</summary>
    private void WriteUrlFromRows()
    {
        // Set on the field, not through UrlText: the rows already say what the URL now holds, and
        // re-parsing would rebuild them under the row being edited.
        string written = UrlQuery.Parse(urlText).WithParts(Arguments.Where(static r => !r.IsEmpty).Select(static r => r.Part)).ToString();
        SetField(ref urlText, written, nameof(UrlText));
    }
}
