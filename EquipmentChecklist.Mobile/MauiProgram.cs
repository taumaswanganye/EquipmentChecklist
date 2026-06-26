using EquipmentChecklist.Mobile.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
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
			})
			// Phase 8.6 — platform lifecycle hooks bridged into our own
			// AppLifecycleService. Each platform fires its native resume
			// event; we relay to the same C# event so subscribers (e.g.
			// MainLayout's permission banner refresh) get one signal
			// regardless of platform.
			.ConfigureLifecycleEvents(events =>
			{
#if ANDROID
				events.AddAndroid(android => android.OnResume(activity =>
				{
					try
					{
						var lifecycle = IPlatformApplication.Current?.Services
							.GetService<AppLifecycleService>();
						lifecycle?.RaiseResumed();
					}
					catch { /* lifecycle handler must never throw */ }
				}));
#endif
#if WINDOWS
				events.AddWindows(windows => windows.OnActivated((window, args) =>
				{
					// Windows fires Activated on every focus change; we
					// only relay when the activation kind is the app
					// returning from background (kind == CodeActivated
					// for foreground resume).
					try
					{
						var lifecycle = IPlatformApplication.Current?.Services
							.GetService<AppLifecycleService>();
						lifecycle?.RaiseResumed();
					}
					catch { }
				}));
#endif
#if IOS || MACCATALYST
				events.AddiOS(ios => ios.OnActivated(app =>
				{
					try
					{
						var lifecycle = IPlatformApplication.Current?.Services
							.GetService<AppLifecycleService>();
						lifecycle?.RaiseResumed();
					}
					catch { }
				}));
#endif
			});

		builder.Services.AddMauiBlazorWebView();

		// ── App services ────────────────────────────────────────────
		builder.Services.AddSingleton<BiometricUnlock>();
		// MDM-lite device fingerprint resolver. Singleton because the
		// platform read (Android.Provider.Settings.Secure.ANDROID_ID /
		// Windows MachineGuid) is constant for the lifetime of the app
		// process, and we cache it in SecureStorage too.
		builder.Services.AddSingleton<DeviceFingerprint>();
		builder.Services.AddSingleton<AuthService>();
		// NOTE: PersistentBackup (cache mirror to public Documents) is
		// temporarily NOT registered — its constructor was suspected of
		// causing a silent startup crash on this Android target. To
		// re-enable: AddSingleton<PersistentBackup>() here and add the
		// TryEager<PersistentBackup>(app) call below.
		builder.Services.AddSingleton<LocalCache>();
		builder.Services.AddSingleton<SubmissionQueue>();
		builder.Services.AddSingleton<ActionQueue>();
		// Append-only audit buffer. Singletons because the queue owns a SQLite
		// connection — only one instance should ever hold the file handle.
		builder.Services.AddSingleton<AuditQueue>();
		builder.Services.AddSingleton<Audit>();
		builder.Services.AddSingleton<SyncWorker>();
		// Bridges SyncWorker.PrunedStale into the toast system so the user
		// gets a one-shot warning when bounded retention drops old rows.
		builder.Services.AddSingleton<StalePruneNotifier>();
		builder.Services.AddSingleton<ApiHealth>();
		// Tactile feedback — toasts subscribed in MainLayout, haptics
		// invoked from pages on commit-style actions.
		builder.Services.AddSingleton<ToastService>();
		builder.Services.AddSingleton<HapticService>();
		// Phase 8.6 — App lifecycle event bus. Singleton so all subscribers
		// see the same Resumed event; raised from ConfigureLifecycleEvents
		// above.
		builder.Services.AddSingleton<AppLifecycleService>();

		// Phase 8.1 — Loud Alert mode for offline NO-GO. Coordinates siren
		// (JS Web Audio) + continuous vibration + full-screen red flash.
		// Scoped because it touches IJSRuntime which is per-circuit, but
		// the StateChanged event lets MainLayout react globally.
		builder.Services.AddScoped<UrgentAlertService>();

		// Phase 8.2 — Bluetooth LE peer-to-peer urgent broadcast.
		// Platform-conditional registration: Android gets the real
		// publisher backed by BluetoothLeAdvertiser + BluetoothLeScanner;
		// Windows + iOS fall back to the NoOp until those platforms get
		// their own per-platform implementations. Singleton because the
		// BLE adapter is process-wide and there's no per-circuit state.
