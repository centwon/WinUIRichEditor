using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace WinUIRichEditor.Tests;

/// <summary>Runs test bodies on a real WinUI UI thread, so control-level code can be tested at all.
/// <para>Everything in <c>Controls</c> needs the WinUI runtime: <see cref="Microsoft.UI.Xaml.DependencyProperty"/>
/// registration runs in a static constructor, and the editor's own constructor builds a
/// <c>CanvasVirtualControl</c> and a <c>ScrollViewer</c>. Constructing any of that off a dispatcher
/// thread throws, which is why this repo's automated coverage stopped at the model and formatter layers
/// and every control defect — toolbar focus, caret alignment, vertical movement — was caught by a human
/// driving the demo.</para>
/// <para>It turns out the runtime starts inside the test host: the test project already builds with
/// <c>UseWinUI</c> and <c>WindowsAppSDKSelfContained</c>, so the Windows App SDK binaries sit next to the
/// test assembly and need no bootstrapper. All that was missing was a thread with a dispatcher on it.</para>
/// </summary>
/// <remarks>
/// <para><b>One per process.</b> <see cref="Microsoft.UI.Xaml.Application.Start"/> may be called once and
/// then owns its thread's message loop forever, so the thread is created lazily and kept for the run.
/// Tests are marshalled onto it; they do NOT get a thread each.</para>
/// <para><b>Tests share it.</b> There is one Application and one dispatcher for every test that uses this,
/// so a test must not leave global state behind — see the static-state note on <see cref="Run(Action)"/>.</para>
/// </remarks>
internal static class UiThread
{
    private static readonly Lazy<DispatcherQueue> Queue = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    // How long a single marshalled body may take. Generous: the first one pays for XAML warm-up.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static DispatcherQueue Start()
    {
        var ready = new ManualResetEventSlim();
        DispatcherQueue? queue = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // The callback runs on the dispatcher this call creates, and Start() does not return
                // until the app exits — which is why this lives on its own thread and why the queue is
                // captured from inside rather than after.
                Microsoft.UI.Xaml.Application.Start(_ =>
                {
                    queue = DispatcherQueue.GetForCurrentThread();
                    ready.Set();
                });
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true, // never keep the test host alive
            Name = "WinUI test UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(Budget))
            throw new InvalidOperationException("The WinUI runtime did not start within the budget.");
        if (failure != null)
            throw new InvalidOperationException("The WinUI runtime failed to start.", failure);
        return queue ?? throw new InvalidOperationException("The WinUI runtime started without a dispatcher.");
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows whatever it threw, with the
    /// original stack, so a failure reads as if the test ran there directly.
    /// <para>⚠ The Application is shared by the whole run. A body that changes STATIC state — the
    /// toolbar's <c>FontSizes</c>/<c>Palette</c>, <c>RichEditorLocalization.Language</c>,
    /// <c>RichEditorDiagnostics.Fault</c> — must put it back, or it leaks into every later test.</para></summary>
    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ExceptionDispatchInfo? error = null;
        var done = new ManualResetEventSlim();

        if (!Queue.Value.TryEnqueue(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        }))
            throw new InvalidOperationException("The UI thread refused work (its dispatcher has shut down).");

        if (!done.Wait(Budget))
            throw new TimeoutException($"A UI-thread test body did not finish within {Budget.TotalSeconds:0}s.");
        error?.Throw();
    }

    /// <summary>As <see cref="Run(Action)"/>, returning the body's value.</summary>
    public static T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        T result = default!;
        Run(() => { result = body(); });
        return result;
    }

    // ---- the shared host window -------------------------------------------------------------------

    private static Microsoft.UI.Xaml.Window? _host;

    /// <summary>Puts <paramref name="content"/> into the shared host window and waits for it to load, so
    /// anything needing a real layout pass (caret geometry, hit-testing, anything that builds a
    /// CanvasTextLayout) can run.
    /// <para><b>The window is never closed.</b> Closing the last window ends the Application's message
    /// loop and tears the whole runtime down — the next test then gets a COMException from a plain
    /// <c>ContentControl</c> constructor, and every test after that is told the dispatcher has shut
    /// down. One window is created for the run and its Content is swapped instead. (The thread is a
    /// background thread, so the process still exits cleanly.)</para>
    /// <para>Measuring or arranging a control OUTSIDE a visual tree is not an alternative: it recurses
    /// until the process dies of a stack overflow.</para></summary>
    public static void Host(Microsoft.UI.Xaml.FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var loaded = new ManualResetEventSlim();

        Run(() =>
        {
            content.Loaded += OnLoaded;
            if (_host == null)
            {
                _host = new Microsoft.UI.Xaml.Window { Content = content };
                _host.Activate();
            }
            else
            {
                _host.Content = content;
            }
            void OnLoaded(object s, Microsoft.UI.Xaml.RoutedEventArgs e)
            {
                content.Loaded -= OnLoaded;
                loaded.Set();
            }
        });

        // Loaded is the signal, not the proof. Swapping the Content of a window that is already up does
        // not always raise it — the element can be in the tree by the time the handler is attached — and
        // waiting alone then burns the whole budget on an element that is, in fact, loaded. Poll IsLoaded
        // as the second opinion.
        var deadline = DateTime.UtcNow + Budget;
        while (DateTime.UtcNow < deadline)
        {
            if (loaded.Wait(TimeSpan.FromMilliseconds(50))) return;
            if (Run(() => content.IsLoaded)) return;
        }
        throw new TimeoutException("The hosted content never loaded.");
    }
}
