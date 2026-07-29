using System.IO.Compression;
using System.Xml.Linq;

namespace SFMCSDK.Net.Android.PackageTests;

/// <summary>What one package is expected to be.</summary>
/// <param name="Id">The NuGet package id, e.g. <c>SFMCSDK.Net.Android</c>.</param>
/// <param name="Artifact">The Maven artifactId the package binds.</param>
/// <param name="DependsOn">The ids of the sibling packages it must depend on.</param>
public sealed record PackageSpec(string Id, string Artifact, string[] DependsOn);

/// <summary>
/// Locates the packed .nupkg files and describes what each one is supposed to contain.
/// </summary>
public static class Packages
{
    /// <summary>
    /// Every package this repository builds, read from build/packages.tsv.
    /// </summary>
    /// <remarks>
    /// Read from the manifest rather than restated here, because the manifest is what the build
    /// script packs and what the release workflow lists. A copy in the tests would let the two
    /// drift and still pass. What is <em>not</em> read from the manifest is the expectation for
    /// third-party dependencies and embedded .aars below - those are stated independently on
    /// purpose, so the test disagrees with the projects rather than echoing them.
    /// </remarks>
    public static readonly PackageSpec[] All = LoadManifest();

    /// <summary>Target frameworks every package carries a binding assembly for.</summary>
    public static readonly string[] ExpectedTargetFrameworks =
    [
        "net8.0-android34.0", "net9.0-android35.0", "net10.0-android36.0",
    ];

    /// <summary>
    /// The native .aars each package must ship for every target framework, and their minimum
    /// plausible sizes. sfmcsdk is ~400 KB and common-internal ~12 KB; an .aar with no
    /// classes.jar is a couple of kilobytes, which is what the silent net8 empty-package failure
    /// produces.
    /// </summary>
    public static readonly (string Package, string Artifact, long MinimumBytes)[] EmbeddedAars =
    [
        ("SFMCSDK.Net.Android", "sfmcsdk", 100_000),
        ("SFMCSDK.Net.Android", "common-internal", 5_000),
    ];

    /// <summary>NuGet packages every binding must depend on, whatever else it declares.</summary>
    public static readonly string[] UniversalDependencies = ["Xamarin.Kotlin.StdLib"];

    /// <summary>
    /// Dependencies whose version legitimately differs per target framework, and what each group
    /// must say. Lifecycle.Process 2.10.0 - the sfmcsdk .pom's version - ships no net8 asset, so
    /// the net8 head pins the last version that does; see Directory.Build.props. Lifecycle.Common
    /// is pinned directly on net8 to resolve a conflict between Work.Runtime 2.11.0 and
    /// GooglePlayServices.Basement 118.9.0 transitive bounds. Asserted here so a merge or an edit
    /// that flattens the split back to one version fails a test instead of a consumer's restore.
    /// </summary>
    public static readonly (string Dependency, string Tfm, string Version)[] PerTfmDependencyVersions =
    [
        ("Xamarin.AndroidX.Lifecycle.Common",  "net8.0-android34.0", "2.9.4"),
        ("Xamarin.AndroidX.Lifecycle.Process", "net8.0-android34.0", "2.9.4"),
        ("Xamarin.AndroidX.Lifecycle.Process", "net9.0-android35.0", "2.10.0"),
        ("Xamarin.AndroidX.Lifecycle.Process", "net10.0-android36.0", "2.10.0"),
    ];

    /// <summary>xunit member data: one row per package.</summary>
    public static TheoryData<string> Ids
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var package in All)
            {
                data.Add(package.Id);
            }

            return data;
        }
    }

    public static PackageSpec Spec(string id) => All.Single(package => package.Id == id);

    /// <summary>
    /// The directory packages are read from. Overridable so the tests can run against a directory
    /// other than the repository's own artifacts/ - a CI job that downloads them, for instance.
    /// </summary>
    public static string ArtifactsDirectory =>
        Environment.GetEnvironmentVariable("SFMC_ARTIFACTS_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(RepositoryRoot, "artifacts");

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private static PackageSpec[] LoadManifest()
    {
        var path = Path.Combine(RepositoryRoot, "build", "packages.tsv");
        var specs = new List<PackageSpec>();

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length < 3)
            {
                continue;
            }

            var dependsOn = columns[2].Trim() is "-" or ""
                ? []
                : columns[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            specs.Add(new PackageSpec(columns[0].Trim(), columns[1].Trim(), dependsOn));
        }

        if (specs.Count == 0)
        {
            throw new InvalidOperationException($"No packages were read from {path}.");
        }

        return [.. specs];
    }

    public static ZipArchive OpenPackage(string id, string extension = ".nupkg")
    {
        var matches = Directory.GetFiles(ArtifactsDirectory, $"{id}.*{extension}");

        // Matching on the id prefix alone would also match a longer id that starts with it, so
        // the next character after the id must look like the start of a version.
        var package = matches.SingleOrDefault(path =>
            Path.GetFileName(path).StartsWith($"{id}.", StringComparison.Ordinal) &&
            char.IsDigit(Path.GetFileName(path)[id.Length + 1]));

        if (package is null)
        {
            throw new FileNotFoundException(
                $"No {id}{extension} in {ArtifactsDirectory}. Run ./build/BuildNugets.sh first.");
        }

        return ZipFile.OpenRead(package);
    }

    /// <summary>Reads an entry into a seekable stream, so it can be opened as an archive.</summary>
    public static MemoryStream ReadEntry(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidOperationException($"Archive has no entry '{path}'.");

        var buffer = new MemoryStream();
        using (var stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        buffer.Position = 0;
        return buffer;
    }

    public static XDocument ReadNuspec(ZipArchive package, string id)
    {
        using var stream = ReadEntry(package, $"{id}.nuspec");
        return XDocument.Load(stream);
    }
}
