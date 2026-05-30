namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Thin abstraction over each platform's biometric/PIN prompt:
///
///   Windows  → Windows.Security.Credentials.UI.UserConsentVerifier
///              (Windows Hello: face, fingerprint, PIN fallback)
///   Android  → TODO Phase 2E+ (AndroidX BiometricPrompt)
///   iOS      → TODO Phase 2E+ (LAContext)
///
/// All callers use <see cref="IsAvailableAsync"/> + <see cref="PromptAsync"/>.
/// Anything that isn't wired up yet returns <see cref="BiometricResult.NotAvailable"/>
/// so <see cref="AuthService"/> can degrade gracefully and just hand back the
/// stored token without a prompt.
/// </summary>
public class BiometricUnlock
{
    /// <summary>Quick check the Login page uses to decide whether to even
    /// show the "Use biometrics next time" checkbox.</summary>
    public async Task<bool> IsAvailableAsync()
    {
#if WINDOWS
        try
        {
            var availability = await Windows.Security.Credentials.UI
                .UserConsentVerifier.CheckAvailabilityAsync();
            return availability == Windows.Security.Credentials.UI
                .UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    /// <summary>Pop the platform's biometric/PIN prompt with the supplied
    /// human reason. Caller decides what to do on cancel/fail/not-available.</summary>
    public async Task<BiometricResult> PromptAsync(string reason)
    {
#if WINDOWS
        try
        {
            var result = await Windows.Security.Credentials.UI
                .UserConsentVerifier.RequestVerificationAsync(reason);

            return result switch
            {
                Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified
                    => BiometricResult.Success,
                Windows.Security.Credentials.UI.UserConsentVerificationResult.Canceled
                    => BiometricResult.Cancelled,
                Windows.Security.Credentials.UI.UserConsentVerificationResult
                    .RetriesExhausted
                    => BiometricResult.Failed,
                _   => BiometricResult.Failed
            };
        }
        catch
        {
            return BiometricResult.Failed;
        }
#else
        await Task.CompletedTask;
        return BiometricResult.NotAvailable;
#endif
    }
}

public enum BiometricResult
{
    /// <summary>User confirmed (biometric or PIN). Proceed.</summary>
    Success,
    /// <summary>User dismissed / hit cancel. Don't sign them out.</summary>
    Cancelled,
    /// <summary>Retries exhausted, hardware error, etc. Sign out.</summary>
    Failed,
    /// <summary>Platform doesn't expose biometrics. Treat as no-op.</summary>
    NotAvailable
}
