using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Iris.Learning;
using Iris.Privacy;
using Iris.Translation;

namespace Iris.Tests;

/// <summary>The vault: private licensed pairs encrypted with a password, a part kept for measuring, sealed outputs, the wall.</summary>
public static class VaultTests
{
    private const string Password = "a long invented password 42";

    // invented sentence pairs (no real text)
    private static IEnumerable<(string, string)> Pairs(int count) =>
        Enumerable.Range(0, count).Select(i => ($"The lighthouse keeper counted {i} gulls.", $"Strážca majáka narátal {i} čajok."));

    [Test]
    public static void PairsGoInEncryptedAndComeOutWithThePasswordOnly()
    {
        var folder = Directory.CreateTempSubdirectory("iris-vault-").FullName;
        try
        {
            var path = Path.Combine(folder, "data.irisvault");
            int learn, measure;
            using (var key = Vault.NewKey(Password, iterations: 100_000))
            {
                (learn, measure) = Vault.CreatePairs(path, "private:test", Pairs(3000).Append(("  ", "prázdne")).Append(("tab\there", "riadok\nnový")), key);
            }

            Assert.Equal(3001, learn + measure, "every pair except the empty one");
            Assert.True(measure is > 20 and < 120, $"about 2 % kept for measuring ({measure})");
            var bytes = File.ReadAllBytes(path);
            Assert.True(Vault.IsVault(path) && !ContainsText(bytes, "lighthouse") && !ContainsText(bytes, "čajok"), "nothing of the text is readable on disk");
            var info = Vault.Info(path);
            Assert.True(info.Kind == "pairs" && info.Tag == "private:test" && info.Iterations == 100_000, "the header without the password");

            using (var key = Vault.Open(path, Password))
            {
                var learned = Vault.ReadPairs(path, key, VaultSection.Learn).ToList();
                var measured = Vault.ReadPairs(path, key, VaultSection.Measure).ToList();
                Assert.True(learned.Count == learn && measured.Count == measure, "both parts come back whole");
                Assert.True(learned.Intersect(measured).Count() == 0, "a pair is never in both parts");
                Assert.True(learned.Concat(measured).Contains(("tab here", "riadok nový")), "tabs and line ends of a pair become spaces");
                Assert.True(measured.All(p => Vault.ForMeasuring(p.Source, p.Target)), "the part is chosen by the pair itself (always the same)");
            }

            Assert.Throws<CryptographicException>(() => Vault.Open(path, "a wrong invented password"), "a wrong password");
            Assert.Throws<ArgumentException>(() => Vault.NewKey("short"), "a short password");

            // a changed byte, a cut end and something added after the end are all found
            foreach (var (name, change) in new (string, Func<byte[], byte[]>)[]
                     {
                         ("a changed byte", b => { var c = (byte[])b.Clone(); c[c.Length / 2] ^= 1; return c; }),
                         ("a cut end", b => b[..^40]),
                         ("data after the end", b => [.. b, 1, 2, 3]),
                     })
            {
                var damaged = Path.Combine(folder, "damaged.irisvault");
                File.WriteAllBytes(damaged, change(bytes));
                using var key = Vault.Open(damaged, Password);
                var failed = false;
                try
                {
                    _ = Vault.ReadPairs(damaged, key, VaultSection.Learn).Count();
                }
                catch (Exception e) when (e is CryptographicException or InvalidDataException)
                {
                    failed = true;
                }

                Assert.True(failed, $"{name} is found");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public static void WhatIsLearnedFromAVaultIsSealedAndTheWallHolds()
    {
        var folder = Directory.CreateTempSubdirectory("iris-vault-").FullName;
        try
        {
            using var tiny = TinyModel.Create(learnable: true);
            var translator = Translator.Load(tiny.Path);
            var adapter = Adapter.Create(translator.Model, "test", rank: 2, alpha: 4).With(new JsonObject { ["private"] = "private:test" });
            var sealedPath = Path.Combine(folder, "learned.sealed");
            var data = Path.Combine(folder, "data.irisvault");
            using (var key = Vault.NewKey(Password, iterations: 100_000))
            {
                Vault.CreatePairs(data, "private:test", Pairs(10), key);
            }

            // the adapter is sealed with the vault's key: the same password opens both
            using (var key = Vault.Open(data, Password))
            {
                Vault.Seal(sealedPath, "private:test", adapter.ToBytes(), key, new JsonObject { ["what"] = "adapter" });
            }

            Assert.Throws<FormatException>(() => Adapter.Load(sealedPath), "a sealed adapter never loads as a plain one");
            using (var key = Vault.Open(sealedPath, Password))
            {
                var opened = Adapter.Parse(Vault.Unseal(sealedPath, key));
                Assert.True(opened.Parameters == adapter.Parameters && opened.Layers.Keys.SequenceEqual(adapter.Layers.Keys), "it opens whole with the password");
                Assert.Throws<CryptographicException>(() => Vault.ReadPairs(data, Vault.NewKey(Password, 100_000), VaultSection.Learn).Count(), "a key of another salt opens nothing");
            }

            // the wall: a vault, a sealed file and a model learned from private data are all private; a public one is not
            var plain = Path.Combine(folder, "plain.iris");
            Adapter.Create(translator.Model, "test", rank: 2, alpha: 4).Save(plain);
            var marked = Path.Combine(folder, "marked.iris");
            adapter.Save(marked);
            Assert.True(Vault.HoldsPrivate(data) && Vault.HoldsPrivate(sealedPath) && Vault.HoldsPrivate(marked), "private files are known");
            Assert.True(!Vault.HoldsPrivate(plain) && !Vault.HoldsPrivate(tiny.Path), "public ones are not");
            Assert.Throws<ArgumentException>(() => Vault.Seal(Path.Combine(folder, "x.sealed"), "intercorp", [1, 2], Vault.Open(data, Password)), "a tag says private: or public:");
            Assert.True(!File.Exists(Path.Combine(folder, "x.sealed")) && !File.Exists(Path.Combine(folder, "x.sealed.part")), "nothing half-written stays");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool ContainsText(byte[] bytes, string text) =>
        bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;
}
