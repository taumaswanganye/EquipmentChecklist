using EquipmentChecklist.Mobile.Services;
using Microsoft.Extensions.Logging;

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
		builder.Services.AddHttpClient<ApiClient>(c =>
		{
			// Windows desktop → localhost is fine
			c.BaseAddress = new Uri("https://localhost:55025/");
			c.Timeout = TimeSpan.FromSeconds(20);
		});

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Force-instantiate SyncWorker so its connectivity listener is alive
		// from app start — without this the worker only wakes up the first time
		// some page injects it.
		_ = app.Services.GetRequiredService<SyncWorker>();

		return app;
	}
}