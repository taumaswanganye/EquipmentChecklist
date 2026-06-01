#if ANDROID
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
#endif

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Thin abstraction over each platform's biometric / PIN prompt:
///
///   Windows  → Windows.Security.Credentials.UI.UserConsentVerifier
///              (Windows Hello: face, fingerprint, PIN fallback)
///   Android  → AndroidX BiometricPrompt with class-3 (Strong) biometric +
///              device-credential fallback (PIN / pattern / password).
///   iOS      → TODO (LocalAuthentication.LAContext)
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
#elif ANDROID
        await Task.CompletedTask;
        try
        {
            var ctx = Platform.CurrentActivity ?? Android.App.Application.Context;
            var manager = BiometricManager.From(ctx);
            // BiometricStrong = Class 3 — required for any cryptographic
            // tie-in. We don't bind a CryptoObject in PromptAsync today,
            // but enforcing Strong here keeps the door open for it later
            // without changing the availability contract.
            var result  = manager.CanAuthenticate(BiometricManager.Authenticators.BiometricStrong);
            return result == BiometricManager.BiometricSuccess;
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
#elif ANDROID
        // BiometricPrompt is callback-based — wrap it in a TCS so the rest
        // of the codebase can stay async/await without touching Android
        // listener internals.
        var tcs = new TaskCompletionSource<BiometricResult>();

        try
        {
            // Must execute on the activity's main thread; PromptAsync is
            // typically called from a Razor click handler which is already
            // marshalled to UI but assert it explicitly.
            var activity = Platform.CurrentActivity as FragmentActivity
                ?? throw new InvalidOperationException(
                    "Current Activity is not a FragmentActivity — required by BiometricPrompt.");

            var executor = ContextCompat.GetMainExecutor(activity);
            var callback = new BiometricAuthCallback(tcs);
            var prompt   = new BiometricPrompt(activity, executor, callback);

            var info = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle("Pre-Checklist")
                .SetSubtitle(reason)
                .SetNegativeButtonText("Use password")
                .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong)
                .SetConfirmationRequired(false)
                .Build();

            // Marshalled to UI thread via the executor — but Authenticate
            // itself must be called from the UI thread.
            activity.RunOnUiThread(() => prompt.Authenticate(info));
            return await tcs.Task;
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

#if ANDROID
    /// <summary>
    /// Bridge between AndroidX's callback-based BiometricPrompt API and our
    /// Task-based <see cref="PromptAsync"/>. Completes the TCS exactly once
    /// — first callback wins.
    /// </summary>
    private sealed class BiometricAuthCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<BiometricResult> _tcs;
        public BiometricAuthCallback(TaskCompletionSource<BiometricResult> tcs)
            => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(BiometricResult.Success);

        public override void OnAuthenticationFailed()
        {
            // Fingerprint not recognised — but the system will retry.
            // Don't complete the TCS yet; wait for success / error / cancel.
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
        {
            // AndroidX biometric error codes — translate the user-cancellable
            // ones to Cancelled, everything else to Failed.
            var cancelled =
                errorCode == BiometricPrompt.ErrorNegativeButton  ||
                errorCode == BiometricPrompt.ErrorUserCanceled    ||
                errorCode == BiometricPrompt.ErrorCanceled;
            _tcs.TrySetResult(cancelled
                ? BiometricResult.Cancelled
                : BiometricResult.Failed);
        }
    }
#endif
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
