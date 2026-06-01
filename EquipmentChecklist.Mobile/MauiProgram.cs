using EquipmentChecklist.Mobile.Services;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace EquipmentChecklist.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// ── App services ────────────────────────────────────────────
		builder.Services.AddSingleton<BiometricUnlock>();
		builder.Services.AddSingleton<AuthService>();
		builder.Services.AddSingleton<LocalCache>();
		builder.Services.AddSingleton<SubmissionQueue>();
		builder.Services.AddSingleton<ActionQueue>();
		builder.Services.AddSingleton<SyncWorker>();
		builder.Services.AddSingleton<ApiHealth>();
		// Scoped: LocalPdfService captures IJSRuntime which is per-WebView.
		builder.Services.AddScoped<LocalPdfService>();
		// Cross-platform recorder/player factory from Plugin.Maui.Audio.
		// AddAudio() is the package-provided extension method — it registers
		// IAudioManager + handles platform-specific init that the bare
		// AudioManager.Current accessor doesn't. Pages then inject
		// IAudioManager via constructor / [Inject] and call CreateRecorder().
		builder.AddAudio();
		// ── HTTP client (platform-aware base URL) ────────────────────
		//
		// localhost means "this device" on every platform:
		//   • Windows desktop  → loopback hits the dev Kestrel directly.
		//   • Android emulator → loopback is the emulator itself, NOT the
		//                        host machine. Google reserves 10.0.2.2 as
		//                        the alias for the host loopback, so that's
		//                        what we hand to the emulator. Physical
		//                        Android devices need the host's LAN IP
		//                        instead — change this string when you test
		//                        on a phone over Wi-Fi.
		//
		// Port stays at the dev Kestrel port from launchSettings.json (55025).
		// Make sure Kestrel binds to 0.0.0.0:55025 (not just localhost) so the
		// emulator's 10.0.2.2 hop can actually reach it — easiest is to add
		// `--urls https://0.0.0.0:55025` to `dotnet run` on the server.
#if ANDROID
		// 10.0.2.2 = the Android emulator's alias for the host's loopback.
		// On a PHYSICAL phone over Wi-Fi, change this to your dev machine's
		// LAN IP (run `ipconfig` on Windows, look at the Wi-Fi adapter's
		// IPv4 line — usually 192.168.x.x). Both devices must be on the
		// same Wi-Fi network and your firewall must allow port 55025.
		//
		// Example for a physical phone:
		//     const string ApiBaseUrl = "https://192.168.1.42:55025/";
		const string ApiBaseUrl = "https://10.0.2.2:55025/";
#else
		const string ApiBaseUrl = "https://localhost:55025/";
#endif
		builder.Services.AddHttpClient<ApiClient>(c =>
		{
			c.BaseAddress = new Uri(ApiBaseUrl);
			c.Timeout     = TimeSpan.FromSeconds(20);
		})
#if DEBUG && ANDROID
		// The ASP.NET Core dev HTTPS cert isn't trusted by Android's
		// system store. In DEBUG only, accept anything so dev work isn't
		// blocked on cert plumbing. NEVER ship this — for release builds
		// either install the dev cert on-device or stand up a properly
		// trusted cert on the API.
		.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
		{
			ServerCertificateCustomValidationCallback =
				HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
		})
#endif
		;

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Force-instantiate SyncWorker so its connectivity listener is alive
		// from app start — without this the worker only wakes up the first time
		// some page injects it.
		_ = app.Services.GetRequiredService<SyncWorker>();

		// Same trick for ApiHealth — start polling the server immediately so
		// the status pill becomes accurate within seconds of app launch
		// instead of waiting for the first page that injects it.
		_ = app.Services.GetRequiredService<ApiHealth>();

		return app;
	}
}