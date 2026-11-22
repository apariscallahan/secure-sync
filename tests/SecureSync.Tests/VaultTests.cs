using System.Text;
using SecureSync.Core.Crypto;
using SecureSync.Core.Security;
using SecureSync.Core.Storage;

namespace SecureSync.Tests;

public class VaultTests
{
    private static Vault Open(string dir, ChainKey key, string name = "PC-A") => Vault.Open(dir, key, name);

    [Fact]
    public void Files_and_folders_survive_lock_and_unlock()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var dir = tmp.Sub("vault");
        using (var vault = Open(dir, key))
        {
            var photos = vault.CreateFolder(null, "Photos");
            var trip = vault.CreateFolder(photos.Id, "Iceland");
            vault.AddFile(tmp.WriteFile("glacier.jpg", "ice"), trip.Id);
            vault.AddFile(tmp.WriteFile("notes.txt", "hello"), null);
            vault.CreateFolder(null, "Empty folder");
        }
        using (var vault = Open(dir, key))
        {
            Assert.Equal(new HashSet<string> { "Photos/", "Photos/Iceland/", "Photos/Iceland/glacier.jpg", "notes.txt", "Empty folder/" }, vault.Paths());
            Assert.Equal("ice", vault.ReadText("Photos/Iceland/glacier.jpg"));
            Assert.Equal("PC-A", vault.Find("notes.txt").CreatedBy);
        }
    }

    [Fact]
    public void Rename_move_and_delete()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var vault = Open(tmp.Sub("vault"), key);
        var docs = vault.CreateFolder(null, "Docs");
        var archive = vault.CreateFolder(null, "Archive");
        var file = vault.AddFile(tmp.WriteFile("draft.txt", "v1"), docs.Id);

        vault.Rename(file.Id, "final.txt");
        Assert.Contains("Docs/final.txt", vault.Paths());

        vault.Move([file.Id], archive.Id);
        Assert.Contains("Archive/final.txt", vault.Paths());

        vault.Move([docs.Id], archive.Id);
        Assert.Contains("Archive/Docs/", vault.Paths());

        vault.Move([file.Id], null);
        Assert.Contains("final.txt", vault.Paths());

        Assert.Equal(2, vault.Delete([archive.Id])); // folder + its subfolder
        Assert.Equal(new HashSet<string> { "final.txt" }, vault.Paths());
    }

    [Fact]
    public void Invalid_and_duplicate_names_are_refused()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var vault = Open(tmp.Sub("vault"), key);
        var a = vault.AddFile(tmp.WriteFile("a.txt", "a"), null);
        vault.AddFile(tmp.WriteFile("b.txt", "b"), null);

        Assert.Throws<VaultException>(() => vault.Rename(a.Id, "b.txt"));
        Assert.Throws<VaultException>(() => vault.Rename(a.Id, "B.TXT"));
        Assert.Throws<VaultException>(() => vault.Rename(a.Id, "bad/name"));
        Assert.Throws<VaultException>(() => vault.CreateFolder(null, "a.txt"));
        vault.Rename(a.Id, "A.txt"); // changing only the case is fine
        Assert.Contains("A.txt", vault.Paths());
    }

    [Fact]
    public void A_folder_cannot_be_moved_into_itself()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var vault = Open(tmp.Sub("vault"), key);
        var outer = vault.CreateFolder(null, "Outer");
        var inner = vault.CreateFolder(outer.Id, "Inner");
        Assert.Throws<VaultException>(() => vault.Move([outer.Id], inner.Id));
        Assert.Throws<VaultException>(() => vault.Move([outer.Id], outer.Id));
    }

    [Fact]
    public void Moving_onto_a_taken_name_keeps_both()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var vault = Open(tmp.Sub("vault"), key);
        var folder = vault.CreateFolder(null, "Folder");
        vault.AddFile(tmp.WriteFile("report.pdf", "inside"), folder.Id);
        var outside = vault.AddFile(tmp.WriteFile("report.pdf", "outside"), null);
        vault.Move([outside.Id], folder.Id);
        Assert.Equal("outside", vault.ReadText("Folder/report (2).pdf"));
    }

    [Fact]
    public void Replacing_a_file_keeps_its_identity_and_drops_old_contents()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var dir = tmp.Sub("vault");
        using var vault = Open(dir, key);
        var file = vault.AddFile(tmp.WriteFile("doc.txt", "old"), null);
        var replaced = vault.AddFile(tmp.WriteFile("doc.txt", "new contents"), null, replace: file.Id);
        Assert.Equal(file.Id, replaced.Id);
        Assert.Equal("new contents", vault.ReadText("doc.txt"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "objects", "blobs")));
    }

    [Fact]
    public void Folder_contains_no_plaintext()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var dir = tmp.Sub("vault");
        using var vault = Open(dir, key);
        var folder = vault.CreateFolder(null, "Private stuff");
        vault.AddFile(tmp.WriteFile("Secret plans.txt", "launch codes 12345"), folder.Id);

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock")) continue;
            Assert.DoesNotContain("Secret", file);
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("launch codes", text);
            Assert.DoesNotContain("Secret plans", text);
            Assert.DoesNotContain("Private stuff", text);
            Assert.DoesNotContain(key.Reveal(), text);
        }
    }

    [Fact]
    public void Folder_of_another_chain_is_rejected()
    {
        using var tmp = new TempDir();
        var dir = tmp.Sub("vault");
        using (var key = ChainKey.Generate()) Open(dir, key).Dispose();
        using var other = ChainKey.Generate();
        var e = Assert.Throws<VaultException>(() => Open(dir, other));
        Assert.Contains("different sync chain", e.Message);
    }

    [Fact]
    public void Folder_cannot_be_opened_twice()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var dir = tmp.Sub("vault");
        using var first = Open(dir, key);
        Assert.Throws<VaultException>(() => Open(dir, key));
    }

    [Fact]
    public void Damaged_files_are_found_and_removed_so_they_can_be_downloaded_again()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var dir = tmp.Sub("vault");
        using var vault = Open(dir, key);
        var good = vault.AddFile(tmp.WriteFile("good.txt", "fine"), null);
        var bad = vault.AddFile(tmp.WriteFile("bad.txt", new string('x', 5000)), null);
        var badPath = Path.Combine(dir, "objects", "blobs", bad.Blob + ".ssblob");
        var bytes = File.ReadAllBytes(badPath);
        bytes[200] ^= 0xFF;
        File.WriteAllBytes(badPath, bytes);

        Assert.Equal((2, 1), vault.VerifyAll());
        Assert.False(vault.Snapshot().Get(bad.Id)!.IsAvailable);
        Assert.True(vault.Snapshot().Get(good.Id)!.IsAvailable);
        Assert.Single(vault.GetMissingBlobs());
        Assert.Throws<ContentUnavailableException>(() => vault.Export(bad.Id, Path.Combine(tmp.Path, "out.txt")));
    }

    [Fact]
    public void Opened_files_are_decrypted_to_temp_and_cleaned_up_on_lock()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        string tempPath;
        using (var vault = Open(tmp.Sub("vault"), key))
        {
            var entry = vault.AddFile(tmp.WriteFile("photo.jpg", "jpeg bytes"), null);
            tempPath = vault.DecryptToTemp(entry.Id);
            Assert.Equal("jpeg bytes", File.ReadAllText(tempPath));
        }
        Assert.False(File.Exists(tempPath));
    }
}

