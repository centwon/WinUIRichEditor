using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Hyperlinks: typing one (auto-link), clicking one, and which ones may be launched at all.
/// <para><c>AutoLinkOnType</c>, <c>OpenLinkAtCaretAsync</c> and the click path had no test. Three defects
/// were measured before this file:</para>
/// <list type="bullet">
/// <item><b>Any scheme was launched.</b> A link from a pasted page or a received file went to the Windows
/// launcher whatever its scheme — <c>ms-msdt:</c>, <c>search-ms:</c>, <c>ms-settings:</c>, an application's
/// own protocol — on one Ctrl+click, or a plain click in a read-only viewer. Upstream already launched
/// http/https only.</item>
/// <item><b>A click was judged half a character off.</b> The decision asked about the run BEFORE the nearest
/// caret boundary, so the left half of a link's first character did not open it and the left half of the
/// character after it did. Upstream hit-tests the character.</item>
/// <item><b>Auto-link</b> never fired on the Tab key (four spaces are inserted, not a single whitespace), and
/// cut a balanced closing bracket off a URL — Wikipedia's <c>/wiki/Foo_(bar)</c> became <c>/wiki/Foo_(bar</c>.</item>
/// </list>
/// <para>The launcher itself is never called here: the decision it is gated on (<c>IsLaunchableLink</c>) and
/// the decision of what was clicked (<c>LinkAtPoint</c>) are what is tested. Auto-link tests ported from
/// upstream's <c>Round2BackportTests</c>.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlHyperlinkTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type T = typeof(RichEditor);

    private static object? Call(object target, string name, params object?[] args)
    {
        var m = (target as Type ?? target.GetType()).GetMethod(name, target is Type ? NS : NP)!;
        try { return m.Invoke(target is Type ? null : target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    // A one-paragraph editor with the caret after "x ".
    private static RichEditor Typing()
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "x " });
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        var ed = new RichEditor { Document = doc };
        var para = ed.Document!.Blocks.OfType<Paragraph>().First();
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(para, 2));
        return ed;
    }

    private static (string text, string? uri)[] Links(RichEditor ed)
        => ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>())
             .Where(r => !string.IsNullOrEmpty(r.NavigateUri)).Select(r => (r.Text!, r.NavigateUri)).ToArray();

    // ---- auto-link (the first three ported: upstream Round2BackportTests) ----------------------------

    [Fact]
    public void TypingASpaceAfterAUrl_LinksIt() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("http://example.com");
        ed.InsertText(" ");
        Assert.Equal(new[] { ("http://example.com", (string?)"http://example.com") }, Links(ed));
    });

    [Fact]
    public void TypingASpaceAfterWww_LinksItWithAnHttpsPrefix() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("www.example.com");
        ed.InsertText(" ");
        Assert.Equal(new[] { ("www.example.com", (string?)"https://www.example.com") }, Links(ed));
    });

    [Fact]
    public void AutoLinkOnTypeFalse_DoesNotLink() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.AutoLinkOnType = false;
        ed.InsertText("http://example.com");
        ed.InsertText(" ");
        Assert.Empty(Links(ed));
    });

    [Fact]
    public void Enter_LinksTheUrlBeforeIt() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("https://example.com");
        Call(ed, "InsertParagraphBreak", false);
        Assert.Equal("https://example.com", Assert.Single(Links(ed)).uri);
    });

    // The contract names space, tab and Enter. Tab inserts four spaces, which InsertText does not treat as a
    // typed whitespace, so the Tab key never linked.
    [Fact]
    public void TheTabKey_LinksTheUrlBeforeIt() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("https://example.com");
        Call(ed, "HandleTab", false);
        Assert.Equal("https://example.com", Assert.Single(Links(ed)).uri);
    });

    // Sentence punctuation after a URL is not part of it; a bracket that closes one INSIDE the URL is.
    [Theory]
    [InlineData("https://example.com.", "https://example.com")]
    [InlineData("https://example.com,", "https://example.com")]
    [InlineData("https://example.com!", "https://example.com")]
    [InlineData("(see https://example.com)", "https://example.com")]
    [InlineData("(see https://example.com).", "https://example.com")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar).", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("https://example.com/a?b=1#c", "https://example.com/a?b=1#c")]
    public void AutoLink_TakesTheUrl_WithoutTrailingPunctuation(string typed, string expected) => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText(typed);
        ed.InsertText(" ");
        var (text, uri) = Assert.Single(Links(ed));
        Assert.Equal(expected, uri);
        Assert.Equal(expected, text); // the linked text is the URL itself — no stray bracket either side
    });

    // A link the user set by hand is never overwritten by the auto-link.
    [Fact]
    public void AutoLink_LeavesAnAlreadyLinkedToken_Alone() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("https://example.com");
        var p = ed.Document!.Blocks.OfType<Paragraph>().First();
        foreach (var f in new[] { "_selStart" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, 2));
        ed.SetHyperlink("https://chosen.example/");
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, 2 + 19));

        ed.InsertText(" ");

        Assert.Equal("https://chosen.example/", Assert.Single(Links(ed)).uri);
    });

    // A single-label host is not a web address to link (as in Word).
    [Fact]
    public void AutoLink_DoesNotLinkASingleLabelHost() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("http://localhost:8080");
        ed.InsertText(" ");
        Assert.Empty(Links(ed));
    });

    // The link rides the space's undo step: one Ctrl+Z takes back the space and the link together.
    [Fact]
    public void OneUndo_TakesBackTheSpaceAndItsLinkTogether() => UiThread.Run(() =>
    {
        var ed = Typing();
        ed.InsertText("https://example.com");
        ed.InsertText(" ");

        ed.Undo();

        Assert.Equal("x https://example.com", ed.GetPlainText().Trim());
        Assert.Empty(Links(ed));
    });

    // ---- which links may be launched ----------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/a?b", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyWebLinks_AreLaunchable(string? url, bool launchable)
    {
        var args = new object?[] { url, null };
        bool got = (bool)typeof(RichEditor).GetMethod("IsLaunchableLink", NS)!.Invoke(null, args)!;
        Assert.Equal(launchable, got);
    }

    // The menu's "Open link" is disabled for a link it would refuse, rather than doing nothing when chosen.
    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("ms-settings:display", false)]
    public void TheOpenLinkMenuItem_IsEnabledOnlyForALaunchableLink(string url, bool enabled) => UiThread.Run(() =>
    {
        var ed = Typing();
        var menu = new MenuFlyout();

        Call(ed, "BuildLinkMenu", menu, url);

        var open = menu.Items.OfType<MenuFlyoutItem>().First(i => i.Text == RichEditorLocalization.GetString("OpenLink"));
        Assert.Equal(enabled, open.IsEnabled);
    });

    // ---- what a click is on (hosted: this needs a real layout) --------------------------------------

    private static readonly Lazy<RichEditor> Hosted = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // Loads "<before>LINK<after>" with LINK linked; returns the caret points at the link's two ends.
    private static ((double x, double y, double h) start, (double x, double y, double h) end) Layout(RichEditor ed, string before, string after)
    {
        var p = new Paragraph();
        if (before.Length > 0) p.Inlines.Add(new Run { Text = before });
        p.Inlines.Add(new Run { Text = "LINK", NavigateUri = "https://example.com/" });
        if (after.Length > 0) p.Inlines.Add(new Run { Text = after });
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        ed.Document = doc;
        Call(ed, "RelayoutToViewport");
        var para = ed.Document!.Blocks.OfType<Paragraph>().First();
        (double, double, double) At(int off)
        {
            var box = Call(ed, "CaretToDocPoint", new TextPointer(para, off))!;
            var ty = box.GetType();
            double F(string n) => (double)ty.GetField(n)!.GetValue(box)!;
            return (F("Item1"), F("Item2"), F("Item3"));
        }
        return (At(before.Length), At(before.Length + 4));
    }

    private static bool On(RichEditor ed, double x, double y) => (bool)Call(ed, "LinkAtPoint", new Point(x, y))!;

    // The measurement this fix answers: 1.5px inside the first character was "not a link", 1.5px past the
    // last one was "a link". Both edges, both sides.
    [Fact]
    public void AClick_IsOnTheLink_ExactlyWhereTheLinkIsDrawn()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var (s, e) = Layout(ed, "aaaa ", " bbbb");
            double sy = s.y + s.h / 2, ey = e.y + e.h / 2;

            Assert.False(On(ed, s.x - 1.5, sy), "left of the link's first character");
            Assert.True(On(ed, s.x + 1.5, sy), "inside the link's first character");
            Assert.True(On(ed, e.x - 1.5, ey), "inside the link's last character");
            Assert.False(On(ed, e.x + 1.5, ey), "right of the link's last character");
        });
    }

    // Past the end of a line that ENDS with the link, the pointer is over empty space — not the link.
    [Fact]
    public void PastTheEndOfALineEndingInALink_IsNotTheLink()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var (_, e) = Layout(ed, "aaaa ", "");
            Assert.True(On(ed, e.x - 1.5, e.y + e.h / 2));
            Assert.False(On(ed, e.x + 40, e.y + e.h / 2));
        });
    }

    // Left of a paragraph that STARTS with the link, likewise.
    [Fact]
    public void LeftOfAParagraphStartingWithALink_IsNotTheLink()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var (s, _) = Layout(ed, "", " bbbb");
            Assert.True(On(ed, s.x + 1.5, s.y + s.h / 2));
            Assert.False(On(ed, s.x - 3, s.y + s.h / 2));
        });
    }

    // A SOFT-WRAPPED line whose next line starts with a link. Past the end of the first line the pointer is
    // over empty space, but the nearest caret there is the wrap boundary — which is the link's first
    // character. The line-end checks in LinkRunAtPoint exist for exactly this; the tests above cannot tell
    // them apart from no checks at all (a paragraph end or start has nothing past it), which is why this
    // one builds a real wrap: the text before the link grows until the link begins the second line.
    [Fact]
    public void PastTheEndOfAWrappedLine_IsNotTheLinkThatStartsTheNextLine()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            for (int n = 1; n < 400; n++)
            {
                var (s, _) = Layout(ed, string.Concat(Enumerable.Repeat("ab ", n)), " tail");
                var first = CaretAt(ed, 0);
                if (s.y <= first.y + 1) continue; // the link still shares the first line

                Assert.True(On(ed, s.x + 1.5, s.y + s.h / 2), "the link itself, at the start of line 2");
                Assert.False(On(ed, 10_000, first.y + first.h / 2), "far right of line 1: empty space, not the link below");
                return;
            }
            Assert.Fail("the text never wrapped before the link — the case could not be built");
        });
    }

    // A link whose own text WRAPS ("click here" breaking between the words). Left of its second line's first
    // character — the paragraph's left margin — the caret is that line's start and the character before it
    // is the space INSIDE the link at the end of line 1; without the line-start check the margin read as
    // the link. (A falsification run left that check deletable with every other test green.)
    [Fact]
    public void LeftOfTheSecondLineOfAWrappedLink_IsNotTheLink()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = string.Concat(Enumerable.Repeat("word ", 200)).TrimEnd(), NavigateUri = "https://example.com/" });
            var doc = new FlowDocument();
            doc.Blocks.Add(p);
            ed.Document = doc;
            Call(ed, "RelayoutToViewport");

            var first = CaretAt(ed, 0);
            int lineStart = Enumerable.Range(1, 999).First(o => CaretAt(ed, o).y > first.y + 1);
            var c = CaretAt(ed, lineStart);

            Assert.True(On(ed, c.x + 1.5, c.y + c.h / 2), "the link's first character on line 2");
            Assert.False(On(ed, c.x - 3, c.y + c.h / 2), "left of line 2's text: the margin, not the link");
        });
    }

    // Below a link's line, in the space BETWEEN two paragraphs: the nearest caret is on the link's line, so
    // without the vertical check the gap under a link read as the link. (Below the LAST paragraph the
    // paragraph-end rule answers first, which is why the gap has to be between two.)
    [Fact]
    public void TheGapBelowALinksLine_IsNotTheLink()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var p = new Paragraph { MarginBottom = 24 };
            p.Inlines.Add(new Run { Text = "aaaa " });
            p.Inlines.Add(new Run { Text = "LINK", NavigateUri = "https://example.com/" });
            p.Inlines.Add(new Run { Text = " bbbb" });
            var next = new Paragraph();
            next.Inlines.Add(new Run { Text = "next paragraph" });
            var doc = new FlowDocument();
            doc.Blocks.Add(p);
            doc.Blocks.Add(next);
            ed.Document = doc;
            Call(ed, "RelayoutToViewport");

            var inside = CaretAt(ed, 7); // between the link's 2nd and 3rd characters
            double lineBottom = LineBottomAt(ed, 7);

            Assert.True(On(ed, inside.x, inside.y + inside.h / 2), "on the link");
            Assert.False(On(ed, inside.x, lineBottom + 6), "in the gap below the link's line");
        });
    }

    private static double LineBottomAt(RichEditor ed, int offset)
    {
        var para = ed.Document!.Blocks.OfType<Paragraph>().First();
        var box = Call(ed, "CaretToDocPoint", new TextPointer(para, offset))!;
        return (double)box.GetType().GetField("Item5")!.GetValue(box)!;
    }

    private static (double x, double y, double h) CaretAt(RichEditor ed, int offset)
    {
        var para = ed.Document!.Blocks.OfType<Paragraph>().First();
        var box = Call(ed, "CaretToDocPoint", new TextPointer(para, offset))!;
        var ty = box.GetType();
        double F(string n) => (double)ty.GetField(n)!.GetValue(box)!;
        return (F("Item1"), F("Item2"), F("Item3"));
    }
}
