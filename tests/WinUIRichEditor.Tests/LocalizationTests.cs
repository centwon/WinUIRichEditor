using System;
using WinUIRichEditor;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>RichEditorLocalization.Language is process-wide, and setting it raises LanguageChanged, which every
/// loaded toolbar answers by REBUILDING itself on the UI thread. These tests used to live in a parallel class
/// (ModelHelperTests), so while they ran, a control test elsewhere could build a menu in one language and look
/// its labels up in another, or have its toolbar replaced mid-test. They also left the process in English
/// whatever it started in. Measured 2026-09-19: not reproduced in 20 runs of the two classes together (the
/// window is milliseconds), so this is not proven to be the intermittent ControlContextMenuTests failure — it
/// is the one piece of global state a parallel test class wrote while the control tests read it.
/// <para>Now serialized with the control tests, and the language is restored, not set to "en".</para></summary>
[Collection(UiTests.Collection)]
public class LocalizationTests
{
    private static void WithLanguage(Action body)
    {
        string before = RichEditorLocalization.Language;
        try { body(); }
        finally { RichEditorLocalization.Language = before; }
    }

    [Fact]
    public void Localization_FallsBackToEnglishThenKey() => WithLanguage(() =>
    {
        RichEditorLocalization.Language = "en";
        Assert.Equal("Copy", RichEditorLocalization.GetString("Copy"));

        RichEditorLocalization.Language = "ko";
        Assert.Equal("복사", RichEditorLocalization.GetString("Copy"));

        // Unknown key returns the key itself.
        Assert.Equal("__nope__", RichEditorLocalization.GetString("__nope__"));

        // Unknown language falls back to English.
        RichEditorLocalization.Language = "zz";
        Assert.Equal("Copy", RichEditorLocalization.GetString("Copy"));
    });

    [Fact]
    public void Localization_RegisterAddsLanguage() => WithLanguage(() =>
    {
        RichEditorLocalization.Register("ja", new System.Collections.Generic.Dictionary<string, string> { ["Copy"] = "コピー" });
        RichEditorLocalization.Language = "ja";
        Assert.Equal("コピー", RichEditorLocalization.GetString("Copy"));
        // Missing key still falls back to English.
        Assert.Equal("Paste", RichEditorLocalization.GetString("Paste"));
    });

    // The guard for the move: nothing outside the control-test collection may write the language again.
    [Fact]
    public void OnlySerializedTestClasses_SetTheLanguage()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var t in typeof(LocalizationTests).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(CollectionAttribute), false).Length > 0) continue;
            string? path = SourceOf(t);
            if (path != null && System.IO.File.ReadAllText(path).Contains("RichEditorLocalization.Language =", StringComparison.Ordinal))
                offenders.Add(t.Name);
        }
        Assert.True(offenders.Count == 0, "parallel test classes that set the language: " + string.Join(", ", offenders));
    }

    // Test sources sit next to the project; a type's file is named after it by this project's convention.
    private static string? SourceOf(Type t)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++, dir = System.IO.Path.GetDirectoryName(dir))
        {
            var candidate = System.IO.Path.Combine(dir, t.Name + ".cs");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
