using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StickyAIusage.Services;
using StickyAIusage.Sharing;

namespace StickyAIusage.Mac.Services;

/// <summary>
/// The macOS side of sharing a card. The clipboard: the PNG goes to a temp file
/// and <c>osascript</c> sets the clipboard to it as PNG data (arguments, never
/// a shell; the path escaped for AppleScript). A machine that refuses that gets
/// the card saved to Pictures instead (<see cref="ShareFlow"/>). The save
/// dialog is the window's storage provider; links open with <c>/usr/bin/open</c>.
/// </summary>
internal sealed class MacShareService(Func<TopLevel?> topLevel) : IShareOutput
{
    private const string Osascript = "/usr/bin/osascript";
    private static readonly TimeSpan ScriptLimit = TimeSpan.FromSeconds(10);

    public async Task<bool> CopyImageAsync(byte[] png)
    {
        var path = Path.Combine(Path.GetTempPath(), "StickyAIusage-share-card.png");
        try
        {
            AtomicFile.WriteAllBytes(path, png);
            var start = new ProcessStartInfo(Osascript) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in ShareFlow.MacClipboardArguments(path))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            var error = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(ScriptLimit);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                DiagnosticLog.WriteInfo("Share: osascript didn't finish within 10 s; stopped it.");
                process.Kill();
                return false;
            }
            if (process.ExitCode == 0)
            {
                DiagnosticLog.WriteInfo($"Share: image copied ({png.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes).");
                return true;
            }
            DiagnosticLog.WriteInfo($"Share: osascript exited {process.ExitCode}: {(await error).Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacShareService.CopyImage", ex);
            return false;
        }
        finally
        {
            // The clipboard holds its own copy once osascript has read the file.
            try { File.Delete(path); } catch { /* best effort: TEMP is cleaned by macOS too */ }
        }
    }

    public async Task<string?> SaveImageAsync(byte[] png, string suggestedName)
    {
        try
        {
            if (topLevel()?.StorageProvider is not { CanSave: true } storage)
                return await SaveToPicturesAsync(png, suggestedName);
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the share card",
                SuggestedFileName = suggestedName,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"], AppleUniformTypeIdentifiers = ["public.png"] }],
                SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures)
            });
            if (file is null)
                return null;
            if (file.TryGetLocalPath() is { } local)
                return Write(local, png);
            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(png);
            DiagnosticLog.WriteInfo("Share: saved " + file.Name + ".");
            return file.Name;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacShareService.SaveImage", ex);
            return null;
        }
    }

    public Task<string?> SaveToPicturesAsync(byte[] png, string fileName) =>
        Task.FromResult(Write(Path.Combine(Pictures(), fileName), png));

    public Task<bool> OpenUrlAsync(string url)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(url);
            using var process = Process.Start(start);
            DiagnosticLog.WriteInfo("Share: opened " + new Uri(url).Host + ".");
            return Task.FromResult(process is not null);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("MacShareService.OpenUrl", ex);
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
            DiagnosticLog.WriteError("MacShareService.Save", ex);
            return null;
        }
    }

    private static string Pictures()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures")
            : pictures;
    }
}
