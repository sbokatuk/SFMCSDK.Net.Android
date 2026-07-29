using Com.Salesforce.Marketingcloud.Sfmcsdk;
using Com.Salesforce.Marketingcloud.Sfmcsdk.Components.Events;
using Com.Salesforce.Marketingcloud.Sfmcsdk.Components.Logging;

namespace SFMCSDK.Net.Android.Example;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>Routes the SDK's own log lines to logcat, tagged so they are easy to filter.</summary>
    private sealed class LogcatListener : Java.Lang.Object, ILogListener
    {
        public void Out(LogLevel? level, string? tag, string? message, Java.Lang.Throwable? throwable)
            => global::Android.Util.Log.Debug($"SFMCSDK.{tag}", $"{message} {throwable}");
    }

    private void OnInitializeClicked(object? sender, EventArgs e)
    {
        InitializeButton.IsEnabled = false;
        AppendLog("Configuring…");

        // Everything below is the raw binding, in the same shapes the Kotlin quick-start uses.
        // The Action overloads of Configure and RequestSdk are this package's Additions - without
        // them these calls each need a hand-written Java.Lang.Object subclass.
        SFMCSdk.SetLogging(LogLevel.Debug!, new LogcatListener());

        var config = new SFMCSdkModuleConfig.Builder().Build();

        SFMCSdk.Configure(global::Android.App.Application.Context, config, status =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusLabel.Text = $"Initialization: {status}";
                AppendLog($"Initialization completed: {status}");
                ProfileIdEntry.IsEnabled = true;
                ProfileIdButton.IsEnabled = true;
                TrackButton.IsEnabled = true;
            });
        });
    }

    private void OnSetProfileIdClicked(object? sender, EventArgs e)
    {
        var profileId = ProfileIdEntry.Text?.Trim();
        if (string.IsNullOrEmpty(profileId))
        {
            AppendLog("Enter a profile id first.");
            return;
        }

        SFMCSdk.RequestSdk(sdk =>
        {
            // v3 identity is an immutable record: rebuild it and assign it back.
            sdk.Identity = sdk.Identity.NewBuilder().SetProfileId(profileId).Build();
            MainThread.BeginInvokeOnMainThread(() => AppendLog($"Profile id set to '{profileId}'."));
        });
    }

    private void OnTrackClicked(object? sender, EventArgs e)
    {
        var custom = EventManager.CustomEvent(
            "sample_button_tapped",
            new Dictionary<string, Java.Lang.Object> { ["source"] = "SFMCSDK.Net.Android.Example" });

        if (custom is null)
        {
            AppendLog("EventManager.CustomEvent returned null - the event name was rejected.");
            return;
        }

        SFMCSdk.Track(custom);
        AppendLog("Tracked 'sample_button_tapped'.");
    }

    private void AppendLog(string line)
        => LogLabel.Text = $"{DateTime.Now:HH:mm:ss}  {line}\n{LogLabel.Text}";
}
