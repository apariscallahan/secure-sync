using System.Security.Cryptography;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;

namespace SecureSync.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "securesync-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string WriteFile(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, "src", Guid.NewGuid().ToString("N")[..6], name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Stands in for Windows Hello: a deterministic signature that only this "device" can produce.</summary>
internal sealed class FakeSigner : IKeySigner
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    public int Calls { get; private set; }
    public bool Cancel { get; set; }

    public Task<SignerAvailability> CheckAvailabilityAsync() => Task.FromResult(SignerAvailability.Available);

    public Task<byte[]> SignAsync(byte[] data, CancellationToken ct)
    {
        Calls++;
        if (Cancel) throw new KeyStoreCanceledException();
        return Task.FromResult(HMACSHA512.HashData(_secret, data));
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000, Func<string>? describe = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for: {what}. {describe?.Invoke()}");
            await Task.Delay(50);
        }
    }
}

internal static class VaultExtensions
{
    /// <summary>Full path of every live item, e.g. "Photos/Iceland/glacier.jpg", with folders marked by a trailing slash.</summary>
    public static HashSet<string> Paths(this Vault vault)
    {
        var snap = vault.Snapshot();
        return snap.Items.Select(i =>
            string.Join("/", snap.PathTo(i.Parent).Select(p => p.Name).Append(i.Name)) + (i.IsFolder ? "/" : "")).ToHashSet();
    }

    public static VaultItem Find(this Vault vault, string path)
    {
        var snap = vault.Snapshot();
        ObjectId? folder = null;
        VaultItem? item = null;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            item = snap.FindChild(folder, part) ?? throw new KeyNotFoundException(path);
            folder = item.Id;
        }
        return item ?? throw new KeyNotFoundException(path);
    }

    public static string ReadText(this Vault vault, string path)
    {
        var temp = System.IO.Path.GetTempFileName();
        try
        {
            vault.Export(vault.Find(path).Id, temp);
            return File.ReadAllText(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
