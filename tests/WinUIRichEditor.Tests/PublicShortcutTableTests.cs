using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.UI.Xaml;
using Windows.UI.Text;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The shortcut table is public (upstream PR #49): a host building its own toolbar or menu could not
/// read the gestures the editor acts on, so it had to write "Ctrl+B" again somewhere the editor cannot see —
/// and the two drift the day a binding changes.
/// <para>What <see cref="ShortcutTableTests"/> does not already hold: the table hands out no way to edit
/// itself, every command has a hint, and pressing what the table advertises does what it says. (Upstream
/// also publishes <c>Gesture(id)</c> for Avalonia menus; a WinUI menu takes the hint as a string, so the port
/// has no counterpart.)</para></summary>
[Collection(UiTests.Collection)]
public class PublicShortcutTableTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static RichEditorShortcut Primary(RichEditorShortcutId id)
        => RichEditorShortcuts.All.First(s => s.Id == id);

    [Fact]
    public void TheTableHandsOutNoWayToEditIt()
    {
        var all = RichEditorShortcuts.All;

        // A host that could write here would be editing what the editor's own key handler matches against.
        Assert.IsNotType<RichEditorShortcut[]>(all);
        if (all is IList<RichEditorShortcut> writable)
        {
            Assert.True(writable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writable[0] = default);
        }
    }

    [Fact]
    public void EveryCommandHasAHint()
    {
        foreach (RichEditorShortcutId id in Enum.GetValues<RichEditorShortcutId>())
            Assert.False(string.IsNullOrEmpty(RichEditorShortcuts.Display(id)), $"{id} has no hint");
    }

    // The tie to behaviour: the chord the table advertises goes the way OnEditorKeyDown sends it (TryMatch,
    // then RunShortcut — a KeyRoutedEventArgs has no public constructor) and the command runs. Without this
    // the rest only proves the table is self-consistent — it could be self-consistent and wrong.
    [Theory]
    [InlineData(RichEditorShortcutId.Bold)]
    [InlineData(RichEditorShortcutId.Italic)]
    [InlineData(RichEditorShortcutId.AlignCenter)]
    [InlineData(RichEditorShortcutId.Heading1)]
    public void PressingWhatTheTableAdvertises_RunsTheCommand(RichEditorShortcutId id) => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "text" } } });
        var ed = new RichEditor { Document = doc };
        Press(ed, Primary(RichEditorShortcutId.SelectAll)); // select it all

        Press(ed, Primary(id));

        var p = ed.Document!.Blocks.OfType<Paragraph>().First();
        switch (id)
        {
            case RichEditorShortcutId.Bold:
                Assert.Equal(700, p.Inlines.OfType<Run>().First().FontWeight.Weight);
                break;
            case RichEditorShortcutId.Italic:
                Assert.Equal(FontStyle.Italic, p.Inlines.OfType<Run>().First().FontStyle);
                break;
            case RichEditorShortcutId.AlignCenter:
                Assert.Equal(TextAlignment.Center, p.TextAlignment);
                break;
            case RichEditorShortcutId.Heading1:
                Assert.Equal(1, p.HeadingLevel);
                break;
        }
    });

    private static void Press(RichEditor ed, RichEditorShortcut s)
    {
        Assert.True(RichEditorShortcuts.TryMatch(s.Ctrl, s.Shift, s.Alt, s.Key, out var id));
        try { typeof(RichEditor).GetMethod("RunShortcut", NP)!.Invoke(ed, new object[] { id }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }
}
