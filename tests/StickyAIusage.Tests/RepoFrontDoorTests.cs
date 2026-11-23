using System.Text.RegularExpressions;

namespace StickyAIusage.Tests;

/// <summary>The repository's front door: issue and pull-request templates, the security policy and
/// the code of conduct exist, say what GitHub needs, and carry no personal contact details.</summary>
public class RepoFrontDoorTests
{
    private static string Root => InstallMethodTests.RepoRoot();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts])).Replace("\r\n", "\n");

    [Theory]
    [InlineData("bug_report.yml", "bug")]
    [InlineData("feature_request.yml", "enhancement")]
    [InlineData("provider_request.yml", "provider")]
    public void Each_issue_template_has_a_name_a_description_a_label_and_a_required_field(string file, string label)
    {
        var text = Read(".github", "ISSUE_TEMPLATE", file);

        Assert.Matches(new Regex(@"^name: \S", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^description: \S", RegexOptions.Multiline), text);
        Assert.Contains($"labels: [\"{label}\"]", text);
        Assert.Contains("\nbody:\n", text);
        Assert.Contains("required: true", text);
        Assert.DoesNotContain("\t", text); // YAML: spaces only
    }

    [Fact]
    public void Blank_issues_are_off_and_questions_and_security_reports_go_elsewhere()
    {
        var config = Read(".github", "ISSUE_TEMPLATE", "config.yml");
        Assert.Contains("blank_issues_enabled: false", config);
        Assert.Contains("https://github.com/mohammad-k-shah/stickyAIusage/discussions", config);
        Assert.Contains("https://github.com/mohammad-k-shah/stickyAIusage/security/advisories/new", config);
    }

    [Fact]
    public void The_bug_template_asks_for_the_health_report_and_names_every_usage_source()
    {
        var bug = Read(".github", "ISSUE_TEMPLATE", "bug_report.yml");
        Assert.Contains("Copy report", bug);
        foreach (var source in new[] { "Plugin", "Automatic", "Legacy", "\"Off\"" })
            Assert.Contains("- " + source, bug);
    }

    [Theory]
    [InlineData("SECURITY.md")]
    [InlineData("CODE_OF_CONDUCT.md")]
    [InlineData(".github/PULL_REQUEST_TEMPLATE.md")]
    public void The_policy_files_exist_and_hold_no_e_mail_address(string file)
    {
        var text = Read(file.Split('/'));
        Assert.DoesNotMatch(new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), text);
    }

    [Fact]
    public void The_security_policy_reports_privately_and_names_the_supported_release()
    {
        var security = Read("SECURITY.md");
        Assert.Contains("security/advisories/new", security);
        Assert.Contains("| 2.0.x | Yes |", security);
        Assert.Contains("docs/DATA-SOURCES.md", security);
    }
}