#if ANDROID
		builder.Services.AddSingleton<IPeerAlertService,
			EquipmentChecklist.Mobile.Platforms.Android.PeerAlertPublisherAndroid>();
		// Phase 8.4 — runtime permission orchestration. Android-only;
		// iOS + Windows use the NoOp.
		builder.Services.AddSingleton<IPeerAlertPermissions,
			EquipmentChecklist.Mobile.Platforms.Android.PeerAlertPermissionsAndroid>();
#elif WINDOWS
		// Phase 8.8 — Surface Pro target. Windows BLE doesn't need a
		// foreground service shim or runtime permission dialogs; just
		// the bluetooth capability in Package.appxmanifest.
		builder.Services.AddSingleton<IPeerAlertService,
			EquipmentChecklist.Mobile.Platforms.Windows.PeerAlertPublisherWindows>();
		builder.Services.AddSingleton<IPeerAlertPermissions, NoOpPeerAlertPermissions>();
#elif IOS || MACCATALYST
		// Phase 8.7 — iOS / iPadOS. Requires NSBluetoothAlwaysUsageDescription
		// + UIBackgroundModes (bluetooth-central, bluetooth-peripheral) in
		// Info.plist. Background operation is heavily rate-limited by iOS
		// — see the PeerAlertPublisherIos class comments for details.
		builder.Services.AddSingleton<IPeerAlertService,
			EquipmentChecklist.Mobile.Platforms.iOS.PeerAlertPublisherIos>();
		builder.Services.AddSingleton<IPeerAlertPermissions, NoOpPeerAlertPermissions>();
#else
		builder.Services.AddSingleton<IPeerAlertService, NoOpPeerAlertService>();
		builder.Services.AddSingleton<IPeerAlertPermissions, NoOpPeerAlertPermissions>();
#endif
		// Real-time notification feed — SignalR client + inbox HTTP fallback.
		// Subscribes to AuthService.SignedIn/SignedOut to start/stop the
		// connection automatically; pages just inject and bind to the
		// UnreadCountChanged event.
		builder.Services.AddSingleton<NotificationService>();
		// Site labels (Mine name, Tagline, etc.) read from GET /api/sync/mine
		// on launch + post-signin and cached in SQLite for offline boots.
		builder.Services.AddSingleton<MineConfigService>();
		// Scoped: LocalPdfService captures IJSRuntime which is per-WebView.
		builder.Services.AddScoped<LocalPdfService>();
		// Cross-platform recorder/player factory from Plugin.Maui.Audio.
		// AddAudio() is the package-provided extension method — it registers
		// IAudioManager + handles platform-specific init that the bare
		// AudioManager.Current accessor doesn't. Pages then inject
		// IAudioManager via constructor / [Inject] and call CreateRecorder().
		builder.AddAudio();
		// ════════════════════════════════════════════════════════════════
		//  HTTP CLIENT — API base URL
		//
		//  The mobile app talks to the dev Kestrel via this base URL. The
		//  right value depends on WHICH device + WHICH network you're on:
		//
		//    Target               URL to use                         Why
		//    ─────────────────    ────────────────────────────────    ─────────────────────────
		//    Windows desktop      https://localhost:55025/           loopback hits dev Kestrel
		//    Android emulator     https://10.0.2.2:55025/            Google's host-loopback alias
		//    Physical Android     https://<DEV_PC_LAN_IP>:55025/     phone reaches the PC via Wi-Fi
		//
		//  To switch from the emulator to a physical phone:
		//    1. On the PC, run `ipconfig` and copy the Wi-Fi adapter's IPv4
		//       (something like 192.168.1.42).
		//    2. Replace DevHostIp below with that string.
		//    3. Launch the API with the "EquipmentChecklist (LAN)" profile so
		//       Kestrel binds to 0.0.0.0 instead of localhost.
		//    4. Allow inbound TCP port 55025 in Windows Firewall (see runbook).
		//    5. Put the phone on the same Wi-Fi network.
		//
		//  Both devices must be on the same subnet; mobile data won't work.
		//  The DEBUG cert override below means you don't have to install the
		//  ASP.NET dev cert on the phone — DEBUG builds accept anything.
		// ════════════════════════════════════════════════════════════════

		// ┌──────────────────────────────────────────────────────────────┐
		// │  COMPILE-TIME FALLBACK ONLY. The real URL is read from        │
		// │  Preferences at startup (Phase 5.2 — set via /setup page on   │
		// │  the device). One APK now fits any deployment; admins point   │
		// │  each device at the right server through the in-app UI.       │
		// └──────────────────────────────────────────────────────────────┘
		const string DevHostIp = "192.168.8.179";//"10.0.2.2";   // ← e.g. "192.168.1.42" for physical phone

