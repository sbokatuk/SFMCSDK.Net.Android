using Com.Salesforce.Marketingcloud.Sfmcsdk;

namespace SFMCSDK.Net.Android.DeviceTests;

/// <summary>One check: a name and something that throws when the SDK misbehaves.</summary>
public sealed record SmokeTest(string Name, Func<Task> Execute);

/// <summary>
/// Drives the real, packed binding on a real Android runtime. No credentials: the configuration
/// below points at no tenant, so nothing registers anywhere - what is being proven is that the
/// native classes are present, the JNI surface works, and the Kotlin callback bridges fire.
/// </summary>
public static class SmokeTests
{
    /// <summary>Where progress lines go; MainActivity points this at logcat.</summary>
    public static Action<string> Reporter { get; set; } = _ => { };

    private static readonly TaskCompletionSource<IInitializationStatus> Initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static readonly SmokeTest[] All =
    [
        new("native_classes_are_present", () =>
        {
            // Class.forName is the same lookup JNI performs; a missing .aar fails here with
            // ClassNotFoundException rather than three checks later with something baffling.
            // PermissionUtils is the marker for common-internal, which ships in this package
            // unbound - the class must still be dexed into the app. IntentUtils is the marker for
            // common-internal being new enough: it first appeared in 1.1.0 and sfmcsdk 3.3.0's
            // BehaviorManagerImpl.BehaviorReceiver calls it from onReceive (javap -c on
            // BehaviorManagerImpl$BehaviorReceiver). An app carrying 1.0.0 is killed at launch by a
            // NoClassDefFoundError out of the SDK's init ContentProvider; with this lookup the log
            // names the missing class instead of leaving only a crash trace. The net9/net10
            // builds cannot produce that pair (XA4241); the direct-download path the net8 head
            // takes has no Java dependency verification and can.
            //
            // The loader is passed explicitly: Class.forName(String) resolves against the caller's
            // loader, and a call made from managed code has no Java frame to take one from, so
            // Android falls back to the boot class loader - which never sees the app's dex and
            // fails every lookup with "Class not found using the boot class loader".
            var loader = global::Android.App.Application.Context.ClassLoader
                ?? throw new InvalidOperationException("The application context has no class loader.");

            Java.Lang.Class.ForName("com.salesforce.marketingcloud.sfmcsdk.SFMCSdk", false, loader);
            Java.Lang.Class.ForName("com.salesforce.marketingcloud.internal.util.PermissionUtils", false, loader);
            Java.Lang.Class.ForName("com.salesforce.marketingcloud.internal.util.IntentUtils", false, loader);
            return Task.CompletedTask;
        }),

        new("packed_aar_is_the_version_the_binding_was_generated_from", () =>
        {
            // BuildConfig.VersionName is a C# const the generator copied out of the .aar when the
            // binding was built; BuildConfig.VERSION_NAME read through reflection is what the
            // .aar dexed into this app says at runtime. They disagree when the package carries a
            // different .aar from the one it was generated against - a stale Maven cache, or a
            // merge that grafted another line's assets - and the binding then calls members
            // that are not there. Read from BuildConfig rather than SFMCSdk.SDK_VERSION_NAME
            // because reading a static field initializes its class, and SFMCSdk's initializer
            // is the SDK starting up - the next check's business, not this one's.
            var loader = global::Android.App.Application.Context.ClassLoader
                ?? throw new InvalidOperationException("The application context has no class loader.");

            var packed = Java.Lang.Class
                .ForName("com.salesforce.marketingcloud.sfmcsdk.BuildConfig", false, loader)
                .GetField("VERSION_NAME")
                .Get(null)?
                .ToString();

            Reporter($"native sfmcsdk version: {packed} (binding generated from {BuildConfig.VersionName})");

            if (packed != BuildConfig.VersionName)
            {
                throw new InvalidOperationException(
                    $"The packed sfmcsdk .aar reports version '{packed}', but the binding was " +
                    $"generated from '{BuildConfig.VersionName}'.");
            }

            return Task.CompletedTask;
        }),

        new("configure_invokes_the_completion_callback", async () =>
        {
            var context = global::Android.App.Application.Context;
            var config = new SFMCSdkModuleConfig.Builder().Build();

            // The Action overload is this repository's Additions code, so this check is also the
            // end-to-end proof of the Kotlin Function1 bridge.
            SFMCSdk.Configure(context, config, status =>
            {
                Reporter($"initialization status: {status}");
                Initialized.TrySetResult(status);
            });

            var completed = await Task.WhenAny(Initialized.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (completed != Initialized.Task)
            {
                throw new TimeoutException("SFMCSdk.Configure never invoked its completion callback.");
            }
        }),

        new("request_sdk_hands_out_an_instance", async () =>
        {
            var ready = new TaskCompletionSource<SFMCSdk>(TaskCreationOptions.RunContinuationsAsynchronously);
            SFMCSdk.RequestSdk(sdk => ready.TrySetResult(sdk));

            var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != ready.Task)
            {
                throw new TimeoutException("SFMCSdk.RequestSdk never called back with an instance.");
            }

            _ = ready.Task.Result.Identity
                ?? throw new InvalidOperationException("The ready SDK instance has no Identity component.");
        }),

        new("identity_accepts_a_profile_id", async () =>
        {
            var ready = new TaskCompletionSource<SFMCSdk>(TaskCreationOptions.RunContinuationsAsynchronously);
            SFMCSdk.RequestSdk(sdk => ready.TrySetResult(sdk));
            var sdk = await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // No server round-trip is asserted - there is no tenant - only that the call crosses
            // JNI without throwing, which is what a missing class or a broken binding breaks.
            // v3 identity is an immutable record: rebuild it and assign it back.
            sdk.Identity = sdk.Identity.NewBuilder().SetProfileId("sfmcsdk-net-device-test").Build();
        }),
    ];
}
