using System;

namespace WinUIRichEditor.Controls;

// The Export / Import / Print file actions now live in RichEditorToolbar (so a standalone toolbar has them
// too). RichEditorView keeps the same public surface by forwarding straight to its embedded toolbar.
public partial class RichEditorView
{
    /// <summary>HWND of the owning window, required to show the file picker in an unpackaged app. Set this
    /// from the host (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>); Export/Import are inert
    /// until it is set. Forwards to <see cref="RichEditorToolbar.WindowHandle"/>.</summary>
    public nint WindowHandle
    {
        get => Toolbar.WindowHandle;
        set => Toolbar.WindowHandle = value;
    }

    /// <summary>Whether the built-in Export/Import (and Print, once <see cref="PrintRequested"/> is handled)
    /// buttons are shown in the toolbar. Default true. Forwards to <see cref="RichEditorToolbar.ShowFileActions"/>.</summary>
    public bool ShowFileActions
    {
        get => Toolbar.ShowFileActions;
        set => Toolbar.ShowFileActions = value;
    }

    /// <summary>Raised when the user clicks the built-in Print button. Printing is host-specific, so a host
    /// handles this to drive its own print/preview. The Print button stays hidden until a handler is
    /// attached. Forwards to <see cref="RichEditorToolbar.PrintRequested"/>.</summary>
    public event EventHandler? PrintRequested
    {
        add => Toolbar.PrintRequested += value;
        remove => Toolbar.PrintRequested -= value;
    }
}
