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

    private static readonly TaskCompletionSource<InitializationStatus> Initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static readonly SmokeTest[] All =
    [
        new("native_classes_are_present", () =>
        {
            // Class.forName is the same lookup JNI performs; a missing .aar fails here with
            // ClassNotFoundException rather than three checks later with something baffling.
            // PermissionUtils is the marker for common-internal, which ships in this package
            // unbound - the class must still be dexed into the app.
            Java.Lang.Class.ForName("com.salesforce.marketingcloud.sfmcsdk.SFMCSdk");
            Java.Lang.Class.ForName("com.salesforce.marketingcloud.internal.util.PermissionUtils");
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
