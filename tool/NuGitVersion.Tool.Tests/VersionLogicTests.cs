using NuGitVersion.Tool;

namespace NuGitVersion.Tool.Tests;

public class VersionLogicTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] vars)
        => name => vars.FirstOrDefault(v => v.Name == name).Value;

    // ---------------------------------------------------------------- ParseArgs

    [Fact]
    public void ParseArgs_OutputDirOnly_UsesDefaultVersionFile()
    {
        var o = VersionLogic.ParseArgs(new[] { "obj/Debug" }, out var error);
        Assert.Null(error);
        Assert.NotNull(o);
        Assert.Equal("obj/Debug", o!.OutputDir);
        Assert.Equal("nugitversion.json", o.VersionFile);
        Assert.Null(o.FailIfMissing);
    }

    [Theory]
    [InlineData("--fail-if-missing=true", true)]
    [InlineData("--fail-if-missing=false", false)]
    [InlineData("--fail-if-missing=True", true)]
    [InlineData("--fail-if-missing", true)]
    public void ParseArgs_FailIfMissingOption(string option, bool expected)
    {
        var o = VersionLogic.ParseArgs(new[] { "obj", "v.json", option }, out var error);
        Assert.Null(error);
        Assert.Equal(expected, o!.FailIfMissing);
        Assert.Equal("v.json", o.VersionFile);
    }

    [Fact]
    public void ParseArgs_InvalidArguments_ReturnsError()
    {
        var invalid = new[]
        {
            Array.Empty<string>(),
            new[] { "obj", "v.json", "extra" },
            new[] { "obj", "--unknown" },
            new[] { "obj", "--fail-if-missing=maybe" },
        };
        foreach (var args in invalid)
        {
            var o = VersionLogic.ParseArgs(args, out var error);
            Assert.Null(o);
            Assert.False(string.IsNullOrEmpty(error), string.Join(" ", args));
        }
    }

    // ------------------------------------------------------------ NormalizePath

    [Fact]
    public void NormalizePath_OnWindows_IsUnchanged()
    {
        var path = @"C:\repo\proj\nugitversion.json";
        Assert.Same(path, VersionLogic.NormalizePath(path, isWindows: true, exists: _ => false));
    }

    [Fact]
    public void NormalizePath_OnUnix_MissingPathWithBackslash_IsConverted()
    {
        var path = @"/workspace/repo/JiraReports.Web\nugitversion.json";
        var result = VersionLogic.NormalizePath(path, isWindows: false, exists: _ => false);
        Assert.Equal("/workspace/repo/JiraReports.Web/nugitversion.json", result);
    }

    [Fact]
    public void NormalizePath_OnUnix_ExistingPathWithBackslash_IsKept()
    {
        var path = @"/data/odd\name.json";
        Assert.Same(path, VersionLogic.NormalizePath(path, isWindows: false, exists: p => p == path));
    }

    [Fact]
    public void NormalizePath_OnUnix_NoBackslash_IsUnchanged()
    {
        var path = "/workspace/repo/nugitversion.json";
        Assert.Same(path, VersionLogic.NormalizePath(path, isWindows: false, exists: _ => false));
    }

    // -------------------------------------------------------- DetectBuildServer

    [Theory]
    [InlineData("TF_BUILD", "True", "Azure")]
    [InlineData("GITHUB_ACTIONS", "true", "GitHub")]
    [InlineData("BITBUCKET_BUILD_NUMBER", "42", "BitBucket")]
    [InlineData("GITLAB_CI", "true", "GitLab")]
    [InlineData("JENKINS_URL", "http://jenkins", "Jenkins")]
    [InlineData("CI", "true", "Other")]
    public void DetectBuildServer_RecognizesEachSystem(string name, string value, string expected)
    {
        Assert.Equal(expected, VersionLogic.DetectBuildServer(Env((name, value))));
    }

    [Fact]
    public void DetectBuildServer_NoVariables_IsLocal()
    {
        Assert.Equal("Local", VersionLogic.DetectBuildServer(Env()));
    }

    // ------------------------------------------------------- ShouldFailIfMissing

    [Theory]
    [InlineData("Local", null, false)]
    [InlineData("GitHub", null, true)]
    [InlineData("Azure", null, true)]
    [InlineData("Other", null, true)]
    [InlineData("Local", true, true)]
    [InlineData("GitHub", false, false)]
    public void ShouldFailIfMissing_AutoAndOverride(string buildServer, bool? overrideValue, bool expected)
    {
        Assert.Equal(expected, VersionLogic.ShouldFailIfMissing(buildServer, overrideValue));
    }

    // ------------------------------------------------------------ ResolveBranch

    [Fact]
    public void ResolveBranch_GitHub_UsesRefName()
    {
        var env = Env(("GITHUB_REF_NAME", "main"));
        Assert.Equal("main", VersionLogic.ResolveBranch("GitHub", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_GitHub_PullRequest_PrefersHeadRef()
    {
        var env = Env(("GITHUB_HEAD_REF", "feature/x"), ("GITHUB_REF_NAME", "17/merge"));
        Assert.Equal("feature/x", VersionLogic.ResolveBranch("GitHub", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_Azure_UsesSourceBranchName()
    {
        var env = Env(("BUILD_SOURCEBRANCHNAME", "develop"));
        Assert.Equal("develop", VersionLogic.ResolveBranch("Azure", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_Azure_PullRequest_StripsRefsHeads()
    {
        var env = Env(("SYSTEM_PULLREQUEST_SOURCEBRANCH", "refs/heads/feature/y"), ("BUILD_SOURCEBRANCHNAME", "merge"));
        Assert.Equal("feature/y", VersionLogic.ResolveBranch("Azure", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_GitLab_UsesCommitRefName()
    {
        var env = Env(("CI_COMMIT_REF_NAME", "release/1.0"));
        Assert.Equal("release/1.0", VersionLogic.ResolveBranch("GitLab", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_GitLab_MergeRequest_PrefersSourceBranch()
    {
        var env = Env(("CI_MERGE_REQUEST_SOURCE_BRANCH_NAME", "mr-branch"), ("CI_COMMIT_REF_NAME", "main"));
        Assert.Equal("mr-branch", VersionLogic.ResolveBranch("GitLab", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_BitBucket_UsesBitbucketBranch()
    {
        var env = Env(("BITBUCKET_BRANCH", "main"));
        Assert.Equal("main", VersionLogic.ResolveBranch("BitBucket", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_Jenkins_PrefersBranchName()
    {
        var env = Env(("BRANCH_NAME", "main"), ("GIT_BRANCH", "origin/other"));
        Assert.Equal("main", VersionLogic.ResolveBranch("Jenkins", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_Jenkins_GitBranch_StripsOriginPrefix()
    {
        var env = Env(("GIT_BRANCH", "origin/develop"));
        Assert.Equal("develop", VersionLogic.ResolveBranch("Jenkins", env, "HEAD"));
    }

    [Fact]
    public void ResolveBranch_CiVariableWinsOverGit()
    {
        var env = Env(("GITHUB_REF_NAME", "main"));
        Assert.Equal("main", VersionLogic.ResolveBranch("GitHub", env, "some-local-branch"));
    }

    [Fact]
    public void ResolveBranch_NoVariable_FallsBackToGit()
    {
        Assert.Equal("feature/z", VersionLogic.ResolveBranch("GitHub", Env(), "feature/z"));
        Assert.Equal("main", VersionLogic.ResolveBranch("Local", Env(("GITHUB_REF_NAME", "ignored")), "main"));
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData(" HEAD ")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveBranch_DetachedHead_IsEmpty(string? gitBranch)
    {
        Assert.Equal(string.Empty, VersionLogic.ResolveBranch("Other", Env(), gitBranch));
        Assert.Equal(string.Empty, VersionLogic.ResolveBranch("Local", Env(), gitBranch));
    }

    // ------------------------------------------------ BuildInformationalVersion

    [Fact]
    public void BuildInformationalVersion_WithBranch()
    {
        Assert.Equal("1.9.2.35-main+e5da1c6-dirty",
            VersionLogic.BuildInformationalVersion("1.9.2.35", "main", "e5da1c6", "-dirty"));
    }

    [Fact]
    public void BuildInformationalVersion_WithoutBranch_OmitsSeparator()
    {
        Assert.Equal("1.9.2.35+e5da1c6",
            VersionLogic.BuildInformationalVersion("1.9.2.35", "", "e5da1c6", ""));
    }

    // ---------------------------------------------------------- StripCredentials

    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("https://gitlab-ci-token:[MASKED]@gitlab.com/group/repo.git", "https://gitlab.com/group/repo.git")]
    [InlineData("https://org@dev.azure.com/org/proj/_git/repo", "https://dev.azure.com/org/proj/_git/repo")]
    [InlineData("ssh://git@host:2222/org/repo.git", "ssh://host:2222/org/repo.git")]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("https://host/a@b/repo.git", "https://host/a@b/repo.git")] // "@" in the path is not userinfo
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")] // scp-like: no password possible
    [InlineData("  https://u:p@host/r.git  ", "https://host/r.git")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripCredentials(string? url, string expected)
    {
        Assert.Equal(expected, VersionLogic.StripCredentials(url));
    }

    // ------------------------------------------------------------------ ToWebUrl

    [Theory]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo")]
    [InlineData("https://github.com/org/repo", "https://github.com/org/repo")]
    [InlineData("https://github.com/org/repo.git/", "https://github.com/org/repo")]
    [InlineData("https://git.example.com:8443/org/repo.git", "https://git.example.com:8443/org/repo")]
    [InlineData("http://git.example.com/org/repo.git", "http://git.example.com/org/repo")]
    [InlineData("https://dev.azure.com/org/proj/_git/repo", "https://dev.azure.com/org/proj/_git/repo")]
    [InlineData("ssh://git@github.com/org/repo.git", "https://github.com/org/repo")]
    [InlineData("ssh://git@git.example.com:2222/org/repo.git", "https://git.example.com/org/repo")]
    [InlineData("git://github.com/org/repo.git", "https://github.com/org/repo")]
    [InlineData("git@github.com:org/repo.git", "https://github.com/org/repo")]
    [InlineData("github.com:org/repo", "https://github.com/org/repo")]
    [InlineData("git@gitea.example.com:org/Repo.GIT", "https://gitea.example.com/org/Repo")]
    [InlineData("file:///srv/git/repo.git", "")]
    [InlineData("/srv/git/repo.git", "")]
    [InlineData("../other/repo.git", "")]
    [InlineData(@"C:\repos\bare.git", "")]
    [InlineData("C:/repos/bare.git", "")]
    [InlineData(@"\\server\share\repo.git", "")]
    [InlineData("repo.git", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToWebUrl(string? url, string expected)
    {
        Assert.Equal(expected, VersionLogic.ToWebUrl(url));
    }

    // -------------------------------------------------------- GetCiRepositoryUrl

    [Fact]
    public void GetCiRepositoryUrl_GitHub_CombinesServerAndRepository()
    {
        var env = Env(("GITHUB_SERVER_URL", "https://github.com/"), ("GITHUB_REPOSITORY", "org/repo"));
        Assert.Equal("https://github.com/org/repo", VersionLogic.GetCiRepositoryUrl("GitHub", env));
    }

    [Fact]
    public void GetCiRepositoryUrl_GitHub_IncompleteVariables_IsNull()
    {
        Assert.Null(VersionLogic.GetCiRepositoryUrl("GitHub", Env(("GITHUB_SERVER_URL", "https://github.com"))));
        Assert.Null(VersionLogic.GetCiRepositoryUrl("GitHub", Env(("GITHUB_REPOSITORY", "org/repo"))));
    }

    [Theory]
    [InlineData("Azure", "BUILD_REPOSITORY_URI")]
    [InlineData("GitLab", "CI_PROJECT_URL")]
    [InlineData("BitBucket", "BITBUCKET_GIT_HTTP_ORIGIN")]
    [InlineData("Jenkins", "GIT_URL")]
    public void GetCiRepositoryUrl_PerBuildServer(string buildServer, string variable)
    {
        Assert.Equal("https://host/repo.git", VersionLogic.GetCiRepositoryUrl(buildServer, Env((variable, "https://host/repo.git"))));
        Assert.Null(VersionLogic.GetCiRepositoryUrl(buildServer, Env()));
    }

    [Fact]
    public void GetCiRepositoryUrl_LocalAndOther_IsNull()
    {
        var env = Env(("GIT_URL", "https://host/repo.git"), ("CI_PROJECT_URL", "https://host/repo.git"));
        Assert.Null(VersionLogic.GetCiRepositoryUrl("Local", env));
        Assert.Null(VersionLogic.GetCiRepositoryUrl("Other", env));
    }

    // ------------------------------------------------------- ResolveRepositoryUrl

    [Fact]
    public void ResolveRepositoryUrl_GitWinsOverCi()
    {
        var env = Env(("CI_PROJECT_URL", "https://gitlab.com/group/repo"));
        Assert.Equal("https://gitlab.com/group/repo.git",
            VersionLogic.ResolveRepositoryUrl("GitLab", env, "https://gitlab-ci-token:TOKEN@gitlab.com/group/repo.git"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void ResolveRepositoryUrl_NoGitRemote_UsesCi(string? gitRemoteUrl)
    {
        var env = Env(("BUILD_REPOSITORY_URI", "https://org@dev.azure.com/org/proj/_git/repo"));
        Assert.Equal("https://dev.azure.com/org/proj/_git/repo",
            VersionLogic.ResolveRepositoryUrl("Azure", env, gitRemoteUrl));
    }

    [Fact]
    public void ResolveRepositoryUrl_Nothing_IsEmpty()
    {
        Assert.Equal(string.Empty, VersionLogic.ResolveRepositoryUrl("Local", Env(), null));
        Assert.Equal(string.Empty, VersionLogic.ResolveRepositoryUrl("GitHub", Env(), ""));
    }

    // ----------------------------------------------------------- ToCSharpLiteral

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData(null, "\"\"")]
    [InlineData("main", "\"main\"")]
    [InlineData(@"C:\repos\bare.git", "\"C:\\\\repos\\\\bare.git\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\r\nb\tc", "\"a\\r\\nb\\tc\"")]
    [InlineData("x\u0001y", "\"x\\u0001y\"")]
    [InlineData("ümlaut/ß", "\"ümlaut/ß\"")]
    public void ToCSharpLiteral(string? value, string expected)
    {
        Assert.Equal(expected, VersionLogic.ToCSharpLiteral(value));
    }
}