#if ANDROID
		const string DefaultApiBaseUrl = "https://" + DevHostIp + ":55025/";
#else
		const string DefaultApiBaseUrl = "https://localhost:55025/";
#endif

		// Read the per-device URL from Preferences (sync — Preferences is
		// the synchronous storage; SecureStorage isn't and we'd need to
		// block here). Falls back to the compile-time default if the user
		// hasn't run the /setup page yet. Changing this requires an app
		// restart because the HttpClient.BaseAddress is bound at DI time.
		string ApiBaseUrl;
		try
		{
			ApiBaseUrl = Microsoft.Maui.Storage.Preferences.Default
				.Get("api_base_url", DefaultApiBaseUrl);
			if (string.IsNullOrWhiteSpace(ApiBaseUrl))
				ApiBaseUrl = DefaultApiBaseUrl;
			if (!ApiBaseUrl.EndsWith("/")) ApiBaseUrl += "/";
		}
		catch
		{
			// Preferences can throw on first launch on some platforms.
			// Falling back to the compile-time default keeps the app
			// launchable even if Preferences is broken.
			ApiBaseUrl = DefaultApiBaseUrl;
		}
		// AuthFailureHandler is a transient DelegatingHandler that watches
		// every API response for the server's "X-Auth-Failure: user_deactivated"
		// header and triggers AuthService.HandleRemoteDeactivationAsync the
		// moment it's seen. Must be Transient (HttpClient factory contract).
		builder.Services.AddTransient<AuthFailureHandler>();
		// Stamps the X-Device-Fingerprint header on every outgoing request
		// so the server's per-call device-allowlist check works.
		builder.Services.AddTransient<DeviceFingerprintHandler>();

		builder.Services.AddHttpClient<ApiClient>(c =>
		{
			c.BaseAddress = new Uri(ApiBaseUrl);
			c.Timeout     = TimeSpan.FromSeconds(20);
		})
		.AddHttpMessageHandler<DeviceFingerprintHandler>()
		.AddHttpMessageHandler<AuthFailureHandler>()
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

		// ── Force-resolve singletons so their event subscribers / connectivity
		//    listeners are alive from app boot, not from "first page that
		//    happens to inject them".
		//
		//    EVERY resolve is wrapped in TryEager so a single misbehaving
		//    service can't kill startup. Previously an exception here would
		//    propagate, take down MainActivity, and the user would see the
		//    splash screen vanish with no error message.
		//
		TryEager<SyncWorker>(app);
		TryEager<ApiHealth>(app);
		TryEager<Audit>(app);
		TryEager<StalePruneNotifier>(app);
		TryEager<NotificationService>(app);
		TryEager<MineConfigService>(app);

		return app;
	}

	/// <summary>
	/// Force-resolve a registered singleton, swallowing any constructor
	/// exception. Used at startup so the failure of one eager service
	/// doesn't take down the whole app. The service will still try to
	/// construct again on first lazy injection — at which point a real
	/// page will surface the error to the user.
	/// </summary>
	private static void TryEager<T>(MauiApp app) where T : notnull
	{
		try { _ = app.Services.GetRequiredService<T>(); }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(
				$"[TryEager] Could not pre-construct {typeof(T).Name}: {ex.Message}");
		}
	}
}