/// <summary>Conflicting changes made on two computers while apart, merged by exchanging records directly.</summary>
public class MergeTests
{
    private static void Exchange(Vault from, Vault to)
    {
        foreach (var (id, _) in from.GetEntryInventory())
            to.ApplyEntry(from.ReadEntryBytes(id)!, source: null);
    }

    [Fact]
    public void Newest_change_wins_and_both_sides_agree()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var a = Vault.Open(tmp.Sub("a"), key, "A");
        using var b = Vault.Open(tmp.Sub("b"), key, "B");
        var file = a.AddFile(tmp.WriteFile("plan.txt", "x"), null);
        Exchange(a, b);

        a.Rename(file.Id, "plan from A.txt");
        b.Rename(file.Id, "plan from B.txt");
        b.Rename(file.Id, "plan from B v2.txt"); // B made more changes, so its clock is ahead
        Exchange(a, b);
        Exchange(b, a);

        Assert.Equal(a.Paths(), b.Paths());
        Assert.Contains("plan from B v2.txt", a.Paths());
    }

    [Fact]
    public void Folders_moved_into_each_other_on_two_computers_do_not_disappear()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var a = Vault.Open(tmp.Sub("a"), key, "A");
        using var b = Vault.Open(tmp.Sub("b"), key, "B");
        var x = a.CreateFolder(null, "X");
        var y = a.CreateFolder(null, "Y");
        Exchange(a, b);

        a.Move([x.Id], y.Id); // on A: X inside Y
        b.Move([y.Id], x.Id); // on B: Y inside X
        Exchange(a, b);
        Exchange(b, a);

        Assert.Equal(a.Paths(), b.Paths());
        Assert.Equal(2, a.Snapshot().Items.Count);
        Assert.Contains(a.Paths(), p => p.Count(c => c == '/') == 1); // one of them ended up at the top level
    }

    [Fact]
    public void A_file_added_to_a_folder_deleted_elsewhere_is_kept()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        using var a = Vault.Open(tmp.Sub("a"), key, "A");
        using var b = Vault.Open(tmp.Sub("b"), key, "B");
        var parent = a.CreateFolder(null, "Parent");
        var child = a.CreateFolder(parent.Id, "Child");
        Exchange(a, b);

        a.Delete([child.Id]);
        b.AddFile(tmp.WriteFile("late.txt", "added while apart"), child.Id);
        Exchange(a, b);
        Exchange(b, a);

        Assert.Equal(a.Paths(), b.Paths());
        Assert.Contains("Parent/late.txt", a.Paths());
    }
}

