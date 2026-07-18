using WinUIRichEditor;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

public class ModelHelperTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 0, 0x20, 0, 0, 0, 0x10 }, 0x20, 0x10)] // PNG 32x16
    public void ImageInfo_ReadsPngSize(byte[] header, int w, int h)
    {
        var (pw, ph) = ImageInfo.GetPixelSize(header);
        Assert.Equal(w, pw);
        Assert.Equal(h, ph);
    }

    [Fact]
    public void ImageInfo_ReadsGifSize()
    {
        // "GIF89a" + width(0x0040 LE) + height(0x0020 LE)
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x40, 0x00, 0x20, 0x00, 0, 0, 0 };
        var (w, h) = ImageInfo.GetPixelSize(gif);
        Assert.Equal(0x40, w);
        Assert.Equal(0x20, h);
    }

    [Fact]
    public void ImageInfo_UnknownReturnsZero()
    {
        var (w, h) = ImageInfo.GetPixelSize(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ListMarkers_BulletAndNumber()
    {
        Assert.Equal("•", ListMarkers.Text(ListKind.Bullet, ListMarkerStyle.Default, 0));
        Assert.Equal("1.", ListMarkers.Text(ListKind.Ordered, ListMarkerStyle.Default, 1));
        Assert.Equal("3.", ListMarkers.Text(ListKind.Ordered, ListMarkerStyle.Default, 3));
    }

    [Fact]
    public void Localization_FallsBackToEnglishThenKey()
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
        RichEditorLocalization.Language = "en";
    }

    [Fact]
    public void Localization_RegisterAddsLanguage()
    {
        RichEditorLocalization.Register("ja", new System.Collections.Generic.Dictionary<string, string> { ["Copy"] = "コピー" });
        RichEditorLocalization.Language = "ja";
        Assert.Equal("コピー", RichEditorLocalization.GetString("Copy"));
        // Missing key still falls back to English.
        Assert.Equal("Paste", RichEditorLocalization.GetString("Paste"));
        RichEditorLocalization.Language = "en";
    }
}
