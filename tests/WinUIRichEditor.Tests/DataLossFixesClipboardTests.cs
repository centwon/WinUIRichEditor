using System;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

// Data-loss fixes added for 1.3.0 (2026-10-15) that go through the system clipboard.
[Collection(UiTests.Collection)]
public class DataLossFixesClipboardTests
{
    private const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;

    // A picture copied in one app lost its size and inline placement when pasted in another: once the data has left the
    // copying app (another process, or after Clipboard.Flush) Windows hands a private format back as a UTF-16 stream,
    // and only the in-process string form was read — the paste fell back to a re-encoded bitmap.
    [Fact]
    public void ThePicturesOwnFormats_AreReadAfterTheyLeftTheCopyingApp() => UiThread.RunAsync(async () =>
    {
        var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        dp.SetData("WinUIRichEditor.ImageMeta", "1;40;20");
        dp.SetData("WinUIRichEditor.Image", Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        Clipboard.SetContent(dp);
        Clipboard.Flush(); // the data now belongs to Windows, as it does when another app pastes
        var view = Clipboard.GetContent();

        var meta = await (Task<(bool Inline, double W, double H)?>)typeof(RichEditor)
            .GetMethod("TryGetImageMetaAsync", NPS)!.Invoke(null, new object[] { view })!;
        var bytes = await (Task<byte[]?>)typeof(RichEditor)
            .GetMethod("TryGetOwnImageBytesAsync", NPS)!.Invoke(null, new object[] { view })!;

        Assert.Equal((true, 40.0, 20.0), meta);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    });
}
