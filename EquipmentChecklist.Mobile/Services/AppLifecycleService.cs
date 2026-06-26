namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Phase 8.6 — Centralised app-lifecycle event bus. Wraps the MAUI
/// platform-lifecycle hooks (which are wired in <see cref="MauiProgram"/>
/// via <c>ConfigureLifecycleEvents</c>) into plain C# events that Blazor
/// components can subscribe to without each one re-implementing the
/// per-platform plumbing.
///
/// <para>Today this exposes a single event — <see cref="Resumed"/> —
/// which fires every time the app returns to the foreground from
/// background (operator opened OS Settings then came back; pulled down
/// the Android notification shade then dismissed it; long-pressed home
/// then returned; etc.). Future events (Started, Stopped, Backgrounded)
/// can be added the same way without touching subscribers.</para>
///
/// <para>Why a service + event vs polling: the Phase 8.5 permission
/// banner needs to re-evaluate its visibility when the operator returns
/// from OS Settings after granting permissions. Polling every navigation
/// is wasteful; subscribing to a real resume signal is exact + cheap.</para>
/// </summary>
public class AppLifecycleService
{
    /// <summary>Fires when the app returns to the foreground. Subscribers
    /// must not assume any specific thread — wrap any UI work in
    /// <c>InvokeAsync(StateHasChanged)</c> or equivalent.</summary>
    public event Action? Resumed;

    /// <summary>Called by the platform lifecycle handlers in
    /// <see cref="MauiProgram"/>. Internal to make the trigger surface
    /// explicit — random code shouldn't be firing fake resume events.</summary>
    internal void RaiseResumed()
    {
        try { Resumed?.Invoke(); }
        catch { /* never let a subscriber's exception bubble up the lifecycle pipeline */ }
    }
}
