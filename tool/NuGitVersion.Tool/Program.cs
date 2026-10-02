using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NuGitVersion.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = VersionLogic.ParseArgs(args, out var argError);
            if (options is null)
            {
                Error("NGV005", argError ?? "Invalid arguments");
                return 2;
            }

            Log("=== NuGitVersion.Tool started ===");

            // Paths may arrive with backslashes on Linux/macOS (see VersionLogic.NormalizePath)
            var isWindows = OperatingSystem.IsWindows();
            var outputDir = VersionLogic.NormalizePath(options.OutputDir, isWindows, Directory.Exists);
            var versionFile = VersionLogic.NormalizePath(options.VersionFile, isWindows, File.Exists);
            if (!ReferenceEquals(outputDir, options.OutputDir))
                Log($"Output directory normalized: {options.OutputDir} -> {outputDir}");
            if (!ReferenceEquals(versionFile, options.VersionFile))
                Log($"Version file path normalized: {options.VersionFile} -> {versionFile}");

            Log($"Output Directory: {outputDir}");
            Log($"Version File:     {versionFile}");

            // 1. Detect build server (needed to decide how to treat a missing version file)
            var env = new Func<string, string?>(Environment.GetEnvironmentVariable);
            var buildServer = VersionLogic.DetectBuildServer(env);
            Log($"BuildServer: {buildServer}");

            // 2. Ensure nugitversion.json exists
            if (!File.Exists(versionFile))
            {
                if (VersionLogic.ShouldFailIfMissing(buildServer, options.FailIfMissing))
                {
                    Error("NGV001", $"{VersionLogic.DefaultVersionFileName} not found at '{versionFile}'. "
                        + "Commit the file to the repository or set the MSBuild property "
                        + "NuGitVersionFailIfMissing=false to create a default version on this build server.");
                    return 1;
                }

                Log($"{VersionLogic.DefaultVersionFileName} not found, creating default version...");
                var defaultJson = JsonSerializer.Serialize(
                    new VersionFile { Major = 0, Minor = 1, Patch = 0 },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(versionFile, defaultJson, new UTF8Encoding(false));
            }

            var versionJson = File.ReadAllText(versionFile);
            var version = JsonSerializer.Deserialize<VersionFile>(versionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException($"Failed to parse {VersionLogic.DefaultVersionFileName}!");

            if (version.Major is null || version.Minor is null || version.Patch is null)
                throw new InvalidOperationException($"{VersionLogic.DefaultVersionFileName} must contain Major, Minor and Patch!");

            Log($"Version: {version.Major}.{version.Minor}.{version.Patch}");

            // 3. Check git availability
            if (!TryRunGit("--version", out _))
                throw new InvalidOperationException("Git is not installed or not in PATH");

            // 4. Collect git information
            if (!TryRunGit("rev-parse --show-toplevel", out var repoRoot) || string.IsNullOrWhiteSpace(repoRoot))
                throw new InvalidOperationException("No git repository found!");

            TryRunGit("rev-parse --short HEAD", out var gitHash);
            TryRunGit("rev-parse HEAD", out var gitHashFull);
            TryRunGit("show -s --format=%ci HEAD", out var commitTime);
            TryRunGit("rev-parse --abbrev-ref HEAD", out var gitBranch);
            TryRunGit("rev-list --count HEAD", out var commitCountStr);
            TryRunGit("status --porcelain", out var porcelain);

            // Remote URL: "origin" first, otherwise the first remote. No remote is fine locally.
            if (!TryRunGit("remote get-url origin", out var remoteUrl) || remoteUrl.Length == 0)
            {
                remoteUrl = string.Empty;
                if (TryRunGit("remote", out var remotes) && remotes.Length > 0)
                {
                    var firstRemote = remotes.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (!TryRunGit($"remote get-url {firstRemote}", out remoteUrl))
                        remoteUrl = string.Empty;
                }
            }

            if (TryRunGit("rev-parse --is-shallow-repository", out var isShallow)
                && string.Equals(isShallow, "true", StringComparison.OrdinalIgnoreCase))
            {
                Warn("NGV003", "Shallow clone detected - the commit count only reflects the cloned "
                    + "commits and will not increase across builds. Configure your CI to clone the "
                    + "full history (e.g. fetch-depth: 0 on GitHub Actions, 'clone: depth: full' on Bitbucket).");
            }

            // 5. Branch: CI variable first, git second, never the literal "HEAD"
            var branch = VersionLogic.ResolveBranch(buildServer, env, gitBranch);
            if (branch.Length == 0)
            {
                Warn("NGV002", "Detached HEAD and no branch variable of the build server found; "
                    + "the branch is left empty.");
            }

            var uncommittedCount = string.IsNullOrWhiteSpace(porcelain)
                ? 0
                : porcelain.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

            var isDirty = uncommittedCount > 0;
            var dirtySuffix = isDirty ? "-dirty" : string.Empty;
            var commitCount = int.TryParse(commitCountStr, out var cc) ? cc : 0;

            // Repository URL: git first, build server variables as fallback, credentials removed
            var repositoryUrl = VersionLogic.ResolveRepositoryUrl(buildServer, env, remoteUrl);
            var repositoryWebUrl = VersionLogic.ToWebUrl(repositoryUrl);

            Log($"Git Hash:           {gitHash}");
            Log($"Git Hash (full):    {gitHashFull}");
            Log($"Branch:             {branch}");
            Log($"CommitCount:        {commitCount}");
            Log($"CommitTime:         {commitTime}");
            Log($"UncommittedChanges: {uncommittedCount}");
            Log($"Dirty:              {dirtySuffix}");
            Log($"RepositoryUrl:      {repositoryUrl}");
            Log($"RepositoryWebUrl:   {repositoryWebUrl}");

            // 6. Ensure output directory exists
            Directory.CreateDirectory(outputDir);

            // 7. Write nugitinfo.json
            var info = new Dictionary<string, object?>
            {
                ["Major"] = version.Major,
                ["Minor"] = version.Minor,
                ["Patch"] = version.Patch,
                ["CommitHash"] = gitHash,
                ["CommitHashFull"] = gitHashFull,
                ["CommitTime"] = commitTime,
                ["Branch"] = branch,
                ["IsDirty"] = dirtySuffix,
                ["TotalCommits"] = commitCount,
                ["UncommittedChanges"] = uncommittedCount,
                ["BuildServer"] = buildServer,
                ["RepositoryUrl"] = repositoryUrl,
                ["RepositoryWebUrl"] = repositoryWebUrl,
            };
            var infoPath = Path.Combine(outputDir, "nugitinfo.json");
            File.WriteAllText(infoPath,
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            Log($"nugitinfo.json written: {infoPath}");

            // 8. Generate NuGitAssemblyInfo.g.cs
            var assemblyVersion = $"{version.Major}.{version.Minor}.{version.Patch}.0";
            var fileVersion = $"{version.Major}.{version.Minor}.{version.Patch}.{commitCount}";
            var infoVersion = VersionLogic.BuildInformationalVersion(fileVersion, branch, gitHash, dirtySuffix);

            // String values are rendered as escaped literals (Lit): URLs may contain backslashes,
            // branch names may contain quotes.
            var cs = $@"// <auto-generated/>
using System.Reflection;

[assembly: AssemblyVersion({Lit(assemblyVersion)})]
[assembly: AssemblyFileVersion({Lit(fileVersion)})]
[assembly: AssemblyInformationalVersion({Lit(infoVersion)})]

public static partial class NuGitAssemblyInfo
{{
    public const int Major = {version.Major};
    public const int Minor = {version.Minor};
    public const int Patch = {version.Patch};
    public const string CommitHash = {Lit(gitHash)};
    public const string CommitHashFull = {Lit(gitHashFull)};
    public const string CommitTime = {Lit(commitTime)};
    public const string Branch = {Lit(branch)};
    public const int CommitCount = {commitCount};
    public const bool IsDirty = {(isDirty ? "true" : "false")};
    public const int UncommittedChanges = {uncommittedCount};
    public const string BuildServer = {Lit(buildServer)};
    public const string RepositoryUrl = {Lit(repositoryUrl)};
    public const string RepositoryWebUrl = {Lit(repositoryWebUrl)};
}}
";
            var csPath = Path.Combine(outputDir, "NuGitAssemblyInfo.g.cs");
            File.WriteAllText(csPath, cs, new UTF8Encoding(false));
            Log($"NuGitAssemblyInfo.g.cs generated: {csPath}");

            Log("=== Completed successfully ===");
            return 0;
        }
        catch (Exception ex)
        {
            Error("NGV004", ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static bool TryRunGit(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            output = stdout.Trim();

            if (p.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log($"git {arguments} -> {stderr.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"git {arguments} -> Exception: {ex.Message}");
            return false;
        }
    }

    private static string Lit(string value) => VersionLogic.ToCSharpLiteral(value);

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    // Warnings and errors use MSBuild's canonical format ("warning CODE: text") without the
    // timestamp prefix, so the Exec task reports them as real build warnings/errors.
    private static void Warn(string code, string message)
    {
        Console.WriteLine($"warning {code}: {message}");
    }

    private static void Error(string code, string message)
    {
        Console.Error.WriteLine($"error {code}: {message}");
    }

    private sealed class VersionFile
    {
        public int? Major { get; set; }
        public int? Minor { get; set; }
        public int? Patch { get; set; }
    }
}
