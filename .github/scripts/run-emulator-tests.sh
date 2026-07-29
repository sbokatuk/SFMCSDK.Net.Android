#!/usr/bin/env bash
set -euo pipefail

# Builds the device test app against the packed SFMCSDK.Net packages, installs it on a running
# Android emulator and runs its smoke tests. The app reports its verdict to logcat under a single
# tag; this script turns that into an exit code.
#
# Assumes an emulator is already booted and visible to adb - in CI that is
# reactivecircus/android-emulator-runner, locally it is whatever you started yourself.
#
# Usage: run-emulator-tests.sh VERSION [TARGET_FRAMEWORK] [LINK_TOOL]
#
# LINK_TOOL is empty (no Java shrinking, the default) or r8. The r8 leg builds the device test
# app shrunk, so the consumer keep-rules every package ships under buildTransitive/ are what
# keeps the smoke tests passing - a keep-rule regression fails here instead of in a consumer's
# Release build.

VERSION="${1:?a package version is required}"
TARGET_FRAMEWORK="${2:-net10.0-android36.0}"
LINK_TOOL="${3:-}"

LINK_ARGS=()
if [ -n "${LINK_TOOL}" ]; then
    LINK_ARGS+=("-p:SfmcDeviceTestsLinkTool=${LINK_TOOL}")
fi

PACKAGE_NAME="com.sbokatuk.sfmcsdknet.devicetests"
LOG_FILE="emulator-tests.log"
LOG_TAG="SfmcNetE2E"
# CI emulators are x86_64. Override for a local arm64 emulator on Apple silicon.
DEVICE_RID="${DATADOG_DEVICE_RID:-android-x64}"
POLL_ATTEMPTS=90
POLL_INTERVAL=5

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="${REPO_ROOT}/tests/SFMCSDK.Net.Android.DeviceTests/SFMCSDK.Net.Android.DeviceTests.csproj"

# The SDK band is chosen by the *Android API level* in the target framework, not by the .NET
# version alone, because that is what decides which workload owns the runtime packs:
#
#   net8.0-android34.0  -> android 34.0.x, in the .NET 8 band
#   net9.0-android35.0  -> android 35.0.x, in the .NET 9 band
#   net10.0-android36.0 -> android 36.0.x, in the .NET 10 band
#
# The .NET 9 band compiles a net8 app happily - it has the API 34 *reference* packs - and then
# fails at packaging time, because it has no API 34 *runtime* packs and they cannot be restored
# from NuGet:
#
#     error NETSDK1112: The runtime pack for Microsoft.Android.Runtime.34.android-x64 was not
#     downloaded. Try running a NuGet restore with the RuntimeIdentifier 'android-x64'.
#
# The restore that error suggests does not help; the packs come from the workload. The SDK is
# resolved from the working directory, and this repository's global.json pins .NET 9, hence the
# scratch directory below.
case "${TARGET_FRAMEWORK}" in
    net10.0-*) sdk_major=10 ;;
    net8.0-*)  sdk_major=8 ;;
    *)         sdk_major=9 ;;
esac

sdk_version="$(dotnet --list-sdks | grep "^${sdk_major}\." | tail -1 | cut -d' ' -f1)"
if [ -z "${sdk_version}" ]; then
    echo "::error::no .NET ${sdk_major} SDK installed, cannot build ${TARGET_FRAMEWORK}"
    exit 1
fi

SDK_DIR="$(mktemp -d)"
trap 'rm -rf "${SDK_DIR}"' EXIT
printf '{ "sdk": { "version": "%s", "rollForward": "latestFeature" } }\n' "${sdk_version}" \
    > "${SDK_DIR}/global.json"

# NuGet caches by package id + version, so rebuilding a version that was already restored once
# silently reuses the stale copy. CI versions are unique, but locally you will re-pack the same
# version repeatedly and test yesterday's bits without this. Every package is cleared, not just
# the ones referenced directly, because the rest arrive as transitive dependencies.
while IFS=$'\t' read -r name _rest; do
    case "${name}" in ''|\#*) continue ;; esac
    lower="$(printf '%s' "${name}" | tr '[:upper:]' '[:lower:]')"
    rm -rf "${HOME}/.nuget/packages/${lower}/${VERSION}"
done < "${REPO_ROOT}/build/packages.tsv"

echo "==> building device tests (version=${VERSION}, tfm=${TARGET_FRAMEWORK}, sdk=${sdk_version}, link-tool=${LINK_TOOL:-none})"
# Debug, not Release. Release AOT-compiles every assembly, and an AOT image built against an
# unlinked assembly set disagrees with what the runtime loads - the app aborts on startup with
# "Assertion ... condition `klass->instance_size == instance_size' not met" before a single check
# runs. Debug also skips the R8 shrinking this app has to avoid anyway, and saves the three and a
# half minutes AOT spends on 347 assemblies every CI run.
#
# Nothing is lost: this suite verifies that the packages carry their .aar files and that the SDK
# runs, not that AOT works. The sample job covers the Release build path.
( cd "${SDK_DIR}" && dotnet build "${PROJECT}" \
    --configuration Debug \
    -p:SfmcPackageVersion="${VERSION}" \
    -p:SfmcDeviceTargetFramework="${TARGET_FRAMEWORK}" \
    -p:RuntimeIdentifier="${DEVICE_RID}" \
    ${LINK_ARGS[@]+"${LINK_ARGS[@]}"} \
    -t:Install )

echo "==> launching"
adb logcat -c
# The activity name is pinned in the app rather than left to the generated crc64* name, so this
# target stays stable across builds.
adb shell am start -n "${PACKAGE_NAME}/.MainActivity"

echo "==> waiting for the verdict"
for _ in $(seq "${POLL_ATTEMPTS}"); do
    if adb logcat -d -s "${LOG_TAG}:*" | grep -q "SFMC_E2E_DONE"; then
        break
    fi
    sleep "${POLL_INTERVAL}"
done

adb logcat -d -s "${LOG_TAG}:*" | tee "${LOG_FILE}"

if ! grep -q "SFMC_E2E_DONE PASS" "${LOG_FILE}"; then
    # No verdict usually means the app died before reporting, so keep the crash trace. A missing
    # Java dependency shows up here as a NoClassDefFoundError naming the class.
    echo "==> no passing verdict; capturing crash output"
    adb logcat -d -s AndroidRuntime:E DEBUG:F "${PACKAGE_NAME}:*" 2>/dev/null \
        | tail -100 | tee -a "${LOG_FILE}" || true
    echo "::error::SFMC emulator smoke tests failed or timed out"
    exit 1
fi

echo "==> emulator smoke tests passed"
