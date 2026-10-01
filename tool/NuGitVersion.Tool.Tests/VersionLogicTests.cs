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
}
