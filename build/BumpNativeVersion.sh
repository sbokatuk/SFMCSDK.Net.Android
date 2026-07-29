#!/bin/sh
# Runs every scripted step of an sfmcsdk upgrade, in order, stopping at the first failure.
#
#   ./build/BumpNativeVersion.sh 3.2.0 [common-internal-version]
#
# What it does NOT automate, in the order you will meet it:
#   - reviewing the checksum diff (a hash that changed for an unchanged version is the event
#     the pins exist to catch);
#   - re-checking the Transforms rules against the new .aar - an XPath that stops matching is
#     silent (see src/SFMCSDK.Net.Android/Transforms/Metadata.xml);
#   - the README's version pins (build/CheckReadmeVersions.sh will tell you);
#   - a release-notes file under docs/release-notes/;
#   - bumping SfmcBindingRevision back to 1 for the new native line.
set -eu

version="$1"
common_internal="${2:-}"

case "$version" in
    *[!0-9.]*|'')
        echo "usage: $0 <sfmcsdk-version> [common-internal-version]" >&2
        exit 1
        ;;
esac

root="$(cd "$(dirname "$0")/.." && pwd)"
props="$root/Directory.Build.props"

echo "==> pinning SfmcNativeVersion $version"
sed -i '' "s:<SfmcNativeVersion>.*</SfmcNativeVersion>:<SfmcNativeVersion>$version</SfmcNativeVersion>:" "$props"

if [ -n "$common_internal" ]; then
    echo "==> pinning SfmcCommonInternalVersion $common_internal"
    sed -i '' "s:<SfmcCommonInternalVersion>.*</SfmcCommonInternalVersion>:<SfmcCommonInternalVersion>$common_internal</SfmcCommonInternalVersion>:" "$props"
fi

echo "==> regenerating Maven checksums"
"$root/build/UpdateMavenChecksums.sh"

echo "==> regenerating consumer R8 rules"
"$root/build/generate-r8-rules.sh"

echo "==> packing"
"$root/build/BuildNugets.sh"

echo "==> package tests"
dotnet test "$root/tests/SFMCSDK.Net.Android.PackageTests"

echo "==> done. Review the diffs (especially build/maven-checksums.txt), update the README and"
echo "    docs/release-notes/, and check the .pom's third-party versions against"
echo "    Directory.Build.props - this script does not do that for you."
