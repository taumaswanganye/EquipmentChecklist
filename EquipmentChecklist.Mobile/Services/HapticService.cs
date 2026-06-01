namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Thin wrapper around <see cref="Microsoft.Maui.Devices.HapticFeedback"/>
/// + <see cref="Microsoft.Maui.Devices.Vibration"/> so pages don't have to
/// catch <see cref="FeatureNotSupportedException"/> on every call.
///
/// <para>Why bother?</para>
/// <list type="bullet">
///   <item><description>Operators wear gloves — visual feedback alone is easy to miss.</description></item>
///   <item><description>Machinery noise drowns out audible cues but never touch.</description></item>
///   <item><description>Buzz tells the user "yes, the system received your tap" before any network round-trip.</description></item>
/// </list>
///
/// <para>Windows has no haptic motor on most devices, so this class no-ops
/// silently rather than throwing.</para>
/// </summary>
public class HapticService
{
    /// <summary>Quick light tap — use on selection / button presses.</summary>
    public void Light()        => Try(() => HapticFeedback.Default.Perform(HapticFeedbackType.Click));

    /// <summary>Stronger pulse — use on commit actions (Submit, Sign Off, Reject).</summary>
    public void Medium()       => Try(() => HapticFeedback.Default.Perform(HapticFeedbackType.LongPress));

    /// <summary>Two short pulses — use on success notifications.</summary>
    public void Success()
    {
        Try(() => HapticFeedback.Default.Perform(HapticFeedbackType.Click));
        // Tiny gap then a second tap so the operator clearly registers
        // "completed", not just "received".
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            Try(() => HapticFeedback.Default.Perform(HapticFeedbackType.Click));
        });
    }

    /// <summary>Long buzz — use on rejection / error so the operator can't miss it.</summary>
    public void Warning()
        => Try(() => Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(300)));

    private static void Try(Action action)
    {
        try { action(); }
        catch (FeatureNotSupportedException) { /* desktop / tablet without vibrator */ }
        catch { /* swallow — feedback is best-effort */ }
    }
}
