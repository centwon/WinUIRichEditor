using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Every control-level test class belongs to this collection, so they run one at a time.
/// <para>They all share a single UI thread and a single host window — there is only one
/// <see cref="Microsoft.UI.Xaml.Application"/> per process — so running the classes in parallel buys
/// nothing and costs correctness: each class hosting its own control evicts whatever the other one had
/// in the window, and the evicted control stops laying out mid-test. That failed all seventeen caret
/// tests, deterministically, while every one of them passed when its class was run alone.</para>
/// <para>The model and formatter suites (including the fuzz) stay parallel — they touch none of this.</para>
/// </summary>
[CollectionDefinition(Collection)]
public class UiTests
{
    public const string Collection = "winui-ui-thread";
}
