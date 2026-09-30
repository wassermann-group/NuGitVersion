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

On first build, a `nugitversion.json` is created in the project directory (initial
version `0.1.0`). Edit it to control the base version:

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
| `AssemblyInformationalVersion` | `FileVersion-Branch+Hash[-dirty]` | `1.2.3.42-main+a1b2c3d-dirty` |

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

## Requirements

- `git` must be installed and available in `PATH`
- .NET runtime 8.0 or later must be available on the build machine
  (the tool rolls forward to any newer major version)

## Caveat: shallow clones

The commit count is computed from the commits that are actually present in the local
clone. If your CI server performs a shallow clone, the count stays constant and does not
increase across builds (the tool logs a warning in this case). Configure a full clone:

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
