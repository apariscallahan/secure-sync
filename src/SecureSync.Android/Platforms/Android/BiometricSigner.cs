using Android.Security.Keystore;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Java.Lang;
using Javax.Crypto;
using SecureSync.Core.Security;
using Exception = System.Exception;

namespace SecureSync.Mobile;

/// <summary>
/// Android's equivalent of Windows Hello: an HMAC-SHA256 key kept in the Android Keystore (in secure hardware where
/// the phone has it) that can only be used right after the person passes BiometricPrompt — fingerprint, face, or the
/// phone's screen-lock PIN, pattern or password. HMAC is deterministic, so signing SecureSync's fixed challenge always
/// gives the same result, from which the key that unwraps the stored chain key is derived.
/// </summary>
internal sealed class BiometricSigner : IKeySigner
{
    private const string KeyAlias = "SecureSyncUnlockKey";

    // androidx.biometric.BiometricManager.Authenticators
    private const int BiometricStrong = 0x000F;
    private const int DeviceCredential = 0x8000;
    // android.security.keystore.KeyProperties
    private const int AuthBiometricStrong = 2;
    private const int AuthDeviceCredential = 1;

    private const int ErrorCanceled = 5;
    private const int ErrorUserCanceled = 10;
    private const int ErrorNegativeButton = 13;

    public Task<SignerAvailability> CheckAvailabilityAsync()
    {
        var result = BiometricManager.From(Android.App.Application.Context).CanAuthenticate(BiometricStrong | DeviceCredential);
        return Task.FromResult(result switch
        {
            BiometricManager.BiometricSuccess => SignerAvailability.Available,
            BiometricManager.BiometricErrorNoneEnrolled => SignerAvailability.NotSetUp, // no screen lock set
            _ => SignerAvailability.NotSupported,
        });
    }

    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct)
    {
        Mac mac;
        try
        {
            mac = Mac.GetInstance(KeyProperties.KeyAlgorithmHmacSha256)!;
            mac.Init(GetOrCreateKey());
        }
        catch (KeyPermanentlyInvalidatedException e)
        {
            // The screen lock was removed or reset: the old key is gone for good. Start a new one.
            DeleteKey();
            throw new KeyStoreException("This phone's screen lock was changed since SecureSync was set up, so the saved key can't be opened. Enter your sync chain key to set this phone up again.", e);
        }

        var authorized = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var activity = Platform.CurrentActivity as FragmentActivity
                           ?? throw new KeyStoreException("SecureSync isn't in the foreground.");
            var done = new TaskCompletionSource<Mac>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity)!, new Callback(done));
            var info = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle("Unlock SecureSync")
                .SetSubtitle("Use your fingerprint, face or screen lock")
                .SetAllowedAuthenticators(BiometricStrong | DeviceCredential)
                .Build();
            prompt.Authenticate(info, new BiometricPrompt.CryptoObject(mac));
            ct.Register(() => MainThread.BeginInvokeOnMainThread(prompt.CancelAuthentication));
            return done.Task;
        });
        return authorized.DoFinal(data)!;
    }

    private static Java.Security.IKey GetOrCreateKey()
    {
        var store = Java.Security.KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        // Note: GetKey returns a generic IKey wrapper, so a managed `is ISecretKey` test never matches; it would make a
        // new key every time and overwrite the one the chain key was sealed with. Mac.Init only needs an IKey.
        if (store.ContainsAlias(KeyAlias) && store.GetKey(KeyAlias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmHmacSha256, "AndroidKeyStore")!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Sign)
            .SetUserAuthenticationRequired(true)
            // Every use needs a fresh fingerprint/face/screen-lock check (timeout 0).
            .SetUserAuthenticationParameters(0, AuthBiometricStrong | AuthDeviceCredential)
            // Adding a fingerprint shouldn't lock you out; the screen-lock credential already unlocks it anyway.
            .SetInvalidatedByBiometricEnrollment(false)
            .Build();
        generator.Init(spec);
        return generator.GenerateKey()!;
    }

    private static void DeleteKey()
    {
        try
        {
            var store = Java.Security.KeyStore.GetInstance("AndroidKeyStore")!;
            store.Load(null);
            store.DeleteEntry(KeyAlias);
        }
        catch (Exception) { /* nothing to delete */ }
    }

    private sealed class Callback(TaskCompletionSource<Mac> done) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            if (result.CryptoObject?.Mac is { } mac) done.TrySetResult(mac);
            else done.TrySetException(new KeyStoreException("The phone didn't unlock the key. Try again."));
        }

        public override void OnAuthenticationError(int errorCode, ICharSequence errString)
        {
            if (errorCode is ErrorCanceled or ErrorUserCanceled or ErrorNegativeButton)
                done.TrySetException(new KeyStoreCanceledException());
            else
                done.TrySetException(new KeyStoreException(errString?.ToString() ?? "Verification failed."));
        }

        // A single unrecognised fingerprint: the prompt stays open for another try.
        public override void OnAuthenticationFailed() { }
    }
}
