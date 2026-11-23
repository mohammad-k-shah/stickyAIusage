using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StickyAIusage.Tests;

/// <summary>
/// The release signing hooks: Windows through SignPath and macOS with a Developer ID and
/// notarization. Both stay inert until the maintainer adds the accounts' settings, so a
/// release without them is exactly the unsigned one it was before.
/// <c>scripts/sign-mac-app.sh</c> runs here with stand-ins for codesign, ditto, file and
/// xcrun that write down how they were called.
/// </summary>
public class SigningHookTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string Root => InstallMethodTests.RepoRoot();

    // ------------------------------------------------------------ the Mac script

    private static string? Bash()
    {
        if (!OperatingSystem.IsWindows())
            return File.Exists("/bin/bash") ? "/bin/bash" : null;
        // Git's usr\bin\bash.exe itself: bin\bash.exe is a launcher that puts Git's tools ahead
        // of the stand-ins on PATH (its own "file" would answer instead of the test's).
        return ScriptProcess.Sh is { } sh &&
               Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sh)!)!, "usr", "bin", "bash.exe") is var bash && File.Exists(bash)
            ? bash
            : null;
    }

    private string App => _dir.Combine("out", "Sticky AI Usage.app");
    private string Zip => _dir.Combine("out", "StickyAIusage-osx-arm64.zip");
    private string CallLog => _dir.Combine("calls.log");

    /// <summary>A bundle with the app, a .NET library, a helper and a plain file, and the stand-ins.</summary>
    private void Prepare()
    {
        var macOs = Path.Combine(App, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        foreach (var name in new[] { "StickyAIusage", "libcoreclr.dylib", "createdump", "StickyAIusage.dll" })
            File.WriteAllText(Path.Combine(macOs, name), "x");

        var bin = Directory.CreateDirectory(_dir.Combine("bin")).FullName;
        Stub(bin, "codesign", "");
        // ditto -c -k --keepParent <folder> <zip>: the zip is the last argument.
        Stub(bin, "ditto", "for last; do :; done; : > \"$last\"");
        Stub(bin, "xcrun", "if [ \"$1 $2\" = \"stapler staple\" ] && [ -n \"${STAPLE_FAILS:-}\" ]; then exit 65; fi");
        Stub(bin, "file", "case \"$2\" in *.dylib|*/createdump|*/StickyAIusage) echo 'Mach-O 64-bit executable arm64' ;; *) echo data ;; esac");
    }

    private void Stub(string bin, string name, string body) =>
        File.WriteAllText(Path.Combine(bin, name),
            "#!/usr/bin/env bash\nprintf '%s' \"" + name + "\" >> \"$CALL_LOG\"; printf ' [%s]' \"$@\" >> \"$CALL_LOG\"; echo >> \"$CALL_LOG\"\n" +
            body + "\n");

    private (ScriptProcess.Result Result, List<string> Calls) Sign(Dictionary<string, string?> extra)
    {
        var bash = Bash()!;
        var bin = _dir.Combine("bin");
        // On Windows, Git's own find comes before System32's (a different program of the same name).
        var tools = OperatingSystem.IsWindows() ? Path.GetDirectoryName(bash) + Path.PathSeparator : "";
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = bin + Path.PathSeparator + tools + Environment.GetEnvironmentVariable("PATH"),
            ["CALL_LOG"] = ScriptProcess.ShPath(CallLog),
            ["MAC_SIGN_IDENTITY"] = null, ["MAC_NOTARY_KEY_PATH"] = null, ["MAC_NOTARY_KEY_ID"] = null, ["MAC_NOTARY_ISSUER"] = null
        };
        foreach (var (name, value) in extra)
            environment[name] = value;
        if (!OperatingSystem.IsWindows())
            foreach (var stub in Directory.GetFiles(bin))
                File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = ScriptProcess.Run(bash,
            [ScriptProcess.ShPath(Path.Combine(Root, "scripts", "sign-mac-app.sh")), ScriptProcess.ShPath(App), ScriptProcess.ShPath(Zip)],
            environment);
        var calls = File.Exists(CallLog)
            ? File.ReadAllLines(CallLog).Select(line => line.Replace(ScriptProcess.ShPath(_dir.Path), "<tmp>")).ToList()
            : [];
        return (result, calls);
    }

    private static readonly Dictionary<string, string?> Identity = new()
    {
        ["MAC_SIGN_IDENTITY"] = "Developer ID Application: Example (TEAMID1234)"
    };

    private static readonly Dictionary<string, string?> Notary = new(Identity)
    {
        ["MAC_NOTARY_KEY_PATH"] = "/tmp/key.p8", ["MAC_NOTARY_KEY_ID"] = "KEYID12345", ["MAC_NOTARY_ISSUER"] = "issuer-uuid"
    };

    [Fact]
    public void Without_a_Developer_ID_the_app_is_ad_hoc_signed_and_zipped_as_before()
    {
        if (Bash() is null) return;
        Prepare();

        var (result, calls) = Sign([]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
        [
            "codesign [--force] [--deep] [--sign] [-] [<tmp>/out/Sticky AI Usage.app]",
            "ditto [-c] [-k] [--keepParent] [Sticky AI Usage.app] [<tmp>/out/StickyAIusage-osx-arm64.zip]"
        ], calls);
        Assert.True(File.Exists(Zip));
    }

    [Fact]
    public void Notary_settings_without_a_Developer_ID_change_nothing()
    {
        if (Bash() is null) return;
        Prepare();

        var (result, calls) = Sign(new Dictionary<string, string?>(Notary) { ["MAC_SIGN_IDENTITY"] = "" });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain(calls, c => c.StartsWith("xcrun"));
        Assert.StartsWith("codesign [--force] [--deep] [--sign] [-]", calls[0]);
    }

    [Fact]
    public void A_Developer_ID_signs_every_binary_inside_out_with_the_hardened_runtime()
    {
        if (Bash() is null) return;
        Prepare();

        var (result, calls) = Sign(Identity);

        Assert.True(result.ExitCode == 0, result.Output);
        var signs = calls.Where(c => c.StartsWith("codesign")).ToList();
        const string id = "[--sign] [Developer ID Application: Example (TEAMID1234)]";
        // The nested code (in any order), then the executable, then the bundle, then a strict check.
        Assert.Equal(
            $"codesign [--force] [--timestamp] [--options] [runtime] {id} [<tmp>/out/Sticky AI Usage.app/Contents/MacOS/createdump]\n" +
            $"codesign [--force] [--timestamp] [--options] [runtime] {id} [<tmp>/out/Sticky AI Usage.app/Contents/MacOS/libcoreclr.dylib]",
            string.Join('\n', signs.Take(2).Order(StringComparer.Ordinal)));
        Assert.Matches(new Regex(@"^codesign \[--force\] \[--timestamp\] \[--options\] \[runtime\] \[--entitlements\] \[[^\]]*mac-entitlements\.plist\] " +
                                 Regex.Escape(id) + @" \[<tmp>/out/Sticky AI Usage\.app/Contents/MacOS/StickyAIusage\]$"), signs[2]);
        Assert.EndsWith($"{id} [<tmp>/out/Sticky AI Usage.app]", signs[3]);
        Assert.Contains("[--entitlements]", signs[3]);
        Assert.Equal("codesign [--verify] [--strict] [--verbose=2] [<tmp>/out/Sticky AI Usage.app]", signs[4]);
        Assert.Equal(5, signs.Count); // the plain .dll isn't code
        Assert.DoesNotContain(calls, c => c.Contains("[--deep]") || c.StartsWith("xcrun"));
        Assert.Single(calls, c => c.StartsWith("ditto"));
    }

    [Fact]
    public void With_the_notary_key_the_zip_is_notarized_the_ticket_stapled_and_the_zip_made_again()
    {
        if (Bash() is null) return;
        Prepare();

        var (result, calls) = Sign(Notary);

        Assert.True(result.ExitCode == 0, result.Output);
        var tail = calls.SkipWhile(c => c.StartsWith("codesign") || c.StartsWith("file")).ToList();
        Assert.Equal(
        [
            "ditto [-c] [-k] [--keepParent] [Sticky AI Usage.app] [<tmp>/out/StickyAIusage-osx-arm64.zip]",
            "xcrun [notarytool] [submit] [<tmp>/out/StickyAIusage-osx-arm64.zip] [--key] [/tmp/key.p8] [--key-id] [KEYID12345] " +
            "[--issuer] [issuer-uuid] [--wait]",
            "xcrun [stapler] [staple] [<tmp>/out/Sticky AI Usage.app]",
            "xcrun [stapler] [validate] [<tmp>/out/Sticky AI Usage.app]",
            "ditto [-c] [-k] [--keepParent] [Sticky AI Usage.app] [<tmp>/out/StickyAIusage-osx-arm64.zip]"
        ], tail);
        Assert.Contains("Notarized and stapled", result.Output);
    }

    [Fact]
    public void A_refused_notarization_stops_the_build()
    {
        if (Bash() is null) return;
        Prepare();

        var (result, calls) = Sign(new Dictionary<string, string?>(Notary) { ["STAPLE_FAILS"] = "1" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(calls, c => c.StartsWith("xcrun [stapler] [validate]"));
        Assert.Single(calls, c => c.StartsWith("ditto"));
    }

    [Fact]
    public void The_build_script_signs_through_the_signing_script()
    {
        var build = File.ReadAllText(Path.Combine(Root, "scripts", "build-mac-app.sh"));
        Assert.Contains("bash \"$ROOT/scripts/sign-mac-app.sh\" \"$APP\" \"$OUT/StickyAIusage-$RID.zip\"", build);
        Assert.DoesNotContain("codesign", build.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Aggregate("", (a, l) => a + l));
        Assert.DoesNotContain("\r", File.ReadAllText(Path.Combine(Root, "scripts", "sign-mac-app.sh")));
    }

    [Fact]
    public void The_entitlements_allow_the_dotnet_runtime_and_nothing_else()
    {
        var plist = XDocument.Load(Path.Combine(Root, "scripts", "mac-entitlements.plist"));
        var dict = plist.Root!.Element("dict")!.Elements().ToList();
        var keys = dict.Where(e => e.Name == "key").Select(e => e.Value).ToList();
        Assert.Equal(
            ["com.apple.security.cs.allow-jit", "com.apple.security.cs.allow-unsigned-executable-memory",
             "com.apple.security.cs.disable-library-validation"], keys);
        Assert.All(dict.Where(e => e.Name != "key"), value => Assert.Equal("true", value.Name.LocalName));
    }

    // -------------------------------------------------------- the Release workflow

    /// <summary>The workflow's steps, each as its text.</summary>
    private static List<string> Steps(string job)
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml")).Replace("\r\n", "\n");
        var start = text.IndexOf($"\n  {job}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no job {job}");
        var end = Regex.Match(text[(start + 1)..], @"\n  [a-z-]+:\n") is { Success: true } next ? start + 1 + next.Index : text.Length;
        // Comment lines go: they sit above the step they describe, which splits them off to the one before.
        return Regex.Split(text[start..end], @"\n      - ").Skip(1)
            .Select(step => string.Join('\n', step.Split('\n').Where(line => !line.TrimStart().StartsWith('#')))).ToList();
    }

    [Fact]
    public void Every_Windows_signing_step_waits_for_the_SignPath_settings()
    {
        var signing = Steps("windows").Where(s => s.Contains("SIGNPATH") || s.Contains("signpath") || s.Contains("signed-")).ToList();

        Assert.Equal(6, signing.Count); // upload, sign, use - for the exes and for the Setup
        Assert.All(signing, step => Assert.Contains("if: vars.SIGNPATH_ORGANIZATION_ID != ''", step));
        Assert.Equal(2, signing.Count(s => s.Contains("uses: signpath/github-action-submit-signing-request@v3")));
        Assert.Equal(2, signing.Count(s => s.Contains("Get-AuthenticodeSignature") && s.Contains("-ne 'Valid'")));
        Assert.All(Steps("windows").Where(s => s.Contains("secrets.")), step => Assert.Contains("SIGNPATH", step));
    }

    [Fact]
    public void The_exes_are_signed_before_the_self_test_and_the_Setup_after_it_is_built()
    {
        var names = Steps("windows").Select(s => Regex.Match(s, @"name: (.+)").Groups[1].Value.Trim()).ToList();
        Assert.True(names.IndexOf("Use the signed exes") < names.IndexOf("Self-test both exes"));
        Assert.True(names.IndexOf("Use the signed exes") < names.IndexOf("Installer"));
        Assert.True(names.IndexOf("Installer") < names.IndexOf("Sign the Setup (SignPath)"));
    }

    [Fact]
    public void The_Mac_job_imports_a_certificate_only_when_there_is_one()
    {
        var steps = Steps("macos");
        var import = Assert.Single(steps, s => s.Contains("name: Import the Developer ID certificate"));
        var firstCommand = import[(import.IndexOf("run: |", StringComparison.Ordinal) + 6)..].TrimStart();
        Assert.StartsWith("if [ -z \"$MAC_CERTIFICATE_P12\" ]; then", firstCommand);
        Assert.InRange(firstCommand.IndexOf("exit 0", StringComparison.Ordinal), 0,
            firstCommand.IndexOf("security create-keychain", StringComparison.Ordinal));

        var build = Assert.Single(steps, s => s.Contains("name: Build both architectures"));
        Assert.Contains("MAC_SIGN_IDENTITY: ${{ steps.signing.outputs.identity }}", build);
        Assert.Contains("MAC_NOTARY_KEY_PATH: ${{ steps.signing.outputs.notary-key }}", build);
        var cleanup = Assert.Single(steps, s => s.Contains("name: Remove the signing keychain and key"));
        Assert.Contains("if: always()", cleanup);
    }

    [Fact]
    public void The_release_page_leads_with_the_highlights_of_its_version()
    {
        var step = Assert.Single(Steps("publish"), s => s.Contains("name: Create the release"));
        // On the page: the picture and what's new, how to install and check it, the note for updaters, the star, the pull requests.
        var order = new[] { "docs/images/demo.gif", "## What's new", "**Install it**", "[!NOTE]", "star the repository", "generate-notes" }
            .Select(text => step.IndexOf(text, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);

        // The step's own awk on the CHANGELOG's newest highlights: that section, and nothing after it.
        if (Bash() is not { } bash)
            return;
        var changelog = File.ReadAllText(Path.Combine(Root, "docs", "CHANGELOG.md")).Replace("\r\n", "\n");
        var heading = Regex.Match(changelog, @"^## (v\d+\.\d+\.\d+) — the highlights$", RegexOptions.Multiline);
        Assert.True(heading.Success, "docs/CHANGELOG.md has no \"## vX.Y.Z — the highlights\" section");
        var awk = Regex.Match(step, @"awk -v head=.*?docs/CHANGELOG\.md", RegexOptions.Singleline).Value;
        Assert.NotEmpty(awk);
        var script = _dir.Combine("highlights.sh");
        File.WriteAllText(script, $"cd \"{ScriptProcess.ShPath(Root)}\"\nTAG={heading.Groups[1].Value}\n{awk}\n");

        // On Windows, Git's own awk sits beside its bash.
        var tools = OperatingSystem.IsWindows() ? Path.GetDirectoryName(bash) + Path.PathSeparator : "";
        var result = ScriptProcess.Run(bash, [ScriptProcess.ShPath(script)],
            new() { ["PATH"] = tools + Environment.GetEnvironmentVariable("PATH"), ["LC_ALL"] = "C.UTF-8" });

        Assert.True(result.ExitCode == 0, result.Output);
        var output = result.Output.Replace("\r\n", "\n");
        var firstBullet = changelog[(heading.Index + heading.Length)..].Split('\n').First(line => line.StartsWith("- ", StringComparison.Ordinal));
        Assert.Contains(firstBullet, output);
        Assert.DoesNotContain("\n## ", "\n" + output);
        Assert.DoesNotContain("---", output);
    }
}
