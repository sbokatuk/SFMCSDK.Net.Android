#!/bin/sh
# Fails when README.md pins a package version that is not the one this repository currently
# builds. The install snippets are copy-paste starting points, and a hardcoded version there goes
# stale silently on every release. Running this in CI makes the version bump before a release
# drag the README along with it.
#
# What is checked: every <PackageReference Include="SFMCSDK.Net..." Version="..."> pin, and the
# device-check example (run-emulator-tests.sh <version> ...). Prose that explains the version
# *scheme* ("3.1.1.1 is sfmcsdk 3.1.1, binding revision 1") is deliberately not checked - it
# describes the format, not the current release.
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
readme="$root/README.md"
props="$root/Directory.Build.props"

prop() {
  sed -n "s/.*<$1>\(.*\)<\/$1>.*/\1/p" "$props" | head -1
}

version="$(prop SfmcNativeVersion).$(prop SfmcBindingRevision)"

bad=0

pins=$(grep -o 'Include="SFMCSDK\.Net[^"]*"[[:space:]]*Version="[^"]*"' "$readme" | sed 's/.*Version="\([^"]*\)"/\1/' || true)
for pin in $pins; do
    if [ "$pin" != "$version" ]; then
        echo "README.md pins a PackageReference at $pin, but this repository builds $version" >&2
        bad=1
    fi
done

runs=$(grep -o 'run-emulator-tests\.sh [0-9][0-9.]*' "$readme" | awk '{print $2}' || true)
for run in $runs; do
    if [ "$run" != "$version" ]; then
        echo "README.md invokes run-emulator-tests.sh with $run, but this repository builds $version" >&2
        bad=1
    fi
done

if [ "$bad" -ne 0 ]; then
    exit 1
fi

echo "README.md version pins match $version"
