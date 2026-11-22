using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using SecureSync.App.Security;
using SecureSync.Core.Crypto;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;

namespace SecureSync.App;

/// <summary>
/// "SecureSync.exe --self-test report.txt" checks, without opening a window or prompting, that this build works on this
/// PC: the encrypted store round-trips, key sealing (DPAPI) works, and whether Windows Hello is set up.
/// </summary>
internal static class SelfTest
{
    /// <summary>Stands in for Windows Hello so the self-test never shows a prompt.</summary>
    private sealed class LocalSigner : IKeySigner
    {
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        public Task<SignerAvailability> CheckAvailabilityAsync() => Task.FromResult(SignerAvailability.Available);
        public Task<byte[]> SignAsync(byte[] data, CancellationToken ct) => Task.FromResult(HMACSHA512.HashData(_secret, data));
    }

    public static int Run(string reportPath)
    {
        var report = new List<string>();
        var tmp = Path.Combine(Path.GetTempPath(), "securesync-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var key = ChainKey.Generate();
            report.Add($"chain key: {key.Reveal().Length} chars, starts with {key.Reveal()[..2]}, fingerprint {key.Fingerprint}");

            var timer = Stopwatch.StartNew();
            var vaultDir = Path.Combine(tmp, "vault");
            var content = RandomNumberGenerator.GetBytes(3_000_000);
            Directory.CreateDirectory(tmp);
            var source = Path.Combine(tmp, "sample.bin");
            File.WriteAllBytes(source, content);

            ObjectId fileId;
            using (var vault = Vault.Open(vaultDir, key, "self-test"))
            {
                var folder = vault.CreateFolder(null, "Folder");
                fileId = vault.AddFile(source, folder.Id).Id;
                vault.Rename(fileId, "renamed.bin");
                vault.Move([fileId], null);
            }
            using (var vault = Vault.Open(vaultDir, key, "self-test"))
            {
                var output = Path.Combine(tmp, "roundtrip.bin");
                vault.Export(fileId, output);
                if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(content)) throw new Exception("round trip mismatch");
                if (vault.Snapshot().Get(fileId)?.Name != "renamed.bin" || vault.Snapshot().Get(fileId)?.Parent is not null)
                    throw new Exception("rename/move not persisted");
            }
            report.Add($"encrypt -> rename -> move -> lock -> unlock -> decrypt: identical ({timer.ElapsedMilliseconds} ms)");

            var signer = new LocalSigner();
            var dpapi = new DpapiProtector();
            var profile = KeyProtection.SealAsync(key, vaultDir, "self-test", signer, dpapi).GetAwaiter().GetResult();
            using (var unsealed = KeyProtection.UnsealAsync(profile, signer, dpapi).GetAwaiter().GetResult())
                if (!unsealed.SameChainAs(key)) throw new Exception("key sealing round trip failed");
            report.Add("key sealing (Windows Hello-style wrap + DPAPI): ok");

            var hello = Task.Run(() => new WindowsHelloSigner().CheckAvailabilityAsync()).GetAwaiter().GetResult();
            report.Add($"windows hello: {hello}");
            report.Add("RESULT: OK");
            return 0;
        }
        catch (Exception e)
        {
            report.Add("RESULT: FAILED");
            report.Add(e.ToString());
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
            File.WriteAllLines(reportPath, report);
        }
    }
}
