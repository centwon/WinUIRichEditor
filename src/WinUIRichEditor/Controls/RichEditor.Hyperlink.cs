using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: hyperlinks. A run's NavigateUri is rendered underlined (BuildTextLayout) and editable via a
// ContentDialog (context menu "Edit Link..."). Opening a link launches the system handler.
public partial class RichEditor
{
    /// <summary>Applies a hyperlink to the selection (or the caret word); pass null/empty to remove it.</summary>
    public void SetHyperlink(string? url)
    {
        var u = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        ApplyStyleToSelection(r => r.NavigateUri = u);
    }

    /// <summary>The hyperlink URL at the caret, or null if the caret is not on a linked run.</summary>
    public string? CurrentLinkUri()
    {
        if (_caret.Paragraph is not { } p) return null;
        return RunAtOffset(p, _caret.Offset > 0 ? _caret.Offset - 1 : 0)?.NavigateUri;
    }

    /// <summary>Opens a dialog to set/edit the hyperlink on the selection (or caret word). The dialog
    /// offers OK (apply), Remove (clear), and Cancel.</summary>
    public async Task EditHyperlinkAsync()
    {
        if (Document == null || IsReadOnly || XamlRoot == null) return;
        var box = new TextBox
        {
            Text = CurrentLinkUri() ?? "https://",
            AcceptsReturn = false,
            Width = 360,
            SelectionStart = 0,
        };
        box.SelectAll();
        var dlg = new ContentDialog
        {
            Title = RichEditorLocalization.GetString("Hyperlink"),
            Content = box,
            PrimaryButtonText = RichEditorLocalization.GetString("OK"),
            SecondaryButtonText = RichEditorLocalization.GetString("RemoveLink"),
            CloseButtonText = RichEditorLocalization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        // ShowAsync throws when another ContentDialog is already open — and every caller invokes this as
        // `_ = EditHyperlinkAsync()`, so the throw would die in an unobserved Task with nothing on screen
        // and nothing in the fault channel. Same fire-and-forget hole the toolbar's file actions had.
        ContentDialogResult result;
        try { result = await dlg.ShowAsync(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return; }
        if (result == ContentDialogResult.Primary) SetHyperlink(box.Text);
        else if (result == ContentDialogResult.Secondary) SetHyperlink(null);
    }

    /// <summary>Identifies the <see cref="AutoLinkOnType"/> dependency property.</summary>
    public static readonly DependencyProperty AutoLinkOnTypeProperty = DependencyProperty.Register(
        nameof(AutoLinkOnType), typeof(bool), typeof(RichEditor), new PropertyMetadata(true));

    /// <summary>When true (default), typing a space/tab/Enter right after a URL-looking token
    /// (http://, https://, www.) turns it into a hyperlink (Word/HWP behavior).
    /// <para>A dependency property, like every other behaviour flag (<see cref="IsReadOnly"/>,
    /// <c>Allow*</c>), so it can be bound and styled rather than only assigned in code.</para></summary>
    public bool AutoLinkOnType
    {
        get => (bool)GetValue(AutoLinkOnTypeProperty);
        set => SetValue(AutoLinkOnTypeProperty, value);
    }

    // Called after a whitespace commit at `boundary` (the offset just past the token). Applies
    // NavigateUri to the completed token when it parses as an http(s) URL; runs inside the same undo
    // group as the typing. "www." tokens link to "https://" + token. Trailing sentence punctuation is
    // excluded, an already-linked token is left alone (don't clobber a manually edited link).
    private void TryAutoLink(Paragraph p, int boundary)
    {
        string plain = BuildPlain(p);
        int end = Math.Clamp(boundary, 0, plain.Length);
        int start = end;
        while (start > 0 && !char.IsWhiteSpace(plain[start - 1]) && plain[start - 1] != '￼') start--;
        if (end - start < 8) return; // shortest sensible candidate ("http://x", "www.a.bc")
        string token = plain.Substring(start, end - start).TrimEnd('.', ',', ';', ':', ')', ']', '!', '?', '"', '\'');
        if (token.Length < 8) return;

        bool www = token.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        bool http = token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!www && !http) return;
        string url = www ? "https://" + token : token;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.Host.Contains('.')) return;
        if (RunAtOffset(p, start)?.NavigateUri is { Length: > 0 }) return;

        new Documents.TextRange(new Documents.TextPointer(p, start), new Documents.TextPointer(p, start + token.Length))
            .ApplyPropertyValue(r => r.NavigateUri = url);
    }

    /// <summary>Launches the hyperlink at the caret in the system browser, if any.</summary>
    public Task OpenLinkAtCaretAsync() => OpenUriAsync(CurrentLinkUri());

    // Launches a specific absolute URI. Callers that captured the link at press time (the read-only
    // plain-click path) pass it in rather than re-reading the caret, which may have moved since.
    internal async Task OpenUriAsync(string? url)
    {
        if (url is not { Length: > 0 }) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try { await Windows.System.Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        }
    }
}
