using System;
using System.Text;

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

    /// <summary>
    /// Repository URL from the build server's environment, or null if none applies. Only a
    /// fallback for the rare case that git reports no remote; unlike the branch, the git value
    /// is correct on build servers.
    /// </summary>
    public static string? GetCiRepositoryUrl(string buildServer, Func<string, string?> env)
    {
        switch (buildServer)
        {
            case "GitHub": // also Forgejo/Gitea Actions
                var server = env("GITHUB_SERVER_URL");
                var repo = env("GITHUB_REPOSITORY");
                return string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(repo)
                    ? null
                    : $"{server!.TrimEnd('/')}/{repo!.Trim('/')}";
            case "Azure":
                return FirstNonEmpty(env("BUILD_REPOSITORY_URI"));
            case "GitLab":
                return FirstNonEmpty(env("CI_PROJECT_URL"));
            case "BitBucket":
                return FirstNonEmpty(env("BITBUCKET_GIT_HTTP_ORIGIN"));
            case "Jenkins":
                return FirstNonEmpty(env("GIT_URL"));
            default:
                return null;
        }
    }

    /// <summary>
    /// Final repository URL: git remote first, build server variables second, credentials
    /// always removed. Empty if neither yields a value (a repository without remote is legitimate).
    /// </summary>
    public static string ResolveRepositoryUrl(string buildServer, Func<string, string?> env, string? gitRemoteUrl)
        => StripCredentials(FirstNonEmpty(gitRemoteUrl, GetCiRepositoryUrl(buildServer, env)));

    /// <summary>
    /// Removes the userinfo part (user name and password or token) from a URL with a scheme:
    /// "https://gitlab-ci-token:TOKEN@host/repo.git" becomes "https://host/repo.git". The whole
    /// userinfo goes, not just the password, so no account name ends up in a shipped assembly.
    /// scp-like addresses ("git@host:org/repo.git") cannot carry a password and are returned as
    /// they are.
    /// </summary>
    public static string StripCredentials(string? url)
    {
        var value = url?.Trim() ?? string.Empty;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return value;

        var authorityStart = schemeEnd + 3;
        var pathStart = value.IndexOf('/', authorityStart);
        var authorityEnd = pathStart < 0 ? value.Length : pathStart;
        var authority = value.Substring(authorityStart, authorityEnd - authorityStart);

        var at = authority.LastIndexOf('@');
        if (at < 0)
            return value;

        return value.Substring(0, authorityStart) + authority.Substring(at + 1) + value.Substring(authorityEnd);
    }

    /// <summary>
    /// Best-effort browsable URL for a remote URL (credentials already removed): ssh://, git://
    /// and scp-like addresses become https, http(s) URLs keep their scheme, a trailing ".git"
    /// and "/" are removed. file:// URLs, local paths and anything unparsable yield an empty
    /// string. There are no host-specific rules, so e.g. Azure DevOps SSH URLs
    /// (git@ssh.dev.azure.com:v3/org/project/repo) do not map to the real web URL.
    /// </summary>
    public static string ToWebUrl(string? url)
    {
        var value = url?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return string.Empty;

        string scheme, host, path;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = value.Substring(0, schemeEnd).ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "ssh" && scheme != "git")
                return string.Empty; // file:// and unknown schemes

            var rest = value.Substring(schemeEnd + 3);
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest.Substring(0, slash);
            path = slash < 0 ? string.Empty : rest.Substring(slash);

            var at = authority.LastIndexOf('@');
            if (at >= 0)
                authority = authority.Substring(at + 1);

            if (scheme == "ssh" || scheme == "git")
            {
                scheme = "https";
                authority = StripPort(authority); // the ssh/git port is not the https port
            }
            host = authority;
        }
        else
        {
            // scp-like syntax: [user@]host:path. Backslashes, a leading "/" or "." and a single
            // drive letter before the colon indicate a local path instead.
            if (value.IndexOf('\\') >= 0 || value[0] == '/' || value[0] == '.')
                return string.Empty;

            var colon = value.IndexOf(':');
            if (colon <= 0 || colon == value.Length - 1)
                return string.Empty;

            host = value.Substring(0, colon);
            var at = host.LastIndexOf('@');
            if (at >= 0)
                host = host.Substring(at + 1);
            if (host.Length == 1 && char.IsLetter(host[0]))
                return string.Empty; // "C:/repos/bare.git"

            scheme = "https";
            path = "/" + value.Substring(colon + 1).TrimStart('/');
        }

        if (host.Length == 0)
            return string.Empty;

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - 4);

        return $"{scheme}://{host}{path}";
    }

    /// <summary>
    /// Renders a value as a C# string literal including the quotes. Remote URLs may contain
    /// backslashes (local bare repositories), branch names may contain quotes.
    /// </summary>
    public static string ToCSharpLiteral(string? value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || c == '\u007f')
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string StripPort(string authority)
    {
        var colon = authority.LastIndexOf(':');
        // "[::1]:22" has a port, "[::1]" has none
        return colon > 0 && authority.IndexOf(']') < colon ? authority.Substring(0, colon) : authority;
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
