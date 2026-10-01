# NuGitVersion

Automatic build versioning from Git — simple and transparent.

NuGitVersion combines a manually maintained base version (`Major.Minor.Patch` from a
`nugitversion.json` in your project) with information from the underlying Git repository
(commit count, commit hash, branch, dirty flag) and stamps the result into your assembly
at build time. It also generates a static `NuGitAssemblyInfo` class so the same
information is available at runtime.

## Installation

```
dotnet add package NuGitVersion
```

On the first local build, a `nugitversion.json` is created in the project directory
(initial version `0.1.0`). Commit it and edit it to control the base version:

```json
{
  "Major": 0,
  "Minor": 1,
  "Patch": 0
}
```

## How it works

Before compilation, an MSBuild target runs the bundled `NuGitVersion.Tool`, which reads
the Git information of the repository and writes two files into the `obj` directory:

- `nugitinfo.json` — the collected version and Git information
- `NuGitAssemblyInfo.g.cs` — generated source that is compiled into your assembly

The resulting version attributes look like this:

| Attribute | Format | Example |
|---|---|---|
| `AssemblyVersion` | `Major.Minor.Patch.0` | `1.2.3.0` |
| `AssemblyFileVersion` | `Major.Minor.Patch.CommitCount` | `1.2.3.42` |
| `AssemblyInformationalVersion` | `FileVersion[-Branch]+Hash[-dirty]` | `1.2.3.42-main+a1b2c3d-dirty` |

The `-Branch` part is omitted when no branch name could be determined (see below).

### Runtime access

The generated static class exposes everything from code:

```csharp
Console.WriteLine($"{NuGitAssemblyInfo.Major}.{NuGitAssemblyInfo.Minor}.{NuGitAssemblyInfo.Patch}");
Console.WriteLine(NuGitAssemblyInfo.CommitHash);   // "a1b2c3d"
Console.WriteLine(NuGitAssemblyInfo.Branch);       // "main"
Console.WriteLine(NuGitAssemblyInfo.CommitCount);  // 42
Console.WriteLine(NuGitAssemblyInfo.IsDirty);      // false
Console.WriteLine(NuGitAssemblyInfo.BuildServer);  // "Local", "GitHub", "Azure", ...
```

Detected build servers: Azure Pipelines, GitHub Actions, Bitbucket Pipelines, GitLab CI,
Jenkins (plus a generic `CI` fallback).

## Continuous integration

### Branch name

CI systems usually check out a commit rather than a branch, so Git reports `HEAD` instead
of a branch name. On a detected build server the tool therefore reads the branch from the
server's environment first and only falls back to Git if no variable is set:

| Build server | Variables (first match wins) |
|---|---|
| GitHub Actions (also Forgejo/Gitea Actions) | `GITHUB_HEAD_REF` (pull requests), `GITHUB_REF_NAME` |
| Azure Pipelines | `SYSTEM_PULLREQUEST_SOURCEBRANCH`, `BUILD_SOURCEBRANCHNAME` |
| GitLab CI | `CI_MERGE_REQUEST_SOURCE_BRANCH_NAME`, `CI_COMMIT_REF_NAME` |
| Bitbucket Pipelines | `BITBUCKET_BRANCH` |
| Jenkins | `BRANCH_NAME`, `GIT_BRANCH` (without `origin/`) |

The literal `HEAD` is never used as a branch name. If neither a variable nor Git yields a
branch, the branch is left empty, the tool emits warning `NGV002`, and the informational
version is written without the `-Branch` part.

### Missing `nugitversion.json`

On a local build (no build server detected) a missing version file is created with
`0.1.0`. On a build server a missing file is almost always a mistake (not committed, wrong
path), so the build fails with error `NGV001` instead of silently publishing version
`0.1.0`. To override the decision in either direction, set the MSBuild property
`NuGitVersionFailIfMissing` in the project file or on the command line:

```xml
<PropertyGroup>
  <NuGitVersionFailIfMissing>false</NuGitVersionFailIfMissing>
</PropertyGroup>
```

### Location of the version file

By default the file is `nugitversion.json` next to the project file. The MSBuild property
`NuGitVersionFile` overrides the full path, e.g. to share one file between several
projects:

```xml
<PropertyGroup>
  <NuGitVersionFile>$(MSBuildThisFileDirectory)..\nugitversion.json</NuGitVersionFile>
</PropertyGroup>
```

## Requirements

- `git` must be installed and available in `PATH`
- .NET runtime 8.0 or later must be available on the build machine
  (the tool rolls forward to any newer major version)

## Caveat: shallow clones

The commit count is computed from the commits that are actually present in the local
clone. If your CI server performs a shallow clone, the count stays constant and does not
increase across builds (the tool emits warning `NGV003` in this case). Configure a full clone:

- **GitHub Actions**: `fetch-depth: 0` on `actions/checkout`
- **Bitbucket Pipelines**: add to your `bitbucket-pipelines.yml`:

  ```yaml
  clone:
    depth: full
  ```

## Credits

Developed at [Wassermann Automation GmbH](https://www.wassermann-group.com) by
[Arno Dewald](https://github.com/add42) and [Jan Helmerichs](https://github.com/Lice2000).

## License

[MIT](LICENSE)
