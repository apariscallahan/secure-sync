using System.Runtime.InteropServices;
using SecureSync.Core.Security;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace SecureSync.App.Security;

/// <summary>
/// Windows Hello for SecureSync. Windows keeps a per-app RSA key in the user's Windows Hello container (in the TPM
/// where the PC has one); it can only be used after the person verifies with their PIN, fingerprint or face. SecureSync
/// asks it to sign a fixed challenge — RSA PKCS#1 v1.5 signatures are deterministic — and derives from the signature
/// the key that unwraps the stored chain key. No new password or passkey: just the normal Windows Hello prompt.
/// </summary>
internal sealed class WindowsHelloSigner : IKeySigner
{
    private const string CredentialName = "SecureSync";

    public async Task<SignerAvailability> CheckAvailabilityAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync() ? SignerAvailability.Available : SignerAvailability.NotSetUp;
        }
        catch (Exception)
        {
            return SignerAvailability.NotSupported;
        }
    }

    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct)
    {
        using var focus = HelloPromptFocus.Start();
        var opened = await KeyCredentialManager.OpenAsync(CredentialName);
        if (opened.Status == KeyCredentialStatus.NotFound)
            opened = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.FailIfExists);
        ThrowIfFailed(opened.Status);

        var result = await opened.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(data));
        ThrowIfFailed(result.Status);
        CryptographicBuffer.CopyToByteArray(result.Result, out byte[] signature);
        return signature;
    }

    private static void ThrowIfFailed(KeyCredentialStatus status)
    {
        switch (status)
        {
            case KeyCredentialStatus.Success:
                return;
            case KeyCredentialStatus.UserCanceled:
                throw new KeyStoreCanceledException();
            case KeyCredentialStatus.UserPrefersPassword:
                throw new KeyStoreException("SecureSync needs Windows Hello (your PIN, fingerprint or face) to unlock. Choose one of those in the Windows Security prompt.");
            case KeyCredentialStatus.SecurityDeviceLocked:
                throw new KeyStoreException("Windows Hello is locked after too many attempts. Wait a little, or sign out of Windows and back in, then try again.");
            case KeyCredentialStatus.NotFound:
                throw new KeyStoreException("Windows Hello isn't set up on this computer. Set up a PIN in Settings › Accounts › Sign-in options, then try again.");
            default:
                throw new KeyStoreException($"Windows Hello couldn't verify you ({status}). Try again.");
        }
    }

    /// <summary>
    /// The Windows Hello prompt opened from a desktop app sometimes appears behind the app's window; this brings it
    /// to the front once it shows up.
    /// </summary>
    private sealed class HelloPromptFocus : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static HelloPromptFocus Start()
        {
            var focus = new HelloPromptFocus();
            var token = focus._cts.Token;
            _ = Task.Run(async () =>
            {
                var deadline = Environment.TickCount64 + 10_000;
                while (!token.IsCancellationRequested && Environment.TickCount64 < deadline)
                {
                    var prompt = FindWindow("Credential Dialog Xaml Host", null);
                    if (prompt != IntPtr.Zero)
                    {
                        SetForegroundWindow(prompt);
                        return;
                    }
                    try { await Task.Delay(100, token); } catch (OperationCanceledException) { return; }
                }
            }, token);
            return focus;
        }

        public void Dispose() => _cts.Cancel();
    }
}
