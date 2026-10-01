using System;

namespace NuGitVersion.Tool;

/// <summary>
/// Pure decision logic of the tool, kept free of I/O so it can be unit tested.
/// Environment access is injected as <c>Func&lt;string, string?&gt;</c>.
/// </summary>
internal static class VersionLogic
{
    public const string DefaultVersionFileName = "nugitversion.json";

    /// <summary>Parsed command line.</summary>
    internal sealed record ToolOptions(string OutputDir, string VersionFile, bool? FailIfMissing);

    /// <summary>
    /// Parses <c>&lt;OutputDir&gt; [VersionFile] [--fail-if-missing=true|false]</c>.
    /// Returns null and sets <paramref name="error"/> when the arguments are invalid.
    /// </summary>
    public static ToolOptions? ParseArgs(string[] args, out string? error)
    {
        error = null;
        string? outputDir = null;
        string? versionFile = null;
        bool? failIfMissing = null;

        foreach (var arg in args)
        {
            const string failOption = "--fail-if-missing";
            if (arg.StartsWith(failOption, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Length > failOption.Length && arg[failOption.Length] == '='
                    ? arg.Substring(failOption.Length + 1)
                    : "true";
                if (!bool.TryParse(value, out var parsed))
                {
                    error = $"Invalid value for {failOption}: '{value}' (expected true or false)";
                    return null;
                }
                failIfMissing = parsed;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option: {arg}";
                return null;
            }
            else if (outputDir is null)
            {
                outputDir = arg;
            }
            else if (versionFile is null)
            {
                versionFile = arg;
            }
            else
            {
                error = $"Unexpected argument: {arg}";
                return null;
            }
        }

        if (outputDir is null)
        {
            error = "Usage: NuGitVersion.Tool <OutputDir> [VersionFile] [--fail-if-missing=true|false]";
            return null;
        }

        return new ToolOptions(outputDir, versionFile ?? DefaultVersionFileName, failIfMissing);
    }

    /// <summary>
    /// Defense in depth for paths handed over by MSBuild on Linux/macOS. MSBuild normalizes
    /// backslashes in most places but not inside an Exec command string, so a path built as
    /// "$(MSBuildProjectDirectory)\nugitversion.json" arrives verbatim. If the path does not
    /// exist as given and contains backslashes, they are replaced by forward slashes.
    /// On Windows the path is always returned unchanged.
    /// </summary>
    public static string NormalizePath(string path, bool isWindows, Func<string, bool> exists)
    {
        if (isWindows || path.IndexOf('\\') < 0 || exists(path))
            return path;
        return path.Replace('\\', '/');
    }

    public static string DetectBuildServer(Func<string, string?> env)
    {
        if (string.Equals(env("TF_BUILD"), "True", StringComparison.OrdinalIgnoreCase))
            return "Azure";
        if (string.Equals(env("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return "GitHub";
        if (!string.IsNullOrEmpty(env("BITBUCKET_BUILD_NUMBER")))
            return "BitBucket";
        if (string.Equals(env("GITLAB_CI"), "true", StringComparison.OrdinalIgnoreCase))
            return "GitLab";
        if (!string.IsNullOrEmpty(env("JENKINS_URL")))
            return "Jenkins";
        if (string.Equals(env("CI"), "true", StringComparison.OrdinalIgnoreCase))
            return "Other";
        return "Local";
    }

    /// <summary>
    /// A missing version file is created with a default version on local builds. On a build
    /// server it is almost always a mistake (file not committed, wrong path), so the build fails.
    /// An explicit override (MSBuild property NuGitVersionFailIfMissing) wins in both cases.
    /// </summary>
    public static bool ShouldFailIfMissing(string buildServer, bool? overrideValue)
        => overrideValue ?? !string.Equals(buildServer, "Local", StringComparison.Ordinal);

    /// <summary>
    /// Branch name from the build server's environment variables, or null if none applies.
    /// CI systems check out a commit, not a branch, so git reports "HEAD" there. For pull
    /// requests the source branch is preferred over the synthetic merge ref.
    /// </summary>
    public static string? GetCiBranch(string buildServer, Func<string, string?> env)
    {
        switch (buildServer)
        {
            case "GitHub": // also Forgejo/Gitea Actions, which set the same variables
                return FirstNonEmpty(env("GITHUB_HEAD_REF"), env("GITHUB_REF_NAME"));
            case "Azure":
                return FirstNonEmpty(StripPrefix(env("SYSTEM_PULLREQUEST_SOURCEBRANCH"), "refs/heads/"),
                                     env("BUILD_SOURCEBRANCHNAME"));
            case "GitLab":
                return FirstNonEmpty(env("CI_MERGE_REQUEST_SOURCE_BRANCH_NAME"), env("CI_COMMIT_REF_NAME"));
            case "BitBucket":
                return FirstNonEmpty(env("BITBUCKET_BRANCH"));
            case "Jenkins":
                return FirstNonEmpty(env("BRANCH_NAME"), StripPrefix(env("GIT_BRANCH"), "origin/"));
            default:
                return null;
        }
    }

    /// <summary>
    /// Final branch name: CI variable first, then git. The literal "HEAD" (detached HEAD)
    /// is never passed through; the result is empty in that case.
    /// </summary>
    public static string ResolveBranch(string buildServer, Func<string, string?> env, string? gitBranch)
    {
        var ci = GetCiBranch(buildServer, env);
        if (!string.IsNullOrWhiteSpace(ci))
            return ci!.Trim();

        var git = gitBranch?.Trim() ?? string.Empty;
        return string.Equals(git, "HEAD", StringComparison.Ordinal) ? string.Empty : git;
    }

    /// <summary>FileVersion[-Branch]+Hash[-dirty]</summary>
    public static string BuildInformationalVersion(string fileVersion, string branch, string gitHash, string dirtySuffix)
    {
        var branchPart = string.IsNullOrEmpty(branch) ? string.Empty : $"-{branch}";
        return $"{fileVersion}{branchPart}+{gitHash}{dirtySuffix}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    private static string? StripPrefix(string? value, string prefix)
        => value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
            ? value.Substring(prefix.Length)
            : value;
}
