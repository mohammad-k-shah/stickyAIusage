using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StickyAIusage.Tests;

/// <summary>
/// scripts/install.ps1, run by Windows PowerShell against a release folder made
/// here (STICKY_INSTALL_BASE_URL), with LOCALAPPDATA and APPDATA pointed at a
/// temp folder and the "Start with Windows" key at a test key of its own, so a
/// run never touches the installed widget, its data or the real Run key.
/// Windows only (each test returns early elsewhere).
/// </summary>
public sealed class InstallPs1Tests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _runKey = @"Software\StickyAIusageInstallTests\" + Guid.NewGuid().ToString("N");

    private string Release => _dir.Combine("release");
    private string Local => _dir.Combine("local");
    private string Roaming => _dir.Combine("roaming");
    private string InstallDir => Path.Combine(Local, "Programs", "Sticky AI Usage");
    private string Exe => Path.Combine(InstallDir, "StickyAIusage.exe");
    private string Shortcut => Path.Combine(Roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Sticky AI Usage.lnk");

    [Fact]
    public void Installs_the_checked_download_with_a_shortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));

        var result = Run("-NoStart");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("build 1", File.ReadAllText(Exe));
        Assert.Equal(new[] { "StickyAIusage.exe", "install-method" }, File.ReadAllLines(Path.Combine(InstallDir, "installed-files.txt")));
        // The app's update notice then names this script.
        Assert.Equal("script", File.ReadAllText(Path.Combine(InstallDir, "install-method")).Trim());
        Assert.True(File.Exists(Shortcut));
        Assert.Contains("Checked StickyAIusage-win-x64.zip", result.Output);
    }

    [Fact]
    public void A_download_that_does_not_match_its_checksum_installs_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        Releases.Tamper(Path.Combine(Release, "StickyAIusage-win-x64.zip"));

        var result = Run("-NoStart");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("doesn't match checksums.txt", result.Output);
        Assert.False(Directory.Exists(InstallDir));
        Assert.False(File.Exists(Shortcut));
    }

    [Fact]
    public void A_release_without_checksums_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        File.Delete(Path.Combine(Release, "checksums.txt"));

        var result = Run("-NoStart");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("no checksums.txt", result.Output);
        // The "newest beta" hint is for GitHub's own "latest" (v1.0.0 until a stable v2 exists), not a mirror.
        Assert.DoesNotContain("-Prerelease", result.Output);
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void Purge_without_uninstall_changes_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        Directory.CreateDirectory(Path.Combine(Local, "StickyAIusage"));

        var result = Run("-Purge", "-NoStart");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("-Purge goes with -Uninstall", result.Output);
        Assert.False(Directory.Exists(InstallDir));
        Assert.True(Directory.Exists(Path.Combine(Local, "StickyAIusage")));
    }

    [Fact]
    public void A_relative_folder_is_taken_from_where_the_script_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        var script = Repository.Path("scripts", "install.ps1").Replace("'", "''");
        var workDir = _dir.Combine("work").Replace("'", "''");
        Directory.CreateDirectory(_dir.Combine("work"));

        // PowerShell's own current folder (Set-Location), which the process's may not follow.
        var result = ScriptProcess.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
             $"Set-Location '{workDir}'; & '{script}' -InstallDir 'here\\app' -NoStart; exit $LASTEXITCODE"],
            ScriptEnvironment());

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(_dir.Combine("work", "here", "app", "StickyAIusage.exe")), result.Output);
    }

    [Fact]
    public void Checksums_without_a_line_for_the_download_are_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        File.WriteAllText(Path.Combine(Release, "checksums.txt"), new string('a', 64) + "  something-else.zip\n");

        var result = Run("-NoStart");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("no line for StickyAIusage-win-x64.zip", result.Output);
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void A_zip_without_the_app_installs_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("readme.txt", "no app here"));

        var result = Run("-NoStart");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("has no StickyAIusage.exe", result.Output);
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void An_update_replaces_its_own_files_and_leaves_others_alone()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"), ("StickyAIusage.pdb", "symbols 1"));
        Assert.Equal(0, Run("-NoStart").ExitCode);
        File.WriteAllText(Path.Combine(InstallDir, "my-notes.txt"), "mine");

        Releases.Windows(Release, ("StickyAIusage.exe", "build 2"));
        var result = Run("-NoStart");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("build 2", File.ReadAllText(Exe));
        Assert.False(File.Exists(Path.Combine(InstallDir, "StickyAIusage.pdb")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(InstallDir, "my-notes.txt")));
    }

    [Fact]
    public void Uninstall_removes_this_copy_and_only_its_own_start_entries()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        Assert.Equal(0, Run("-NoStart").ExitCode);
        Directory.CreateDirectory(Path.Combine(Local, "StickyAIusage"));
        SetRunValues(new()
        {
            ["StickyAIusage"] = $"\"{Exe}\"",
            ["StickyAIusage-dev"] = $"\"{Exe}\" --profile dev",
            ["StickyAIusage-other"] = "\"C:\\Elsewhere\\StickyAIusage.exe\" --profile other",
            ["SomethingElse"] = $"\"{Exe}\""
        });

        var result = Run("-Uninstall");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(InstallDir));
        Assert.False(File.Exists(Shortcut));
        Assert.Equal("SomethingElse, StickyAIusage-other", string.Join(", ", RunValueNames()));
        Assert.True(Directory.Exists(Path.Combine(Local, "StickyAIusage")), "settings stay without -Purge");
        Assert.Contains("-Purge deletes them", result.Output);
    }

    [Fact]
    public void Uninstall_with_purge_deletes_the_settings_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        Assert.Equal(0, Run("-NoStart").ExitCode);
        Directory.CreateDirectory(Path.Combine(Local, "StickyAIusage", "logs"));

        var result = Run("-Uninstall", "-Purge");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(Path.Combine(Local, "StickyAIusage")));
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void Uninstall_leaves_a_folder_the_script_did_not_fill()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Exe, "unzipped by hand");

        var result = Run("-Uninstall");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("No copy installed by this script", result.Output);
        Assert.Equal("unzipped by hand", File.ReadAllText(Exe));
    }

    [Fact]
    public void A_folder_from_the_setup_is_left_to_the_setup()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 2"));
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Exe, "from setup");
        File.WriteAllText(Path.Combine(InstallDir, "unins000.exe"), "uninstaller");

        var install = Run("-NoStart");
        var uninstall = Run("-Uninstall");

        Assert.Equal(4, install.ExitCode);
        Assert.Equal(4, uninstall.ExitCode);
        Assert.Contains("winget", install.Output);
        Assert.Equal("from setup", File.ReadAllText(Exe));
    }

    [Fact]
    public void A_running_copy_is_never_closed_by_the_script()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 2"));
        Directory.CreateDirectory(InstallDir);
        // Any program will do as "the running widget", as long as it runs from the folder under that name.
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Exe);
        using var running = Process.Start(new ProcessStartInfo(Exe, "-n 120 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("couldn't start the stand-in");
        try
        {
            var install = Run("-NoStart");
            var uninstall = Run("-Uninstall");

            Assert.Equal(3, install.ExitCode);
            Assert.Equal(3, uninstall.ExitCode);
            Assert.Contains("Exit it first", install.Output);
            Assert.False(running.HasExited, "the script must not close it");
            Assert.True(File.Exists(Exe));
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
    }

    [Fact]
    public void Pasted_into_a_window_it_reports_without_closing_the_window()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        Releases.Tamper(Path.Combine(Release, "StickyAIusage-win-x64.zip"));
        var script = Repository.Path("scripts", "install.ps1").Replace("'", "''");

        // What "& ([scriptblock]::Create((irm …))) -NoStart" does: the script must
        // not end the PowerShell session it was pasted into.
        var result = ScriptProcess.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
             $"& ([scriptblock]::Create([IO.File]::ReadAllText('{script}'))) -NoStart; 'still here, code ' + $LASTEXITCODE"],
            ScriptEnvironment());

        Assert.Contains("still here, code 2", result.Output);
    }

    [Fact]
    public void Works_without_powershell_s_own_modules()
    {
        if (!OperatingSystem.IsWindows()) return;
        Releases.Windows(Release, ("StickyAIusage.exe", "build 1"));
        var environment = ScriptEnvironment();
        // Windows PowerShell started from PowerShell 7 (a GitHub runner, a pwsh
        // terminal) inherits 7's module path and finds none of its own script
        // modules: 7's own Utility and Archive come first and don't load in 5.1,
        // so Get-FileHash and Expand-Archive are "not recognized". Stand-ins
        // that need PowerShell 7.4 do the same.
        var modules = _dir.Combine("pwsh7-modules");
        foreach (var (module, function) in new[] { ("Microsoft.PowerShell.Utility", "Get-FileHash"), ("Microsoft.PowerShell.Archive", "Expand-Archive") })
        {
            Directory.CreateDirectory(Path.Combine(modules, module));
            File.WriteAllText(Path.Combine(modules, module, module + ".psd1"),
                $"@{{ ModuleVersion = '7.0.0'; PowerShellVersion = '7.4'; FunctionsToExport = @('{function}') }}");
        }
        environment["PSModulePath"] = modules;

        var result = ScriptProcess.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Repository.Path("scripts", "install.ps1"), "-NoStart"],
            environment);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("build 1", File.ReadAllText(Exe));
    }

    private ScriptProcess.Result Run(params string[] arguments) =>
        ScriptProcess.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Repository.Path("scripts", "install.ps1"), .. arguments],
            ScriptEnvironment());

    private Dictionary<string, string?> ScriptEnvironment() => new()
    {
        ["STICKY_INSTALL_BASE_URL"] = Release,
        ["STICKY_INSTALL_RUN_KEY"] = @"HKCU:\" + _runKey,
        ["STICKY_INSTALL_NONINTERACTIVE"] = "1",
        ["LOCALAPPDATA"] = Local,
        ["APPDATA"] = Roaming
    };

    [SupportedOSPlatform("windows")]
    private void SetRunValues(Dictionary<string, string> values)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_runKey);
        foreach (var (name, value) in values)
            key.SetValue(name, value);
    }

    [SupportedOSPlatform("windows")]
    private string[] RunValueNames()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_runKey);
        return key is null ? [] : key.GetValueNames().Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\StickyAIusageInstallTests", throwOnMissingSubKey: false);
        _dir.Dispose();
    }
}

