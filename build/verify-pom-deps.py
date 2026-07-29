#!/usr/bin/env python3
"""Verify the declared NuGet dependencies against the sfmcsdk .pom.

The binding's PackageReference set is hand-maintained; the .pom is what upstream actually links
against. On every native bump the two can drift - a dependency upstream added, or a version it
raised - and the failure a consumer sees is a runtime NoClassDefFoundError, not a build error.
Java dependency verification catches missing *artifacts*; this catches versions that fall behind
the .pom, which that check accepts as satisfied.

Salesforce publishes plain .pom files (no Gradle .module metadata), so the .pom is the source of
truth here, fetched from the same repository the build resolves artifacts from.

Exceptions are explicit: NET8_HELD_BACK lists coordinates whose net8 head deliberately pins below
the .pom (the newer binding dropped net8); the net9/net10 heads still must satisfy the .pom.
"""

import re
import sys
import urllib.request
from pathlib import Path
from xml.etree import ElementTree

REPO = "https://salesforce-marketingcloud.github.io/MarketingCloudSDK-Android/repository"
ROOT = Path(__file__).resolve().parent.parent

# Maven coordinate -> (NuGet id, MSBuild property in Directory.Build.props holding the pin).
COORDINATE_TO_NUGET = {
    "org.jetbrains.kotlin:kotlin-stdlib": ("Xamarin.Kotlin.StdLib", "KotlinStdLibVersion"),
    "androidx.core:core-ktx": ("Xamarin.AndroidX.Core.Core.Ktx", "AndroidXCoreKtxVersion"),
    "androidx.lifecycle:lifecycle-process": ("Xamarin.AndroidX.Lifecycle.Process", "AndroidXLifecycleProcessVersion"),
    "androidx.annotation:annotation": ("Xamarin.AndroidX.Annotation", "AndroidXAnnotationVersion"),
    "com.google.android.gms:play-services-basement": ("Xamarin.GooglePlayServices.Basement", "GooglePlayServicesBasementVersion"),
    "androidx.work:work-runtime": ("Xamarin.AndroidX.Work.Runtime", "AndroidXWorkRuntimeVersion"),
    "androidx.concurrent:concurrent-futures": ("Xamarin.AndroidX.Concurrent.Futures", "AndroidXConcurrentFuturesVersion"),
}

# Salesforce-internal coordinates satisfied inside this repository's own packages, not by NuGet.
IN_REPO = {
    "com.salesforce.marketingcloud:common-internal": "SfmcCommonInternalVersion",
}

# Google Play services versions on NuGet carry a leading "1" (18.9.0 -> 118.9.0).
GMS_PREFIX = "com.google.android.gms:"


def prop(name: str) -> str:
    text = (ROOT / "Directory.Build.props").read_text()
    match = re.search(rf"<{name}>([^<]+)</{name}>", text)
    if not match:
        sys.exit(f"error: {name} not found in Directory.Build.props")
    return match.group(1).strip()


def parse_version(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", value)[:4]) or (0,)


def nuget_wraps(coordinate: str, nuget_version: str, pom_version: str) -> bool:
    """A NuGet binding at X.Y.Z[.R] wraps native X.Y.Z; the fourth part is the binding's own."""
    native = parse_version(pom_version)
    nuget = parse_version(nuget_version)
    if coordinate.startswith(GMS_PREFIX) and nuget and nuget[0] >= 100:
        nuget = (nuget[0] - 100,) + nuget[1:]
    return nuget[: len(native)] >= native


def main() -> int:
    native = prop("SfmcNativeVersion")
    url = f"{REPO}/com/salesforce/marketingcloud/sfmcsdk/{native}/sfmcsdk-{native}.pom"
    with urllib.request.urlopen(url) as response:
        pom = ElementTree.fromstring(response.read())

    ns = {"m": "http://maven.apache.org/POM/4.0.0"}
    failures = []
    checked = 0

    for dependency in pom.findall(".//m:dependency", ns):
        group = dependency.findtext("m:groupId", "", ns)
        artifact = dependency.findtext("m:artifactId", "", ns)
        version = dependency.findtext("m:version", "", ns)
        coordinate = f"{group}:{artifact}"

        if coordinate in IN_REPO:
            pinned = prop(IN_REPO[coordinate])
            checked += 1
            if parse_version(pinned) != parse_version(version):
                failures.append(
                    f"{coordinate}: the .pom wants {version}, {IN_REPO[coordinate]} pins {pinned}")
            continue

        if coordinate not in COORDINATE_TO_NUGET:
            failures.append(
                f"{coordinate} {version}: the .pom declares a dependency this script has no NuGet "
                "mapping for - upstream added one; map it in build/verify-pom-deps.py and reference "
                "the binding package")
            continue

        nuget_id, prop_name = COORDINATE_TO_NUGET[coordinate]
        pinned = prop(prop_name)
        checked += 1
        if not nuget_wraps(coordinate, pinned, version):
            failures.append(
                f"{coordinate}: the .pom wants {version}, but {nuget_id} is pinned at {pinned} "
                f"({prop_name})")

    if failures:
        print(f"sfmcsdk {native} .pom disagreements:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1

    print(f"verified {checked} declared dependencies against the sfmcsdk {native} .pom")
    return 0


if __name__ == "__main__":
    sys.exit(main())
