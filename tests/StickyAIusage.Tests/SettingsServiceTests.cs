using System.Text.Json.Nodes;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>The settings file: old formats load, newer keys survive, a broken
/// file falls back to the backup, and a save never leaves a partial file.</summary>
public class SettingsServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string SettingsPath => _dir.Combine("settings.json");
    private string BackupPath => SettingsService.BackupPath(SettingsPath);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_v1_file_without_a_version_loads_and_is_migrated()
    {
        // What v1.0.0 writes: no SchemaVersion key.
        File.WriteAllText(SettingsPath, """
            { "ShowMonthlySpend": false, "DayTargetUsd": 25, "Theme": "light", "IsRunning": false, "RedThresholdPercent": 90 }
            """);

        var settings = SettingsService.Load(SettingsPath);

        Assert.False(settings.ShowMonthlySpend);
        Assert.Equal(25m, settings.DayTargetUsd);
        Assert.Equal("light", settings.Theme);
        Assert.False(settings.IsRunning);
        Assert.Equal(90, settings.RedThresholdPercent);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.UnknownKeys);
    }

    [Fact]
    public void Fresh_settings_are_the_current_version()
    {
        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.ShowMonthlySpend);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Keys_from_a_newer_version_survive_a_save_by_this_one()
    {
        File.WriteAllText(SettingsPath, """
            { "SchemaVersion": 7, "Theme": "light", "FutureFeature": { "Enabled": true, "Level": 3 }, "FutureList": [1, 2] }
            """);

        var settings = SettingsService.Load(SettingsPath);
        settings.Theme = "dark";
        SettingsService.Save(settings, SettingsPath);

        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("dark", saved["Theme"]!.GetValue<string>());
        Assert.True(saved["FutureFeature"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(3, saved["FutureFeature"]!["Level"]!.GetValue<int>());
        Assert.Equal(2, saved["FutureList"]!.AsArray().Count);
        // The newer build's own version is kept, so it doesn't migrate twice.
        Assert.Equal(7, saved["SchemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_saved_file_round_trips()
    {
        var settings = new AppSettings { DayTargetUsd = 12.5m, WidgetRight = 100, CardFields = "model|in,out" };
        SettingsService.Save(settings, SettingsPath);

        var loaded = SettingsService.Load(SettingsPath);

        Assert.Equal(12.5m, loaded.DayTargetUsd);
        Assert.Equal(100, loaded.WidgetRight);
        Assert.Equal("model|in,out", loaded.CardFields);
        Assert.Equal(AppSettings.CurrentSchemaVersion, JsonNode.Parse(File.ReadAllText(SettingsPath))!["SchemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Saving_keeps_the_previous_file_as_a_backup()
    {
        SettingsService.Save(new AppSettings { Theme = "light" }, SettingsPath);
        SettingsService.Save(new AppSettings { Theme = "dark" }, SettingsPath);

        Assert.Equal("dark", SettingsService.Load(SettingsPath).Theme);
        Assert.Equal("light", JsonNode.Parse(File.ReadAllText(BackupPath))!["Theme"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("{ \"Theme\": \"dark\", ")]   // cut off mid-write
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ \"DayTargetUsd\": \"lots\" }")]
    public void A_broken_file_falls_back_to_the_backup(string broken)
    {
        SettingsService.Save(new AppSettings { Theme = "light", DayTargetUsd = 7m }, SettingsPath);
        SettingsService.Save(new AppSettings { Theme = "light", DayTargetUsd = 7m }, SettingsPath);
        File.WriteAllText(SettingsPath, broken);

        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal("light", settings.Theme);
        Assert.Equal(7m, settings.DayTargetUsd);
        Assert.Contains("loaded the backup", File.ReadAllText(DiagnosticLog.LogPath));
    }

    [Fact]
    public void A_broken_file_without_a_backup_gives_the_defaults()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var settings = SettingsService.Load(SettingsPath);

        // The defaults are a new install's (format 4: follow the computer).
        Assert.Equal(AppSettings.NewInstallTheme, settings.Theme);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public async Task A_reader_never_sees_a_partial_file_while_it_is_saved()
    {
        // Large enough that a non-atomic write would be caught half-way.
        var versions = Enumerable.Range(0, 2).Select(i => new AppSettings { CardFields = new string((char)('a' + i), 400_000) }).ToArray();
        SettingsService.Save(versions[0], SettingsPath);
        // At least two seconds and at least 11 writes and 11 reads, whichever
        // comes last: a busy machine (the whole suite, a virus scanner) makes
        // fewer saves a second, and a fixed time then proved too little.
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(20);
        var writerDone = false;
        var bad = 0;
        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                try
                {
                    // Opened the way a well-behaved reader does, so the writer's
                    // rename isn't blocked.
                    using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var text = new StreamReader(stream);
                    var node = JsonNode.Parse(text.ReadToEnd());
                    var fields = node!["CardFields"]!.GetValue<string>();
                    if (fields.Length != 400_000 || fields.Distinct().Count() != 1)
                        Interlocked.Increment(ref bad);
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex) when (ex is not IOException)
                {
                    Interlocked.Increment(ref bad);
                }
                catch (IOException)
                {
                    // The file is being swapped this instant; try again.
                }
                // A short pause between reads: Windows refuses to replace a file
                // that any reader has open, and the writer waits for it (up to
                // five tries), so a reader that never lets go would only measure
                // those waits.
                Thread.Sleep(2);
            }
        });

        var writes = 0;
        while (DateTime.UtcNow < deadline &&
               (DateTime.UtcNow < started.AddSeconds(2) || writes <= 10 || Volatile.Read(ref reads) <= 10))
            SettingsService.Save(versions[++writes % 2], SettingsPath);
        Volatile.Write(ref writerDone, true);
        await reader;

        Assert.Equal(0, bad);
        Assert.True(reads > 10, $"only {reads} reads completed");
        Assert.True(writes > 10, $"only {writes} writes completed");
    }

    [Fact]
    public void Atomic_write_creates_the_folder_and_leaves_no_temp_file()
    {
        var path = _dir.Combine("new", "folder", "file.json");

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void A_failed_atomic_write_leaves_the_old_file_and_no_temp_file()
    {
        var path = _dir.Combine("file.json");
        File.WriteAllText(path, "old");
        // A folder where the backup should go makes the copy fail.
        Directory.CreateDirectory(path + ".bak");

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new", backupPath: path + ".bak"));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }
}
