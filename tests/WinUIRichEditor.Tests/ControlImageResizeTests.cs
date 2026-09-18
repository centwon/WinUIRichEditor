using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;
using Grip = WinUIRichEditor.Controls.RichEditor.ResizeGrip;

namespace WinUIRichEditor.Tests;

/// <summary>Picture resize handles (2026-09-19): the corner keeps the proportions, the middle of the right
/// edge changes only the width, the middle of the bottom edge only the height. Before, the corner was the
/// only handle, so a picture's proportions could not be changed at all without a file.
/// <para>A press is <c>BeginImageResizeAt</c> (the handler minus its pointer capture), a move is
/// <c>TryResizeImage</c>, a release is <c>FinishImageResize</c> — as in ControlTableResizeTests.</para></summary>
[Collection(UiTests.Collection)]
public class ControlImageResizeTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void Hosted(Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            try { body(ed); }
            finally { ed.IsReadOnly = false; }
        });
    }

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        try { return T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static void Draw(RichEditor ed)
    {
        Call(ed, "RelayoutToViewport");
        using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 2000, 96);
        using var ds = rt.CreateDrawingSession();
        Call(ed, "DrawDocument", ds, new Rect(0, 0, 1000, 2000));
    }

    private static void Load(RichEditor ed, params Block[] blocks)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "top" } } });
        foreach (var b in blocks) doc.Blocks.Add(b);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "end" } } });
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        Draw(ed);
    }

    private static ImageBlock Picture(int w, int h)
    {
        var img = new ImageBlock { Width = w, Height = h };
        img.SetImageData(ControlImageDecodeTests.SolidBmp(w, h, 200, 40, 40), "image/bmp");
        return img;
    }

    // The rect the selected block picture was drawn at (top-level map or cell registry).
    private static Rect DrawnRect(RichEditor ed, ImageBlock img)
        => ((IEnumerable<Rect>)Call(ed, "BlockImageHandleRects", img)!).First();

    private static void Select(RichEditor ed, ImageBlock img)
    {
        T.GetField("_selectedBlock", NP)!.SetValue(ed, img);
        Draw(ed);
    }

    private static Point Handle(Rect r, Grip g) => g switch
    {
        Grip.Right => new Point(r.Right, r.Top + r.Height / 2),
        Grip.Bottom => new Point(r.Left + r.Width / 2, r.Bottom),
        _ => new Point(r.Right, r.Bottom),
    };

    private static void Drag(RichEditor ed, Point from, double dx, double dy)
    {
        Assert.True(ed.BeginImageResizeAt(from), $"no handle at {from}");
        Call(ed, "TryResizeImage", new Point(from.X + dx, from.Y + dy));
        Call(ed, "FinishImageResize");
    }

    // ---- which handle is where ----------------------------------------------------------------------

    [Fact]
    public void GripAt_FindsTheCornerAndTheTwoEdgeMiddles_CornerFirst()
    {
        var r = new Rect(10, 10, 200, 100);
        Assert.Equal(Grip.Corner, RichEditor.GripAt(r, new Point(210, 110)));
        Assert.Equal(Grip.Right, RichEditor.GripAt(r, new Point(212, 58)));
        Assert.Equal(Grip.Bottom, RichEditor.GripAt(r, new Point(113, 108)));
        Assert.Equal(Grip.None, RichEditor.GripAt(r, new Point(210, 12)));  // top-right: no handle there
        Assert.Equal(Grip.None, RichEditor.GripAt(r, new Point(12, 110)));  // bottom-left: no handle there
        Assert.Equal(Grip.None, RichEditor.GripAt(r, new Point(110, 60)));  // the middle is the picture (drag to move)
        // A small picture: all three grab areas overlap, and the corner — the handle that came first — wins.
        Assert.Equal(Grip.Corner, RichEditor.GripAt(new Rect(0, 0, 16, 16), new Point(16, 8)));
    }

    // ---- what each handle changes -------------------------------------------------------------------

    [Theory]
    [InlineData("Right", 60, 40, 260, 150)]   // width only; the vertical movement is ignored
    [InlineData("Bottom", 40, 50, 200, 200)]  // height only; the horizontal movement is ignored
    [InlineData("Corner", 100, -70, 300, 225)] // proportions kept (4:3), driven by the horizontal movement
    public void EachHandle_ChangesItsOwnSides(string handle, double dx, double dy, double w, double h)
    {
        Hosted(ed =>
        {
            Load(ed, Picture(200, 150));
            var img = ed.Document!.Blocks.OfType<ImageBlock>().Single();
            Select(ed, img);
            Drag(ed, Handle(DrawnRect(ed, img), Enum.Parse<Grip>(handle)), dx, dy);
            Assert.Equal((w, h), (img.Width, img.Height));

            // One drag, one undo step, back to where it started.
            ed.Undo();
            var back = ed.Document!.Blocks.OfType<ImageBlock>().Single();
            Assert.Equal((200.0, 150.0), (back.Width, back.Height));
            Assert.False(ed.CanUndo);
        });
    }

    [Fact]
    public void AnEdgeHandle_ClampsToTheMinimumSize()
    {
        Hosted(ed =>
        {
            Load(ed, Picture(200, 150));
            var img = ed.Document!.Blocks.OfType<ImageBlock>().Single();
            Select(ed, img);
            Drag(ed, Handle(DrawnRect(ed, img), Grip.Bottom), 0, -1000);
            Assert.Equal((200.0, 24.0), (img.Width, img.Height));
        });
    }

    // A cell draws a picture scaled down to its width, and the drag is measured from what is DRAWN (see
    // _cellImageRects). An edge handle there writes both sides from the drawn size — writing only the height
    // would leave the declared 400px width, which the cell scales down again, so the edge would move a
    // fraction of the pointer's distance.
    [Fact]
    public void InACell_TheBottomEdgeFollowsThePointer()
    {
        Hosted(ed =>
        {
            var tb = new TableBlock(1, 1);
            tb.ColumnWidths[0] = 150;
            tb.Cells[0][0].Blocks.Insert(0, Picture(400, 300));
            Load(ed, tb);
            var img = ed.Document!.Blocks.OfType<TableBlock>().Single().Cells[0][0].Blocks.OfType<ImageBlock>().Single();
            Select(ed, img);
            var before = DrawnRect(ed, img);
            Assert.True(before.Width < 400, $"the cell did not scale the picture down (drawn {before.Width})");

            Drag(ed, Handle(before, Grip.Bottom), 0, 30);
            Draw(ed);
            var after = DrawnRect(ed, img);
            Assert.Equal(before.Width, after.Width, 1);
            Assert.Equal(before.Height + 30, after.Height, 1);
        });
    }

    [Fact]
    public void AnInlinePicture_HasTheEdgeHandlesToo()
    {
        Hosted(ed =>
        {
            var inline = new InlineImage { Width = 40, Height = 30 };
            inline.SetImageData(ControlImageDecodeTests.SolidBmp(40, 30, 40, 200, 40), "image/bmp");
            Load(ed, new Paragraph { Inlines = { new Run { Text = "before " }, inline, new Run { Text = " after" } } });
            var p = ed.Document!.Blocks.OfType<Paragraph>().First(x => x.Inlines.OfType<InlineImage>().Any());
            var img = p.Inlines.OfType<InlineImage>().Single();
            T.GetField("_selectedInline", NP)!.SetValue(ed, (p, img));
            Draw(ed);
            var rects = (System.Collections.IDictionary)T.GetField("_inlineImageRects", NP)!.GetValue(ed)!;
            var rect = (Rect)rects[img]!.GetType().GetField("Item2")!.GetValue(rects[img])!;

            Drag(ed, Handle(rect, Grip.Right), 20, 0);
            Assert.Equal((60.0, 30.0), (img.Width, img.Height));
        });
    }

    [Fact]
    public void AViewer_HasNoHandles()
    {
        Hosted(ed =>
        {
            Load(ed, Picture(200, 150));
            var img = ed.Document!.Blocks.OfType<ImageBlock>().Single();
            Select(ed, img);
            ed.IsReadOnly = true;
            var r = DrawnRect(ed, img);
            foreach (var g in new[] { Grip.Corner, Grip.Right, Grip.Bottom })
                Assert.False(ed.BeginImageResizeAt(Handle(r, g)), $"{g} handle in a viewer");
        });
    }
}
