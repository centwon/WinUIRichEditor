using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The toolbar's and the view's own settings are dependency properties (2026-09-19), so XAML can bind them.
/// They were plain CLR properties: a toolbar declared in XAML could not be bound to its editor, and a view's
/// status bar or file actions could not follow a view-model. Each test binds through a real Binding and checks
/// the effect the old setter had — a DP whose callback forgot the side effect would still hold the value.</summary>
[Collection(UiTests.Collection)]
public class BindablePropertyTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T? Field<T>(object o, string name) where T : class => o.GetType().GetField(name, NP)!.GetValue(o) as T;

    private static void Bind(FrameworkElement target, DependencyProperty dp, object source)
        => target.SetBinding(dp, new Binding { Source = source, Mode = BindingMode.OneWay });

    // Bound to an editor, the toolbar drives it — and re-bound to another, it follows the new one and lets go of
    // the old (the setter's hook/unhook, now in the property-changed callback).
    [Fact]
    public void Toolbar_Target_Binds_AndRebindingMovesTheHooks()
    {
        UiThread.Run(() =>
        {
            var a = new RichEditor();
            var b = new RichEditor();
            var tb = new RichEditorToolbar();
            // Through another element's dependency property, the shape a XAML {Binding ElementName=..., Path=...}
            // takes. (A Binding whose Source IS the editor, with no Path, yields null in WinUI — the engine, not
            // this property: the bool and enum bindings below take that form and work.)
            var holder = new Border { Tag = a };
            tb.SetBinding(RichEditorToolbar.TargetProperty, new Binding { Source = holder, Path = new PropertyPath("Tag"), Mode = BindingMode.OneWay });
            Assert.Same(a, tb.Target);

            // The toolbar follows its target: a read-only editor gets the view toolbar.
            bool ViewToolbar() => (bool)typeof(RichEditorToolbar).GetField("_builtReadOnly", NP)!.GetValue(tb)!;
            a.IsReadOnly = true;
            Assert.True(ViewToolbar());

            holder.Tag = b; // the binding follows the source
            Assert.Same(b, tb.Target);
            Assert.False(ViewToolbar()); // rebuilt for b, which is editable
            // And it let go of a. (Behaviourally invisible — Sync reads the CURRENT target — so the subscriber list
            // is checked: a toolbar still subscribed would be kept alive by the editor it no longer drives.)
            bool Listens(RichEditor ed) => typeof(RichEditor).GetField("StatusChanged", NP)!.GetValue(ed) is System.Delegate d
                && System.Array.Exists(d.GetInvocationList(), h => ReferenceEquals(h.Target, tb));
            Assert.False(Listens(a), "the toolbar still listens to the editor it was re-bound away from");
            Assert.True(Listens(b));
        });
    }

    // Each layout setting rebuilds the strip, as its setter did.
    [Fact]
    public void Toolbar_LayoutSettings_Bind_AndRebuildTheStrip()
    {
        UiThread.Run(() =>
        {
            var tb = new RichEditorToolbar { Target = new RichEditor() };
            Bind(tb, RichEditorToolbar.ToolbarLevelProperty, ToolbarLevel.Maximum);
            Assert.NotNull(Field<ComboBox>(tb, "_paper")); // Maximum shows the page controls
            Bind(tb, RichEditorToolbar.ShowPageControlsProperty, false);
            Assert.Null(Field<ComboBox>(tb, "_paper") is { Parent: not null } p ? p : null);
            Bind(tb, RichEditorToolbar.ShowFileActionsProperty, false);
            Assert.False(Field<Button>(tb, "_exportBtn") is { Parent: not null });
            Bind(tb, RichEditorToolbar.ToolbarLevelProperty, ToolbarLevel.Minimal);
            Assert.Equal(ToolbarLevel.Minimal, tb.ToolbarLevel);
        });
    }

    [Fact]
    public void View_OwnSettings_Bind()
    {
        UiThread.Run(() =>
        {
            var view = new RichEditorView();
            Bind(view, RichEditorView.ShowStatusBarProperty, false);
            Assert.False(view.ShowStatusBar);
            Assert.Equal(Visibility.Collapsed, Field<Border>(view, "_statusBar")!.Visibility);

            Bind(view, RichEditorView.ShowFileActionsProperty, false);
            Assert.False(view.Toolbar.ShowFileActions); // pushed to the toolbar

            Bind(view, RichEditorView.ShowBuiltInFindBarProperty, false);
            Assert.False(view.ShowBuiltInFindBar);
        });
    }
}