public class KeyStoreTests
{
    [Fact]
    public async Task Sealed_key_opens_only_with_the_same_windows_hello_key()
    {
        using var key = ChainKey.Generate();
        var signer = new FakeSigner();
        var profile = await KeyProtection.SealAsync(key, @"C:\Vault", "PC", signer);
        Assert.DoesNotContain(key.Reveal(), System.Text.Json.JsonSerializer.Serialize(profile));
        Assert.Equal(key.Fingerprint, profile.Fingerprint);

        using var unsealed = await KeyProtection.UnsealAsync(profile, signer);
        Assert.True(unsealed.SameChainAs(key));

        // A different Windows Hello key (e.g. after a PIN reset) can't open it.
        await Assert.ThrowsAsync<KeyStoreException>(() => KeyProtection.UnsealAsync(profile, new FakeSigner()));
    }

    [Fact]
    public async Task Tampered_profiles_are_rejected()
    {
        using var key = ChainKey.Generate();
        var signer = new FakeSigner();
        var profile = await KeyProtection.SealAsync(key, @"C:\Vault", "PC", signer);
        var sealedBytes = Convert.FromBase64String(profile.SealedKey);
        sealedBytes[^1] ^= 1;
        await Assert.ThrowsAsync<KeyStoreException>(() =>
            KeyProtection.UnsealAsync(profile with { SealedKey = Convert.ToBase64String(sealedBytes) }, signer));
        await Assert.ThrowsAsync<KeyStoreException>(() => KeyProtection.UnsealAsync(profile with { Fingerprint = "0000-0000-0000" }, signer));
    }

    [Fact]
    public async Task Cancelling_windows_hello_cancels_unlock()
    {
        using var key = ChainKey.Generate();
        var signer = new FakeSigner();
        var profile = await KeyProtection.SealAsync(key, @"C:\Vault", "PC", signer);
        signer.Cancel = true;
        await Assert.ThrowsAsync<KeyStoreCanceledException>(() => KeyProtection.UnsealAsync(profile, signer));
    }

    private sealed class XorProtector : ILocalProtector
    {
        public int Calls;
        public byte[] Protect(byte[] data, byte[] entropy) { Calls++; return Xor(data, entropy); }
        public byte[] Unprotect(byte[] data, byte[] entropy) { Calls++; return Xor(data, entropy); }
        private static byte[] Xor(byte[] data, byte[] entropy) => data.Select((b, i) => (byte)(b ^ entropy[i % entropy.Length])).ToArray();
    }

    [Fact]
    public async Task Platform_protector_is_applied_when_given()
    {
        using var key = ChainKey.Generate();
        var signer = new FakeSigner();
        var protector = new XorProtector();
        var profile = await KeyProtection.SealAsync(key, @"C:\Vault", "PC", signer, protector);
        await Assert.ThrowsAsync<KeyStoreException>(() => KeyProtection.UnsealAsync(profile, signer)); // without the platform seal
        using var unsealed = await KeyProtection.UnsealAsync(profile, signer, protector);
        Assert.True(unsealed.SameChainAs(key));
        Assert.Equal(2, protector.Calls);
    }

    [Fact]
    public async Task Profile_round_trips_through_disk()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        var signer = new FakeSigner();
        var path = Path.Combine(tmp.Path, "profile.json");
        (await KeyProtection.SealAsync(key, @"C:\Vault", "Office PC", signer) with { SyncEnabled = false }).Save(path);
        var loaded = LocalProfile.Load(path)!;
        Assert.Equal("Office PC", loaded.DeviceName);
        Assert.False(loaded.SyncEnabled);
        using var unsealed = await KeyProtection.UnsealAsync(loaded, signer);
        Assert.True(unsealed.SameChainAs(key));
    }
}
