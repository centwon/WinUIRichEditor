using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The Native AOT line-metrics fallback (<c>LineMetricsFromRegions</c>) against the native
/// <c>CanvasTextLayout.LineMetrics</c> it stands in for.
/// <para>Under AOT <c>LineMetrics</c> throws (a <c>bool</c> in <c>CanvasLineMetrics</c> makes the array
/// non-blittable) and every caller silently gets the fallback instead. The test suite runs on JIT, where
/// the native path always works — so without this class the fallback is the one piece of line math that
/// NO test ever executes, and it is what AOT users get for caret movement, Home/End and pagination.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class LineMetricsFallbackTests
{
    private const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo FromRegions = typeof(RichEditor).GetMethod("LineMetricsFromRegions", NS)!;

    private readonly record struct Line(double Height, int Chars, double Baseline);

    private static Line[] Native(CanvasTextLayout layout)
        => layout.LineMetrics.Select(l => new Line(l.Height, l.CharacterCount, l.Baseline)).ToArray();

    private static Line[] Fallback(CanvasTextLayout layout, int textLength)
    {
        var arr = (Array)FromRegions.Invoke(null, new object?[] { layout, textLength })!;
        var result = new Line[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            object o = arr.GetValue(i)!;
            var t = o.GetType();
            result[i] = new Line((double)t.GetProperty("Height")!.GetValue(o)!,
                                 (int)t.GetProperty("CharacterCount")!.GetValue(o)!,
                                 (double)t.GetProperty("Baseline")!.GetValue(o)!);
        }
        return result;
    }

    private static int Len(Paragraph p) => p.Inlines.Sum(i => i is Run r ? (r.Text?.Length ?? 0) : 1);

    private static Paragraph Para(params Inline[] inlines)
    {
        var p = new Paragraph();
        foreach (var i in inlines) p.Inlines.Add(i);
        return p;
    }

    private const string Wrapping = "The quick brown fox jumps over the lazy dog and keeps running well past the edge of the line. ";

    private static IEnumerable<(string name, Paragraph p)> Cases()
    {
        foreach (double sp in new[] { double.NaN, 1.0, 1.6, 2.0, 3.0 })
        {
            string s = double.IsNaN(sp) ? "default" : $"{sp * 100:0}%";
            Paragraph With(Paragraph p) { p.LineSpacing = sp; return p; }
            yield return ($"wrap {s}", With(Para(new Run { Text = Wrapping + Wrapping })));
            yield return ($"mixed sizes {s}", With(Para(new Run { Text = "small text " + Wrapping }, new Run { Text = "BIG ", FontSize = 24 }, new Run { Text = Wrapping })));
            yield return ($"soft break {s}", With(Para(new Run { Text = "one\ntwo\nthree" })));
            yield return ($"double soft break {s}", With(Para(new Run { Text = "one\n\nthree" })));
            yield return ($"trailing soft break {s}", With(Para(new Run { Text = "one\n" })));
            yield return ($"only soft break {s}", With(Para(new Run { Text = "\n" })));
            yield return ($"leading soft break {s}", With(Para(new Run { Text = "\none" })));
            yield return ($"empty {s}", With(Para()));
            yield return ($"korean {s}", With(Para(new Run { Text = "다람쥐 헌 쳇바퀴에 타고파. 키스의 고유조건은 입술끼리 만나야 하고 특별한 기술은 필요치 않다. 다람쥐 헌 쳇바퀴에 타고파." })));
            yield return ($"tab {s}", With(Para(new Run { Text = "a\tb\tc" })));
            yield return ($"trailing spaces {s}", With(Para(new Run { Text = Wrapping + "          " })));
            yield return ($"small image {s}", With(Para(new Run { Text = "before " }, new InlineImage { Width = 20, Height = 10 }, new Run { Text = " after " + Wrapping })));
            yield return ($"tall image {s}", With(Para(new Run { Text = "before " }, new InlineImage { Width = 40, Height = 120 }, new Run { Text = " after " + Wrapping })));
            yield return ($"image only {s}", With(Para(new InlineImage { Width = 40, Height = 40 })));
            yield return ($"big font then soft break {s}", With(Para(new Run { Text = "tiny " }, new Run { Text = "BIG\n", FontSize = 24 })));
            yield return ($"tall image then soft break {s}", With(Para(new Run { Text = "x " }, new InlineImage { Width = 40, Height = 120 }, new Run { Text = "\n" })));
            yield return ($"wrap then soft break {s}", With(Para(new Run { Text = Wrapping + "\n" })));
        }
        var fixedH = Para(new Run { Text = Wrapping + Wrapping });
        fixedH.LineHeight = 40;
        yield return ("fixed LineHeight 40", fixedH);
        var h1 = Para(new Run { Text = Wrapping });
        h1.HeadingLevel = 1;
        yield return ("heading 1", h1);
    }

    // The oracle is the native path itself, on the same layout: line count, per-line character count
    // and height must agree, and so must the baseline wherever the fallback claims to know it (uniform
    // spacing). Measured before the fix: every paragraph ending in '\n' lost its last (empty) line, at
    // every spacing; heights were already exact.
    [Fact]
    public void Fallback_MatchesNativeLineMetrics()
    {
        var failures = new List<string>();
        int uniformBaselines = 0;
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            foreach (var (name, p) in Cases())
            {
                foreach (double width in new[] { 120.0, 300.0 })
                {
                    var layout = ed.BuildTextLayout(p, width);
                    var n = Native(layout);
                    var f = Fallback(layout, Len(p));
                    bool uniform = layout.LineSpacingMode == CanvasLineSpacingMode.Uniform;
                    bool same = n.Length == f.Length && n.Zip(f).All(z =>
                        z.First.Chars == z.Second.Chars
                        && Math.Abs(z.First.Height - z.Second.Height) < 0.75
                        && (uniform ? Math.Abs(z.First.Baseline - z.Second.Baseline) < 0.01 : double.IsNaN(z.Second.Baseline)));
                    if (uniform && f.Length > 0) uniformBaselines++;
                    if (!same)
                        failures.Add($"{name} @{width}: native [{string.Join(" | ", n.Select(l => $"{l.Chars}c {l.Height:0.##}h b{l.Baseline:0.##}"))}]"
                                     + $" fallback [{string.Join(" | ", f.Select(l => $"{l.Chars}c {l.Height:0.##}h b{l.Baseline:0.##}"))}]");
                }
            }
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        // Not vacuous: the baseline branch was exercised, not just the NaN one.
        Assert.True(uniformBaselines > 50, $"only {uniformBaselines} uniform layouts checked");
    }
}