/// <summary>
/// scripts/install.sh against a release folder made here, with HOME and the
/// Applications folder in a temp folder. Runs wherever a POSIX sh is found:
/// macOS (the real ditto, shasum and pgrep) and Windows with Git for Windows
/// (its unzip and sha256sum).
/// </summary>
public sealed class InstallShTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string Release => _dir.Combine("release");
    private string Home => _dir.Combine("home");
    private string Apps => _dir.Combine("apps");
    private string App => Path.Combine(Apps, "Sticky AI Usage.app");

    [Fact]
    public void Installs_the_checked_build_for_this_mac()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");

        var result = Run("intel", "--no-open");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("intel build", File.ReadAllText(Path.Combine(App, "Contents", "MacOS", "StickyAIusage")));
        Assert.Contains("Checked StickyAIusage-mac-intel.zip", result.Output);
    }

    [Fact]
    public void Apple_silicon_gets_its_own_build()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");

        var missing = Run("arm64", "--no-open");
        Releases.Mac(Release, "StickyAIusage-mac-apple-silicon.zip", "arm build");
        var found = Run("arm64", "--no-open");

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no StickyAIusage-mac-apple-silicon.zip", missing.Output);
        Assert.True(found.ExitCode == 0, found.Output);
        Assert.Equal("arm build", File.ReadAllText(Path.Combine(App, "Contents", "MacOS", "StickyAIusage")));
    }

    [Fact]
    public void A_download_that_does_not_match_its_checksum_installs_nothing()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");
        Releases.Tamper(Path.Combine(Release, "StickyAIusage-mac-intel.zip"));

        var result = Run("intel", "--no-open");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("doesn't match checksums.txt", result.Output);
        Assert.False(Directory.Exists(App));
    }

    [Fact]
    public void A_release_without_checksums_is_refused()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");
        File.Delete(Path.Combine(Release, "checksums.txt"));

        var result = Run("intel", "--no-open");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("no checksums.txt", result.Output);
        Assert.False(Directory.Exists(App));
    }

    [Fact]
    public void Uninstall_removes_the_app_and_only_its_own_login_entries()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");
        Assert.Equal(0, Run("intel", "--no-open").ExitCode);
        var agents = Path.Combine(Home, "Library", "LaunchAgents");
        Directory.CreateDirectory(agents);
        var appPath = ScriptProcess.ShPath(App);
        File.WriteAllText(Path.Combine(agents, "com.stickyaiusage.widget.plist"), $"<string>{appPath}/Contents/MacOS/StickyAIusage</string>");
        File.WriteAllText(Path.Combine(agents, "com.stickyaiusage.widget-dev.plist"), "<string>/Other/Sticky AI Usage.app/Contents/MacOS/StickyAIusage</string>");
        var data = Path.Combine(Home, "Library", "Application Support", "StickyAIusage");
        Directory.CreateDirectory(data);

        var result = Run("intel", "--uninstall");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(App));
        Assert.Equal("com.stickyaiusage.widget-dev.plist", string.Join(", ", Directory.GetFiles(agents).Select(f => Path.GetFileName(f))));
        Assert.True(Directory.Exists(data), "settings stay without --purge");
        Assert.Contains("--purge deletes them", result.Output);
    }

    [Fact]
    public void A_login_entry_is_found_when_the_folder_name_needs_xml_escaping()
    {
        if (ScriptProcess.Sh is null) return;
        var apps = _dir.Combine("o'brien & co");
        var agents = Path.Combine(Home, "Library", "LaunchAgents");
        Directory.CreateDirectory(agents);
        // What the app's SecurityElement.Escape writes for that folder.
        File.WriteAllText(Path.Combine(agents, "com.stickyaiusage.widget.plist"),
            $"<string>{ScriptProcess.ShPath(apps).Replace("&", "&amp;").Replace("'", "&apos;")}/Sticky AI Usage.app/Contents/MacOS/StickyAIusage</string>");

        var result = ScriptProcess.Run(ScriptProcess.Sh,
            [ScriptProcess.ShPath(Repository.Path("scripts", "install.sh")), "--uninstall", "--dir", ScriptProcess.ShPath(apps)],
            new() { ["HOME"] = ScriptProcess.ShPath(Home), ["STICKY_INSTALL_NONINTERACTIVE"] = "1" });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Empty(Directory.GetFiles(agents));
    }

    [Fact]
    public void Purge_without_uninstall_changes_nothing()
    {
        if (ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");

        var result = Run("intel", "--purge", "--no-open");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--purge goes with --uninstall", result.Output);
        Assert.False(Directory.Exists(App));
    }

    [Fact]
    public void Uninstall_with_purge_deletes_the_settings_and_logs()
    {
        if (ScriptProcess.Sh is null) return;
        var data = Path.Combine(Home, "Library", "Application Support", "StickyAIusage");
        var logs = Path.Combine(Home, "Library", "Logs", "StickyAIusage");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(logs);

        var result = Run("intel", "--uninstall", "--purge");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(logs));
    }

    [Fact]
    public void Help_works_when_the_script_comes_through_a_pipe()
    {
        if (ScriptProcess.Sh is null) return;
        // "curl … | sh -s -- --help": the script arrives on standard input, so there is no file to read back.
        var script = ScriptProcess.ShPath(Repository.Path("scripts", "install.sh"));

        var result = ScriptProcess.Run(ScriptProcess.Sh, ["-c", $"sh -s -- --help < \"{script}\""], new());

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("--uninstall", result.Output);
        Assert.Contains("checksums.txt", result.Output);
    }

    [Fact]
    public void Outside_macOS_it_says_where_to_look_instead()
    {
        if (ScriptProcess.Sh is null || OperatingSystem.IsMacOS()) return;

        var result = ScriptProcess.Run(ScriptProcess.Sh, [ScriptProcess.ShPath(Repository.Path("scripts", "install.sh")), "--dir", ScriptProcess.ShPath(Apps)],
            new() { ["HOME"] = ScriptProcess.ShPath(Home), ["STICKY_INSTALL_BASE_URL"] = null });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("install.ps1", result.Output);
    }

    [Fact]
    public void A_running_copy_is_never_quit_by_the_script()
    {
        if (!OperatingSystem.IsMacOS() || ScriptProcess.Sh is null) return;
        Releases.Mac(Release, "StickyAIusage-mac-apple-silicon.zip", "arm build");
        Releases.Mac(Release, "StickyAIusage-mac-intel.zip", "intel build");
        var binary = Path.Combine(App, "Contents", "MacOS", "StickyAIusage");
        Directory.CreateDirectory(Path.Combine(App, "Contents", "MacOS"));
        // Any program will do as "the running app", as long as it runs from the bundle under that name.
        File.Copy("/bin/sleep", binary);
        using var running = Process.Start(new ProcessStartInfo(binary, "120") { UseShellExecute = false })
            ?? throw new InvalidOperationException("couldn't start the stand-in");
        try
        {
            var result = Run(RuntimeArchitecture(), "--no-open");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("Quit it first", result.Output);
            Assert.False(running.HasExited, "the script must not quit it");
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
    }

    private static string RuntimeArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "intel";

    private ScriptProcess.Result Run(string arch, params string[] arguments) =>
        ScriptProcess.Run(ScriptProcess.Sh ?? throw new InvalidOperationException("no sh"),
            [ScriptProcess.ShPath(Repository.Path("scripts", "install.sh")), "--dir", ScriptProcess.ShPath(Apps), .. arguments],
            new()
            {
                ["HOME"] = ScriptProcess.ShPath(Home),
                ["STICKY_INSTALL_BASE_URL"] = ScriptProcess.ShPath(Release),
                ["STICKY_INSTALL_ARCH"] = arch == "arm64" ? "arm64" : "x86_64",
                ["STICKY_INSTALL_NONINTERACTIVE"] = "1"
            });

    public void Dispose() => _dir.Dispose();
}

/// <summary>packaging/New-PackageManifests.ps1, the release files' names and the
/// rules for the scripts' files. Runs where a PowerShell is found.</summary>
public sealed class PackagingTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void The_scoop_manifest_is_filled_from_the_release_checksums()
    {
        if (ScriptProcess.PowerShell is null) return;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("zip")));
        var sums = _dir.Combine("checksums.txt");
        File.WriteAllText(sums, $"{hash}  StickyAIusage-win-x64.zip\n{new string('b', 64)}  checksums-other.zip\n{SetupHash}  StickyAIusage-Setup-x64.exe\n");

        var result = Generate("v2.0.0-beta.2", sums);

        Assert.True(result.ExitCode == 0, result.Output);
        var manifest = JsonNode.Parse(File.ReadAllText(_dir.Combine("out", "scoop", "sticky-ai-usage.json")))!;
        Assert.Equal("2.0.0-beta.2", (string?)manifest["version"]);
        var x64 = manifest["architecture"]!["64bit"]!;
        Assert.Equal("https://github.com/mohammad-k-shah/stickyAIusage/releases/download/v2.0.0-beta.2/StickyAIusage-win-x64.zip", (string?)x64["url"]);
        Assert.Equal(hash, (string?)x64["hash"]);
        // Scoop's own updater writes the next versions from the same pattern.
        var next = (string?)manifest["autoupdate"]!["architecture"]!["64bit"]!["url"];
        Assert.Equal((string?)x64["url"], next?.Replace("$version", "2.0.0-beta.2"));
        Assert.Equal("$baseurl/checksums.txt", (string?)manifest["autoupdate"]!["hash"]!["url"]);
        Assert.Equal("StickyAIusage.exe", (string?)manifest["shortcuts"]![0]![0]);
    }

    private static readonly string SetupHash = new('e', 64);

    [Fact]
    public void The_winget_manifests_are_filled_in_the_winget_pkgs_layout_and_validate()
    {
        if (ScriptProcess.PowerShell is null) return;
        var sums = _dir.Combine("checksums.txt");
        File.WriteAllText(sums, $"{new string('a', 64)}  StickyAIusage-win-x64.zip\n{SetupHash}  StickyAIusage-Setup-x64.exe\n");

        var result = Generate("v2.0.0-beta.2", sums);

        Assert.True(result.ExitCode == 0, result.Output);
        var folder = _dir.Combine("out", "winget", "manifests", "m", "MohammadKShah", "StickyAIUsage", "2.0.0-beta.2");
        var installer = File.ReadAllText(Path.Combine(folder, "MohammadKShah.StickyAIUsage.installer.yaml"));
        Assert.Contains("PackageVersion: 2.0.0-beta.2", installer);
        Assert.Contains("InstallerUrl: https://github.com/mohammad-k-shah/stickyAIusage/releases/download/v2.0.0-beta.2/StickyAIusage-Setup-x64.exe", installer);
        Assert.Contains("InstallerSha256: " + SetupHash, installer);
        Assert.True(File.Exists(Path.Combine(folder, "MohammadKShah.StickyAIUsage.yaml")));
        Assert.True(File.Exists(Path.Combine(folder, "MohammadKShah.StickyAIUsage.locale.en-US.yaml")));
        Assert.DoesNotContain('\r', installer);

        // winget's own check, where winget is installed. An older winget (GitHub's
        // Windows runner) doesn't know schema 1.12.0, the one Microsoft's docs
        // name, and only warns about the header URL; anything else fails.
        if (!OperatingSystem.IsWindows()) return;
        ScriptProcess.Result validate;
        try { validate = ScriptProcess.Run("winget", ["validate", "--manifest", folder], new()); }
        catch (System.ComponentModel.Win32Exception) { return; }
        if (validate.ExitCode == 0) return;
        Assert.True(OnlySchemaHeaderWarnings(validate.Output), validate.Output);
    }

    internal static bool OnlySchemaHeaderWarnings(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var warnings = lines.Where(l => l.StartsWith("Manifest Warning:", StringComparison.Ordinal)).ToList();
        return lines.Any(l => l.StartsWith("Manifest validation succeeded with warnings", StringComparison.Ordinal))
               && warnings.Count > 0
               && warnings.All(w => w.Contains("The schema header URL does not match the expected pattern", StringComparison.Ordinal))
               && !lines.Any(l => l.StartsWith("Manifest Error", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Manifest validation succeeded with warnings.\nManifest Warning: The schema header URL does not match the expected pattern. Value: x", true)]
    [InlineData("Manifest validation succeeded with warnings.\nManifest Warning: The schema header URL does not match the expected pattern. Value: x\nManifest Warning: Unknown field Foo", false)]
    [InlineData("Manifest validation failed.\nManifest Error: Field InstallerSha256 is invalid", false)]
    [InlineData("Manifest validation succeeded with warnings.", false)]
    public void Only_an_older_winget_s_schema_header_warning_is_accepted(string output, bool accepted) =>
        Assert.Equal(accepted, OnlySchemaHeaderWarnings(output));

    [Fact]
    public void The_installer_the_winget_manifest_and_the_app_agree()
    {
        var setup = Repository.File("installer", "windows", "setup.iss");
        var installer = Repository.File("packaging", "winget", "manifests", "m", "MohammadKShah", "StickyAIUsage", "{{VERSION}}",
            "MohammadKShah.StickyAIUsage.installer.yaml.template");
        var app = Repository.File("src", "StickyAIusage", "App.xaml.cs");

        // The Apps entry Inno Setup writes is "<AppId>_is1": winget matches upgrades on it.
        var appId = System.Text.RegularExpressions.Regex.Match(setup, @"AppId=\{(\{[0-9A-F-]{36}\})").Groups[1].Value;
        Assert.NotEqual("", appId);
        Assert.Contains($"ProductCode: '{appId}_is1'", installer);
        // Setup and Uninstall find a running copy through the widget's own single-instance mutex.
        var mutex = System.Text.RegularExpressions.Regex.Match(setup, "#define AppMutexName \"([^\"]+)\"").Groups[1].Value;
        Assert.Contains($@"Local\{mutex}""", app);
        // The file Setup writes is the one the manifest downloads and the release uploads.
        Assert.Contains("OutputBaseFilename=StickyAIusage-Setup-x64", setup);
        Assert.Contains("/StickyAIusage-Setup-x64.exe", installer);
        Assert.Contains("StickyAIusage-Setup-x64.exe", Repository.File(".github", "workflows", "release.yml"));
    }

    [Fact]
    public void A_file_missing_from_the_checksums_fails_the_run()
    {
        if (ScriptProcess.PowerShell is null) return;
        var sums = _dir.Combine("checksums.txt");
        File.WriteAllText(sums, $"{new string('c', 64)}  StickyAIusage-mac-intel.zip\n");

        var result = Generate("v2.0.0", sums);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("StickyAIusage-win-x64.zip", result.Output);
        Assert.False(File.Exists(_dir.Combine("out", "scoop", "sticky-ai-usage.json")));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("latest")]
    [InlineData("v2.0.0 beta")]
    public void A_version_that_is_not_a_release_version_fails_the_run(string version)
    {
        if (ScriptProcess.PowerShell is null) return;
        var sums = _dir.Combine("checksums.txt");
        File.WriteAllText(sums, $"{new string('d', 64)}  StickyAIusage-win-x64.zip\n{SetupHash}  StickyAIusage-Setup-x64.exe\n");

        var result = Generate(version, sums);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("isn't a release version", result.Output);
    }

    [Theory]
    [InlineData("scripts", "install.ps1")]
    [InlineData("packaging", "New-PackageManifests.ps1")]
    [InlineData("installer/windows", "setup.iss")]
    public void PowerShell_files_are_plain_ascii(string folder, string file)
    {
        // Windows PowerShell 5.1 reads a file without a byte-order mark in the
        // system code page: one dash or arrow outside ASCII breaks the script there.
        // Inno Setup reads a BOM-less .iss the same way (and 7.0.2+ refuses bad bytes).
        var bytes = File.ReadAllBytes(Repository.Path(folder, file));
        var first = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(first < 0, $"{file} has a non-ASCII byte at offset {first}");
    }

    [Fact]
    public void The_release_the_scripts_and_the_manifests_name_the_same_files()
    {
        var release = Repository.File(".github", "workflows", "release.yml");
        var ps1 = Repository.File("scripts", "install.ps1");
        var sh = Repository.File("scripts", "install.sh");
        var scoop = Repository.File("packaging", "scoop", "sticky-ai-usage.json.template");

        foreach (var asset in new[] { "StickyAIusage-win-x64.zip", "StickyAIusage-win-x64-small.zip", "StickyAIusage-Setup-x64.exe", "StickyAIusage-mac-apple-silicon.zip", "StickyAIusage-mac-intel.zip", "checksums.txt" })
            Assert.Contains(asset, release);
        Assert.Contains("'StickyAIusage-win-x64.zip'", ps1);
        Assert.Contains("StickyAIusage-mac-apple-silicon.zip", sh);
        Assert.Contains("StickyAIusage-mac-intel.zip", sh);
        Assert.Contains("/StickyAIusage-win-x64.zip", scoop);
        // The lines people paste come from the README and must reach these files.
        var readme = Repository.File("README.md");
        Assert.Contains("irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1 | iex", readme);
        Assert.Contains("curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh", readme);
    }

    [Theory]
    [InlineData("scripts", "install.sh")]
    [InlineData("scripts", "build-mac-app.sh")]
    public void Shell_scripts_have_unix_line_ends(string folder, string file)
    {
        // A Windows checkout converts text to CRLF unless .gitattributes says
        // otherwise, and sh then fails on the first line.
        var bytes = File.ReadAllBytes(Repository.Path(folder, file));
        Assert.True(Array.IndexOf(bytes, (byte)'\r') < 0, $"{file} has CR line ends: .gitattributes must keep *.sh LF");
    }

    [Theory]
    [InlineData("scripts/install.sh")]
    [InlineData("scripts/build-mac-app.sh")]
    public void Shell_scripts_stay_executable_in_git(string path)
    {
        // Windows git drops the executable bit; "curl | sh" doesn't need it, but
        // the Mac build and a cloned copy's "scripts/install.sh" do.
        var root = Repository.Root();
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            return;
        ScriptProcess.Result result;
        try { result = ScriptProcess.Run("git", ["-C", root, "ls-files", "-s", "--", path], new()); }
        catch (System.ComponentModel.Win32Exception) { return; } // no git on this machine
        if (result.ExitCode != 0 || result.Output.Length == 0)
            return; // no git, or the file isn't committed yet
        Assert.StartsWith("100755 ", result.Output);
    }

    private ScriptProcess.Result Generate(string version, string checksums) =>
        ScriptProcess.Run(ScriptProcess.PowerShell ?? throw new InvalidOperationException("no PowerShell"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Repository.Path("packaging", "New-PackageManifests.ps1"),
             "-Version", version, "-ChecksumsFile", checksums, "-OutDir", _dir.Combine("out")],
            new());

    public void Dispose() => _dir.Dispose();
}

/// <summary>Release folders as the Release workflow lays them out: the zip(s)
/// and a checksums.txt in sha256sum's format.</summary>
internal static class Releases
{
    public static void Windows(string folder, params (string Name, string Content)[] files) =>
        Add(folder, "StickyAIusage-win-x64.zip", files);

    public static void Mac(string folder, string asset, string binaryContent) =>
        Add(folder, asset, ("Sticky AI Usage.app/Contents/MacOS/StickyAIusage", binaryContent), ("Sticky AI Usage.app/Contents/Info.plist", "<plist/>"));

    /// <summary>Changes the zip after its checksum was written, as a proxy might.</summary>
    public static void Tamper(string zip)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        using var writer = new StreamWriter(archive.CreateEntry("extra.txt").Open());
        writer.Write("changed on the way");
    }

    private static void Add(string folder, string asset, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, asset);
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        var sums = Path.Combine(folder, "checksums.txt");
        var lines = File.Exists(sums)
            ? File.ReadAllLines(sums).Where(l => !l.EndsWith("  " + asset, StringComparison.Ordinal)).ToList()
            : [];
        lines.Add($"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)))}  {asset}");
        File.WriteAllText(sums, string.Join("\n", lines) + "\n");
    }
}

