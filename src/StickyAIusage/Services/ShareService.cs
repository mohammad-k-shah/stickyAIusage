using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using StickyAIusage.Sharing;

namespace StickyAIusage.Services;

/// <summary>The Windows side of sharing a card: the PNG, the clipboard (as an
/// image and as PNG data, which browsers paste without losing quality), the
/// save dialog and the browser.</summary>
internal sealed class ShareService(Func<Window?> owner) : IShareOutput
{
    /// <summary>The card drawn and encoded as PNG.</summary>
    public static byte[] Png(ShareCardModel model, bool dark) => Encode(ShareCardView.Render(model, dark));

    public static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public async Task<bool> CopyImageAsync(byte[] png)
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png), autoConvert: false);
        data.SetImage(Decode(png));
        // Another program may hold the clipboard for a moment (CLIPBRD_E_CANT_OPEN).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                DiagnosticLog.WriteInfo($"Share: image copied ({png.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes).");
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException && attempt < 5)
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError("ShareService.CopyImage", ex);
                return false;
            }
        }
    }

    public Task<string?> SaveImageAsync(byte[] png, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the share card",
            FileName = suggestedName,
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Pictures()
        };
        // Owned by the widget, so the dialog opens above it (the widget stays on top of other windows).
        var window = owner();
        if ((window is null ? dialog.ShowDialog() : dialog.ShowDialog(window)) != true)
            return Task.FromResult<string?>(null);
        return Task.FromResult(Write(dialog.FileName, png));
    }

    public Task<string?> SaveToPicturesAsync(byte[] png, string fileName) =>
        Task.FromResult(Write(Path.Combine(Pictures(), fileName), png));

    public Task<bool> OpenUrlAsync(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            DiagnosticLog.WriteInfo("Share: opened " + new Uri(url).Host + ".");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ShareService.OpenUrl", ex);
            return Task.FromResult(false);
        }
    }

    private static string? Write(string path, byte[] png)
    {
        try
        {
            AtomicFile.WriteAllBytes(path, png);
            DiagnosticLog.WriteInfo("Share: saved " + Path.GetFileName(path) + ".");
            return path;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ShareService.Save", ex);
            return null;
        }
    }

    private static string Pictures()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : pictures;
    }

    private static BitmapSource Decode(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}
