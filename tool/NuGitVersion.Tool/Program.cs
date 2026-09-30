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
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: NuGitVersion.Tool <OutputDir> [VersionFile]");
                return 2;
            }

            var outputDir = args[0];
            var versionFile = args.Length >= 2 ? args[1] : "nugitversion.json";

            Log("=== NuGitVersion.Tool started ===");
            Log($"Output Directory: {outputDir}");
            Log($"Version File:     {versionFile}");

            // 1. Ensure nugitversion.json exists
            if (!File.Exists(versionFile))
            {
                Log("nugitversion.json not found, creating default version...");
                var defaultJson = JsonSerializer.Serialize(
                    new VersionFile { Major = 0, Minor = 1, Patch = 0 },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(versionFile, defaultJson, new UTF8Encoding(false));
            }

            var versionJson = File.ReadAllText(versionFile);
            var version = JsonSerializer.Deserialize<VersionFile>(versionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Failed to parse nugitversion.json!");

            if (version.Major is null || version.Minor is null || version.Patch is null)
                throw new InvalidOperationException("nugitversion.json must contain Major, Minor and Patch!");

            Log($"Version: {version.Major}.{version.Minor}.{version.Patch}");

            // 2. Check git availability
            if (!TryRunGit("--version", out _))
                throw new InvalidOperationException("Git is not installed or not in PATH");

            // 3. Detect build server
            var buildServer = DetectBuildServer();
            Log($"BuildServer: {buildServer}");

            // 4. Collect git information
            if (!TryRunGit("rev-parse --show-toplevel", out var repoRoot) || string.IsNullOrWhiteSpace(repoRoot))
                throw new InvalidOperationException("No git repository found!");

            TryRunGit("rev-parse --short HEAD", out var gitHash);
            TryRunGit("show -s --format=%ci HEAD", out var commitTime);
            TryRunGit("rev-parse --abbrev-ref HEAD", out var branch);
            TryRunGit("rev-list --count HEAD", out var commitCountStr);
            TryRunGit("status --porcelain", out var porcelain);

            if (TryRunGit("rev-parse --is-shallow-repository", out var isShallow)
                && string.Equals(isShallow, "true", StringComparison.OrdinalIgnoreCase))
            {
                Log("WARNING: Shallow clone detected - the commit count only reflects the cloned "
                    + "commits and will not increase across builds. Configure your CI to clone the "
                    + "full history (e.g. fetch-depth: 0 on GitHub Actions, 'clone: depth: full' on Bitbucket).");
            }

            var uncommittedCount = string.IsNullOrWhiteSpace(porcelain)
                ? 0
                : porcelain.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

            var isDirty = uncommittedCount > 0;
            var dirtySuffix = isDirty ? "-dirty" : string.Empty;
            var commitCount = int.TryParse(commitCountStr, out var cc) ? cc : 0;

            Log($"Git Hash:           {gitHash}");
            Log($"Branch:             {branch}");
            Log($"CommitCount:        {commitCount}");
            Log($"CommitTime:         {commitTime}");
            Log($"UncommittedChanges: {uncommittedCount}");
            Log($"Dirty:              {dirtySuffix}");

            // 5. Ensure output directory exists
            Directory.CreateDirectory(outputDir);

            // 6. Write nugitinfo.json
            var info = new Dictionary<string, object?>
            {
                ["Major"] = version.Major,
                ["Minor"] = version.Minor,
                ["Patch"] = version.Patch,
                ["CommitHash"] = gitHash,
                ["CommitTime"] = commitTime,
                ["Branch"] = branch,
                ["IsDirty"] = dirtySuffix,
                ["TotalCommits"] = commitCount,
                ["UncommittedChanges"] = uncommittedCount,
                ["BuildServer"] = buildServer,
            };
            var infoPath = Path.Combine(outputDir, "nugitinfo.json");
            File.WriteAllText(infoPath,
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            Log($"nugitinfo.json written: {infoPath}");

            // 7. Generate NuGitAssemblyInfo.g.cs
            var assemblyVersion = $"{version.Major}.{version.Minor}.{version.Patch}.0";
            var fileVersion = $"{version.Major}.{version.Minor}.{version.Patch}.{commitCount}";
            var infoVersion = $"{fileVersion}-{branch}+{gitHash}{dirtySuffix}";

            var cs = $@"// <auto-generated/>
using System.Reflection;

[assembly: AssemblyVersion(""{assemblyVersion}"")]
[assembly: AssemblyFileVersion(""{fileVersion}"")]
[assembly: AssemblyInformationalVersion(""{infoVersion}"")]

public static partial class NuGitAssemblyInfo
{{
    public const int Major = {version.Major};
    public const int Minor = {version.Minor};
    public const int Patch = {version.Patch};
    public const string CommitHash = ""{gitHash}"";
    public const string CommitTime = ""{commitTime}"";
    public const string Branch = ""{branch}"";
    public const int CommitCount = {commitCount};
    public const bool IsDirty = {(isDirty ? "true" : "false")};
    public const int UncommittedChanges = {uncommittedCount};
    public const string BuildServer = ""{buildServer}"";
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
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string DetectBuildServer()
    {
        static string? E(string name) => Environment.GetEnvironmentVariable(name);

        if (string.Equals(E("TF_BUILD"), "True", StringComparison.OrdinalIgnoreCase))
            return "Azure";
        if (string.Equals(E("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return "GitHub";
        if (!string.IsNullOrEmpty(E("BITBUCKET_BUILD_NUMBER")))
            return "BitBucket";
        if (string.Equals(E("GITLAB_CI"), "true", StringComparison.OrdinalIgnoreCase))
            return "GitLab";
        if (!string.IsNullOrEmpty(E("JENKINS_URL")))
            return "Jenkins";
        if (string.Equals(E("CI"), "true", StringComparison.OrdinalIgnoreCase))
            return "Other";
        return "Local";
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

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private sealed class VersionFile
    {
        public int? Major { get; set; }
        public int? Minor { get; set; }
        public int? Patch { get; set; }
    }
}