internal static class Repository
{
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StickyAIusage.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("The repository root (StickyAIusage.sln) is not above the test folder.");
    }

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root(), .. parts]);

    public static string File(params string[] parts) => System.IO.File.ReadAllText(Path(parts));
}

/// <summary>Runs a script's interpreter with extra environment variables (null
/// removes one) and collects everything it printed.</summary>
internal static class ScriptProcess
{
    public sealed record Result(int ExitCode, string Output);

    /// <summary>Windows PowerShell on Windows, else pwsh when it is installed.</summary>
    public static string? PowerShell { get; } = OperatingSystem.IsWindows() ? "powershell.exe" : FindOnPath("pwsh");

    /// <summary>A POSIX sh: /bin/sh, or Git for Windows' own.</summary>
    public static string? Sh { get; } = FindSh();

    public static Result Run(string program, IEnumerable<string> arguments, Dictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"couldn't start {program}");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(90)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{program} {string.Join(' ', arguments)} didn't finish in 90 s");
        }
        process.WaitForExit();
        return new Result(process.ExitCode, output.Result + errors.Result);
    }

    /// <summary>Git for Windows' sh takes C:/ paths; elsewhere a path is a path.</summary>
    public static string ShPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static string? FindSh()
    {
        if (!OperatingSystem.IsWindows())
            return File.Exists("/bin/sh") ? "/bin/sh" : null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
        {
            if (root is null) continue;
            var sh = Path.Combine(root, "Git", "bin", "sh.exe");
            if (File.Exists(sh)) return sh;
        }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
