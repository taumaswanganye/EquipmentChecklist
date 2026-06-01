namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Broadcasts in-app toast notifications. One singleton, every page injects
/// it and calls <see cref="Success"/> / <see cref="Error"/> / <see cref="Info"/>
/// / <see cref="Warning"/>. The Toast component in <c>MainLayout</c>
/// subscribes to <see cref="OnShow"/> and renders the stack.
///
/// <para>Why an event bus rather than a JS interop call?</para>
/// <list type="bullet">
///   <item><description>No round-trip to JS — toasts work even before the WebView has fully painted (e.g. during boot).</description></item>
///   <item><description>The toast queue UI lives in Razor so it can be styled with the rest of the app's CSS.</description></item>
///   <item><description>Easy to mock in tests — just substitute a stub <see cref="ToastService"/>.</description></item>
/// </list>
/// </summary>
public class ToastService
{
    public event Action<ToastMessage>? OnShow;

    /// <summary>Green toast — operation succeeded.</summary>
    public void Success(string message, int durationMs = 3000)
        => OnShow?.Invoke(new ToastMessage(message, ToastKind.Success, durationMs));

    /// <summary>Red toast — operation failed.</summary>
    public void Error(string message, int durationMs = 5000)
        => OnShow?.Invoke(new ToastMessage(message, ToastKind.Error, durationMs));

    /// <summary>Cyan toast — neutral / informational.</summary>
    public void Info(string message, int durationMs = 3000)
        => OnShow?.Invoke(new ToastMessage(message, ToastKind.Info, durationMs));

    /// <summary>Amber toast — non-fatal warning ("Saved offline, will sync later").</summary>
    public void Warning(string message, int durationMs = 4000)
        => OnShow?.Invoke(new ToastMessage(message, ToastKind.Warning, durationMs));
}

/// <summary>One queued toast. Auto-deduplicated by reference inside the
/// rendering component — fire twice in quick succession and you get
/// two separate cards.</summary>
public record ToastMessage(string Text, ToastKind Kind, int DurationMs);

public enum ToastKind { Success, Error, Info, Warning }
