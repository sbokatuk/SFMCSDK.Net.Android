using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SFMCSDK.Net.Android.PackageTests;

/// <summary>
/// Asserts the shape of the produced NuGet packages. These run against the packed .nupkg rather
/// than the build output, so they catch packaging regressions the compiler cannot see.
/// </summary>
public class PackageLayoutTests
{
    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Package_carries_a_binding_assembly_for_every_target_framework(string id)
    {
        using var package = Packages.OpenPackage(id);

        foreach (var tfm in Packages.ExpectedTargetFrameworks)
        {
            var expected = $"lib/{tfm}/{id}.dll";
            Assert.True(
                package.GetEntry(expected) is not null,
                $"{id} is missing '{expected}'.");
        }
    }

    [Fact]
    public void Package_ships_every_native_aar_for_every_target_framework()
    {
        foreach (var (packageId, artifact, minimumBytes) in Packages.EmbeddedAars)
        {
            using var package = Packages.OpenPackage(packageId);

            foreach (var tfm in Packages.ExpectedTargetFrameworks)
            {
                var expected = $"lib/{tfm}/{artifact}-";
                var aar = package.Entries.SingleOrDefault(entry =>
                    entry.FullName.StartsWith(expected, StringComparison.Ordinal) &&
                    entry.FullName.EndsWith(".aar", StringComparison.Ordinal));

                // This is the check that catches the silent-empty-package failure.
                // @(AndroidMavenLibrary) does not exist in the .NET Android SDK 34, so on net8 the
                // item is ignored without a word and the package would be produced with a 5 KB
                // assembly and no .aar at all.
                Assert.True(
                    aar is not null,
                    $"{packageId} ships no {artifact} .aar for {tfm}. " +
                    "Was the artifact resolved on this target framework?");

                Assert.True(
                    aar!.Length > minimumBytes,
                    $"'{aar.FullName}' is only {aar.Length} bytes, which is too small to be the real module.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Binding_assembly_contains_public_salesforce_types(string id)
    {
        using var package = Packages.OpenPackage(id);

        foreach (var tfm in Packages.ExpectedTargetFrameworks)
        {
            using var assembly = Packages.ReadEntry(package, $"lib/{tfm}/{id}.dll");
            using var reader = new PEReader(assembly);
            var metadata = reader.GetMetadataReader();

            // Counted by namespace rather than by raw type count: .NET Android always emits its
            // own Resource designer class, so "no types at all" is never true even for a package
            // that binds nothing.
            var salesforceTypes = metadata.TypeDefinitions
                .Select(metadata.GetTypeDefinition)
                .Count(type =>
                    type.Attributes.HasFlag(TypeAttributes.Public) &&
                    metadata.GetString(type.Namespace).StartsWith("Com.Salesforce", StringComparison.Ordinal));

            Assert.True(
                salesforceTypes > 0,
                $"{id}'s {tfm} assembly declares no public Com.Salesforce types. " +
                "The binding generator produced nothing - most likely the .aar never reached it.");
        }
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Package_declares_its_dependencies_for_every_target_framework(string id)
    {
        var spec = Packages.Spec(id);
        var expectedSiblings = spec.DependsOn.OrderBy(dep => dep, StringComparer.Ordinal).ToList();

        using var package = Packages.OpenPackage(id);
        var nuspec = Packages.ReadNuspec(package, id);

        var groups = nuspec.Descendants()
            .Where(element => element.Name.LocalName == "group")
            .ToList();

        Assert.Equal(
            Packages.ExpectedTargetFrameworks.OrderBy(tfm => tfm, StringComparer.Ordinal),
            groups.Select(group => group.Attribute("targetFramework")?.Value ?? string.Empty)
                  .OrderBy(tfm => tfm, StringComparer.Ordinal));

        // Asserted per group, not just once: the net10 group is grafted in by merge-packages.py
        // from a separately built package, and an empty or stale group there would leave net10
        // consumers restoring a package whose dependencies never come with it.
        foreach (var group in groups)
        {
            var declared = group.Elements()
                .Where(element => element.Name.LocalName == "dependency")
                .Select(element => (
                    Id: element.Attribute("id")?.Value ?? string.Empty,
                    Version: element.Attribute("version")?.Value ?? string.Empty))
                .ToList();

            var siblings = declared
                .Select(dependency => dependency.Id)
                .Where(dependencyId => dependencyId.StartsWith("SFMCSDK.Net", StringComparison.Ordinal) ||
                                       dependencyId.StartsWith("MarketingCloudSDK.Net", StringComparison.Ordinal))
                .OrderBy(dependencyId => dependencyId, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expectedSiblings, siblings);

            foreach (var universal in Packages.UniversalDependencies)
            {
                Assert.True(
                    declared.Any(dependency => dependency.Id == universal),
                    $"{id} does not depend on {universal} for " +
                    $"{group.Attribute("targetFramework")?.Value}. Every module needs the Kotlin stdlib.");
            }

            var tfm = group.Attribute("targetFramework")?.Value ?? string.Empty;
            foreach (var (dependency, expectedTfm, version) in Packages.PerTfmDependencyVersions)
            {
                if (tfm != expectedTfm)
                {
                    continue;
                }

                var actual = declared.SingleOrDefault(d => d.Id == dependency);
                Assert.True(
                    actual.Version == version,
                    $"{id}'s {tfm} group pins {dependency} at '{actual.Version}', expected '{version}'. " +
                    "The per-head version split has been flattened; see Directory.Build.props.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Package_declares_the_expected_nuspec_metadata(string id)
    {
        using var package = Packages.OpenPackage(id);
        var nuspec = Packages.ReadNuspec(package, id);

        string Value(string element) => nuspec.Descendants()
            .FirstOrDefault(node => node.Name.LocalName == element)?.Value.Trim() ?? string.Empty;

        Assert.Equal(id, Value("id"));
        Assert.NotEmpty(Value("version"));
        Assert.Equal("MIT AND BSD-3-Clause", Value("license"));
        Assert.Equal("icon.png", Value("icon"));
        Assert.Equal("README.md", Value("readme"));

        // The description names the module the package wraps, which is what tells a reader on
        // nuget.org which package they want.
        Assert.Contains(Packages.Spec(id).Artifact, Value("description"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Package_ships_the_icon_readme_and_every_licence_text(string id)
    {
        using var package = Packages.OpenPackage(id);

        Assert.True(package.GetEntry("icon.png") is not null, "icon.png is referenced but not packed.");
        Assert.True(package.GetEntry("README.md") is not null, "README.md is referenced but not packed.");

        using var bindings = new StreamReader(Packages.ReadEntry(package, "licenses/LICENSE"));
        Assert.Contains("MIT License", bindings.ReadToEnd(), StringComparison.OrdinalIgnoreCase);

        using var native = new StreamReader(Packages.ReadEntry(package, "licenses/BSD-3-Clause-Salesforce.txt"));
        Assert.Contains("Salesforce", native.ReadToEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Symbol_package_is_produced(string id)
    {
        using var symbols = Packages.OpenPackage(id, ".snupkg");

        foreach (var tfm in Packages.ExpectedTargetFrameworks)
        {
            var expected = $"lib/{tfm}/{id}.pdb";
            Assert.True(
                symbols.GetEntry(expected) is not null,
                $"Symbol package for {id} is missing '{expected}'.");
        }
    }

    [Theory]
    [MemberData(nameof(Packages.Ids), MemberType = typeof(Packages))]
    public void Every_package_ships_consumer_keep_rules(string id)
    {
        using var package = Packages.OpenPackage(id);

        // Every SFMC entry point is reached from .NET through JNI alone - no Java code references
        // it - so a consumer's R8 shrink removes it and the binding throws ClassNotFoundException
        // in Release builds only. The keep-rules ride buildTransitive/, where NuGet imports the
        // .targets into every consuming project. The .pro files are generated by
        // build/generate-r8-rules.sh: upstream's consumer rules, which .NET for Android never
        // reads out of the .aar, plus curated keeps for the JNI-only entry surface.
        Assert.NotNull(package.GetEntry($"buildTransitive/{id}.targets"));
        Assert.NotNull(package.GetEntry($"buildTransitive/{id}.pro"));
    }

    [Fact]
    public void Every_expected_package_was_built_and_nothing_else()
    {
        var found = Directory.GetFiles(Packages.ArtifactsDirectory, "*.nupkg")
            .Select(path => Path.GetFileName(path)!)
            .Select(file => file[..FindVersionStart(file)])
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var expected = Packages.All
            .Select(spec => spec.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        // Catches both halves of a mistake in packages.tsv: a package silently dropped from the
        // list, and a stale package left in artifacts/ from an earlier version.
        Assert.Equal(expected, found);

        static int FindVersionStart(string file)
        {
            for (var i = 0; i < file.Length - 1; i++)
            {
                if (file[i] == '.' && char.IsDigit(file[i + 1]))
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"'{file}' has no version segment.");
        }
    }
}
