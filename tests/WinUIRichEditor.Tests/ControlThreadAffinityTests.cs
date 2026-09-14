using System;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>A brush is a DependencyObject: it belongs to the thread that made it. A WinUI host may give a window its
/// own UI thread, and an editor or toolbar there read brushes another thread made — the editor's three brush defaults
/// (one instance each, made in the static constructor) and the toolbar's six cached brushes — and got
/// RPC_E_WRONG_THREAD on its first paint (measured 2026-09-14 on a second XAML thread). Upstream fixed the same shape
/// with immutable brushes (a66b472); WinUI has none, so each editor makes its own defaults and the toolbar caches its
/// brushes per thread.
/// <para>As upstream does, the guard sweeps the two shapes that are shared process-wide instead of naming today's
/// brushes, so the next one is caught when it is written — not when a host first opens a second UI thread.</para></summary>
[Collection(UiTests.Collection)]
public class ControlThreadAffinityTests
{
    private static readonly Assembly Library = typeof(RichEditor).Assembly;

    // Every static field that can hold a brush is either [ThreadStatic] or none at all.
    [Fact]
    public void NoBrushIsCachedProcessWide()
    {
        var shared = Library.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => typeof(Brush).IsAssignableFrom(f.FieldType))
            .Where(f => f.GetCustomAttribute<ThreadStaticAttribute>() == null)
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .ToList();

        Assert.Empty(shared);
    }

    // The sweep's premise: it does see the fields it is meant to police (a sweep that finds no brush fields at all
    // would pass forever). The toolbar's per-thread caches are brush fields.
    [Fact]
    public void TheSweep_SeesTheToolbarsBrushFields()
    {
        var fields = typeof(RichEditorToolbar).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(f => typeof(Brush).IsAssignableFrom(f.FieldType)).ToList();
        Assert.True(fields.Count >= 6, $"found {fields.Count}");
        Assert.All(fields, f => Assert.NotNull(f.GetCustomAttribute<ThreadStaticAttribute>()));
    }

    // Every brush-valued property's DEFAULT is the editor's own: two fresh controls never hand out the same instance.
    [Fact]
    public void BrushDefaults_AreMadePerInstance() => UiThread.Run(() =>
    {
        object[] a = { new RichEditor(), new RichEditorToolbar(), new RichEditorView() };
        object[] b = { new RichEditor(), new RichEditorToolbar(), new RichEditorView() };
        var shared = a.Zip(b).SelectMany(pair => pair.First.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(p => typeof(Brush).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetValue(pair.First) is { } x && ReferenceEquals(x, p.GetValue(pair.Second)))
                .Select(p => $"{p.DeclaringType!.Name}.{p.Name}"))
            .ToList();

        Assert.Empty(shared);
        // The premise: the editor has brush properties with defaults for this to compare.
        Assert.NotNull(new RichEditor().TextForeground);
    });

    // The real thing: a second XAML thread — a window of its own — builds an editor and a toolbar after the first
    // thread already made one of each, reads the editor's brushes and syncs the toolbar. It threw RPC_E_WRONG_THREAD
    // at "read TextForeground" (editor) and "build a toolbar" (toolbar) before the fix.
    [Fact]
    public void AnEditorAndAToolbar_OnASecondUiThread_UseTheirOwnBrushes()
    {
        UiThread.Run(() =>
        {
            var first = new RichEditor();
            first.LoadHtml("<p><b>bold</b> list</p>");
            _ = new RichEditorToolbar { Target = first, ToolbarLevel = ToolbarLevel.Maximum };
            first.InsertText("x");
        });
        string result = "timed out";
        var t = new System.Threading.Thread(() =>
        {
            string step = "set up the thread";
            try
            {
                _ = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
                _ = Microsoft.UI.Xaml.Hosting.WindowsXamlManager.InitializeForCurrentThread();
                step = "construct";
                var ed = new RichEditor();
                step = "read the brushes";
                _ = ((SolidColorBrush)ed.TextForeground).Color;
                _ = ((SolidColorBrush)ed.CaretBrush).Color;
                _ = ((SolidColorBrush)ed.SelectionBrush).Color;
                step = "build a toolbar";
                ed.LoadHtml("<p><b>bold</b> list</p>");
                _ = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
                step = "sync the toolbar";
                ed.InsertText("x");
                result = "ok";
            }
            catch (Exception ex) { result = $"{step}: {ex.GetType().Name} 0x{ex.HResult:X8}"; }
        }) { IsBackground = true };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(60));

        Assert.Equal("ok", result);
    }

    // A host-supplied brush still wins over the per-editor default.
    [Fact]
    public void AHostBrush_StillWins() => UiThread.Run(() =>
    {
        var mine = new SolidColorBrush(Microsoft.UI.Colors.White);
        var ed = new RichEditor { TextForeground = mine };
        Assert.Same(mine, ed.TextForeground);
    });
}
