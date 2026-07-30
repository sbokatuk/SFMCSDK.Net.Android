using System;
using Android.Content;
using Android.Runtime;

namespace Com.Salesforce.Marketingcloud.Sfmcsdk
{
	/// <summary>
	/// Bridges a C# delegate to <c>kotlin.jvm.functions.Function1&lt;InitializationStatus, Unit&gt;</c>,
	/// the callback type <c>SFMCSdk.configure</c> takes. Without this every consumer has to write
	/// their own <c>Java.Lang.Object, IFunction1</c> subclass - the old sfmc-net-bindings-mobile
	/// sample shipped exactly that boilerplate, copy-pasted per app.
	/// </summary>
	public sealed class InitializationCallback : Java.Lang.Object, Kotlin.Jvm.Functions.IFunction1
	{
		private readonly Action<IInitializationStatus> onComplete;

		public InitializationCallback(Action<IInitializationStatus> onComplete)
			=> this.onComplete = onComplete ?? throw new ArgumentNullException(nameof(onComplete));

		public Java.Lang.Object Invoke(Java.Lang.Object p0)
		{
			// The interface, not the same-named class: upstream's InitializationStatus is a Java
			// interface, so the generator emits IInitializationStatus (with an Invoker) plus an
			// obsolete class of the same name that only carries the constants and has no Invoker.
			// Casting to the class form throws "Unable to find Invoker for type ..." on the SDK's
			// callback thread, which is fatal - it is a Java-called thread.
			onComplete(p0.JavaCast<IInitializationStatus>());
			return Kotlin.Unit.Instance;
		}
	}

	/// <summary>
	/// Bridges a C# delegate to <c>SFMCSdkReadyListener</c>, the interface
	/// <c>SFMCSdk.requestSdk</c> takes.
	/// </summary>
	public sealed class SdkReadyListener : Java.Lang.Object, ISFMCSdkReadyListener
	{
		private readonly Action<SFMCSdk> onReady;

		public SdkReadyListener(Action<SFMCSdk> onReady)
			=> this.onReady = onReady ?? throw new ArgumentNullException(nameof(onReady));

		public void Ready(SFMCSdk sdk) => onReady(sdk);
	}

	public partial class SFMCSdk
	{
		/// <summary>
		/// <see cref="Configure(Context, SFMCSdkModuleConfig, Kotlin.Jvm.Functions.IFunction1)"/>
		/// with a C# delegate in place of the Kotlin function type. The delegate is invoked once,
		/// on the SDK's initialization thread, with the terminal status.
		/// </summary>
		public static void Configure(Context context, SFMCSdkModuleConfig config, Action<IInitializationStatus> onInitialized)
			=> Configure(context, config, new InitializationCallback(onInitialized));

		/// <summary>
		/// <see cref="RequestSdk(ISFMCSdkReadyListener)"/> with a C# delegate. The delegate runs
		/// once the SDK reaches its operational state, with the ready instance.
		/// </summary>
		public static void RequestSdk(Action<SFMCSdk> onReady)
			=> RequestSdk(new SdkReadyListener(onReady));
	}
}
