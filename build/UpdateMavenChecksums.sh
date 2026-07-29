#!/bin/sh
# Regenerates build/maven-checksums.txt: one pinned SHA-256 per Maven artifact any project in
# this repository resolves, at the versions currently declared in Directory.Build.props.
#
# Run it after bumping SfmcNativeVersion (or SfmcCommonInternalVersion), then diff the result -
# a hash that CHANGED for an unchanged version is exactly the event the pins exist to catch, and
# wants investigating rather than committing.
#
# Where the hashes come from: Salesforce's repository serves a .sha256 sidecar for every file, so
# each pin is the publisher's own published hash - and the artifact is also downloaded and hashed
# locally, with a mismatch between the two being a hard failure. That anchors trust at "what
# Salesforce served on this date" rather than "whatever this machine happened to download".
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
props="$root/Directory.Build.props"
out="$root/build/maven-checksums.txt"
repo="https://salesforce-marketingcloud.github.io/MarketingCloudSDK-Android/repository"
group_path="com/salesforce/marketingcloud"

prop() {
    sed -n "s/.*<$1>\(.*\)<\/$1>.*/\1/p" "$props" | head -1
}

native_version="$(prop SfmcNativeVersion)"
common_internal_version="$(prop SfmcCommonInternalVersion)"

if [ -z "$native_version" ] || [ -z "$common_internal_version" ]; then
    echo "error: could not read versions from Directory.Build.props" >&2
    exit 1
fi

# artifact-id<space>version, one per resolved Maven file. Keep in sync with the
# @(SfmcMavenArtifact) rows in src/ - the VerifySfmcMavenArtifactHashes target fails the build
# when a declared artifact has no pin, so a missing row here cannot ship silently.
artifacts="sfmcsdk $native_version
common-internal $common_internal_version"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

tmp="$work/maven-checksums.txt"
cat > "$tmp" <<'EOF'
# SHA-256 pins for every Maven artifact this repository resolves.
#
# Verified by the VerifySfmcMavenArtifactHashes target in src/Sfmc.Binding.props on every
# build, on both resolution paths - @(AndroidMavenLibrary) and the direct-download fallback.
# Regenerate with build/UpdateMavenChecksums.sh after a version bump; a hash that changes for
# an UNCHANGED version is the tampering event these pins exist to catch.
#
# <file name> <sha256>
EOF

echo "$artifacts" | while IFS=' ' read -r artifact version; do
    [ -n "$artifact" ] || continue
    file="$artifact-$version.aar"
    url="$repo/$group_path/$artifact/$version/$file"

    echo "==> $file"
    curl -fsSL -o "$work/$file" "$url"
    published="$(curl -fsSL "$url.sha256" | tr -d '[:space:]')"
    computed="$(shasum -a 256 "$work/$file" | cut -d' ' -f1)"

    if [ "$published" != "$computed" ]; then
        echo "error: $file - published sha256 ($published) does not match the downloaded bytes ($computed)" >&2
        exit 1
    fi

    printf '%s %s\n' "$file" "$computed" >> "$tmp"
done

mv "$tmp" "$out"
echo "==> wrote $out - review the diff before committing"
