using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using EcstasyEngineer.AvatarReplacement;

internal static class Program
{
    private static int assertions, scenarios, skippedLinks;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException("FAIL: " + label); assertions++; }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "avatar-catalog-test-" + Guid.NewGuid().ToString("N"));
        internal string Avatars => Path.Combine(Root, "avatars");
        internal Fixture() { Directory.CreateDirectory(Avatars); }
        internal string FileOfLength(string name, long size)
        {
            var path = Path.Combine(Avatars, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var file = File.Create(path); file.SetLength(size); return path;
        }
        internal void Manifest(string filename, string id, string name, string bundle = "sample.bundle", string prefab = "assets/model.prefab", string format = "fbx")
        {
            File.WriteAllText(Path.Combine(Avatars, filename), JsonSerializer.Serialize(new { id, name, bundle, prefab, format }));
        }
        internal ModelCatalog Read() { var catalog = new ModelCatalog(Root, null); catalog.Refresh(); return catalog; }
        public void Dispose()
        {
            // This uniquely created test root is the only recursive deletion target.
            var resolved = Path.GetFullPath(Root);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("avatar-catalog-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private static void Case(string name, Action<Fixture> body)
    { using var fixture = new Fixture(); body(fixture); scenarios++; }
    private static void Rejected(ModelCatalog catalog, string fragment, string label)
    { Check(catalog.Entries.Count == 0 && catalog.Errors.Any(e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase)), label); }

    private static void Main()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The production catalog requires Windows drive paths; run these filesystem tests on Windows.");
        Case("missing directory", f => { Directory.Delete(f.Avatars); Rejected(f.Read(), "Add local .vrm", "missing catalog gives actionable error"); });
        Case("empty directory", f => Rejected(f.Read(), "No .vrm", "empty catalog has no phantom entries"));
        Case("raw discovery", f => {
            var path = f.FileOfLength("Sample.vrm", 20); var catalog = f.Read();
            Check(catalog.Errors.Count == 0 && catalog.Entries.Count == 1, "raw VRM discovered without manifest");
            var item = catalog.Entries[0];
            Check(item.Id == "vrm:Sample.vrm" && item.Name == "Sample" && item.Format == "vrm", "raw VRM gets stable filename identity and format");
            Check(item.BundlePath == path && item.Prefab == "" && item.ManifestPath == path, "raw VRM needs no prefab or converted model path");
            Check(item.SupportLabel.Contains("direct import"), "raw entry advertises direct import");
        });
        Case("case and sort", f => {
            f.FileOfLength("z-last.VRM", 20); f.FileOfLength("a-first.vrm", 20);
            var catalog = f.Read();
            Check(catalog.Entries.Count == 2 && catalog.Entries[0].Name == "a-first" && catalog.Entries[1].Name == "z-last", "extension matching is case-insensitive and scan order deterministic");
        });
        Case("top-level only", f => {
            f.FileOfLength("nested/hidden.vrm", 20); f.FileOfLength("visible.vrm", 20);
            for (var i = 0; i < 40; i++) f.FileOfLength("note" + i + ".txt", 1);
            f.FileOfLength("not-imported.fbx", 20);
            var catalog = f.Read(); Check(catalog.Entries.Count == 1 && catalog.Entries[0].Name == "visible" && catalog.Errors.Count == 0, "nested models and unrelated files are not catalog entries or count-budget items");
        });
        Case("too small", f => { f.FileOfLength("tiny.vrm", 19); Rejected(f.Read(), "20 bytes", "VRM lower size bound enforced"); });
        Case("maximum length", f => {
            var path = f.FileOfLength("large.vrm", 256L * 1024 * 1024); Check(f.Read().Entries.Count == 1, "exact 256 MiB catalog limit accepted");
            using (var file = File.OpenWrite(path)) file.SetLength(256L * 1024 * 1024 + 1);
            Rejected(f.Read(), "256 MiB", "one byte above VRM limit rejected");
        });
        Case("filename bounds", f => { f.FileOfLength(new string('x', 81) + ".vrm", 20); Rejected(f.Read(), "length limit", "VRM display filename bound enforced"); });
        Case("maximum entries", f => {
            for (var i = 0; i < ModelCatalog.MaxFiles; i++) f.FileOfLength("model-" + i.ToString("D2") + ".vrm", 20);
            Check(f.Read().Entries.Count == 32, "exact 32 raw models accepted");
            f.FileOfLength("extra.vrm", 20); Rejected(f.Read(), "exceeds 32", "oversized catalog refuses whole listing");
        });
        Case("mixed entry budget", f => {
            for (var i = 0; i < 32; i++) f.FileOfLength("model-" + i.ToString("D2") + ".vrm", 20);
            f.Manifest("extra.json", "extra", "Extra"); Rejected(f.Read(), "exceeds 32", "VRM and JSON share a combined bound");
        });
        Case("experimental coexistence", f => {
            f.FileOfLength("raw.vrm", 20); var bundle = f.FileOfLength("sample.bundle", 1); f.Manifest("legacy.json", "legacy", "Experimental FBX");
            var catalog = f.Read(); Check(catalog.Errors.Count == 0 && catalog.Entries.Count == 2, "VRM and legacy FBX manifest coexist");
            var legacy = catalog.Entries.Single(e => e.Id == "legacy");
            Check(legacy.BundlePath == bundle && legacy.Prefab == "assets/model.prefab" && legacy.SupportLabel.Contains("experimental"), "FBX keeps prepared path and experimental label");
        });
        Case("bad entry isolation", f => {
            f.FileOfLength("valid.vrm", 20); File.WriteAllText(Path.Combine(f.Avatars, "bad.json"), "{broken");
            var catalog = f.Read(); Check(catalog.Entries.Count == 1 && catalog.Errors.Count == 1, "malformed manifest does not hide valid raw VRM");
        });
        Case("duplicate names", f => {
            f.FileOfLength("Sample.vrm", 20); f.FileOfLength("sample.bundle", 1); f.Manifest("legacy.json", "legacy", "sAmPlE");
            var catalog = f.Read(); Check(catalog.Entries.Count == 0 && catalog.Errors.Count == 2 && catalog.Errors.All(e => e.Contains("duplicate display name")), "raw/legacy display-name collision rejects both participants");
        });
        Case("duplicate ids", f => {
            f.FileOfLength("one.bundle", 1); f.FileOfLength("two.bundle", 1);
            f.Manifest("a.json", "DUP", "First", "one.bundle"); f.Manifest("b.json", "dup", "Second", "two.bundle");
            var catalog = f.Read(); Check(catalog.Entries.Count == 0 && catalog.Errors.Count == 2 && catalog.Errors.All(e => e.Contains("duplicate model id")), "legacy ids compare case-insensitively and reject both");
        });
        Case("duplicate targets", f => {
            f.FileOfLength("sample.bundle", 1); f.Manifest("a.json", "first", "First"); f.Manifest("b.json", "second", "Second", "SAMPLE.BUNDLE", "ASSETS/MODEL.PREFAB");
            var catalog = f.Read(); Check(catalog.Entries.Count == 0 && catalog.Errors.Count == 2 && catalog.Errors.All(e => e.Contains("duplicate bundle/prefab")), "legacy target collision rejects both participants");
        });
        Case("refresh stale state", f => {
            var path = f.FileOfLength("old.vrm", 19); var catalog = f.Read(); Check(catalog.Errors.Count == 1, "initial size failure observed");
            using (var file = File.OpenWrite(path)) file.SetLength(20);
            catalog.Refresh(); Check(catalog.Entries.Count == 1 && catalog.Errors.Count == 0, "Refresh clears stale error after file repair");
            File.Delete(path); f.FileOfLength("new.vrm", 20); catalog.Refresh();
            Check(catalog.Entries.Count == 1 && catalog.Entries[0].Name == "new", "Refresh replaces removed model entries");
        });
        foreach (var path in new[] { "https://example.invalid/model.bundle", "\\\\invalid-server\\share\\model.bundle", "//invalid-server/share/model.bundle", "C:model.bundle", "model.bundle:stream", "C:\\model.bundle:stream", "\\model.bundle" })
            Case("unsafe manifest path", f => { f.Manifest("bad.json", "bad", "Bad", path); Check(f.Read().Entries.Count == 0, "unsafe manifest path rejected before model loading"); });
        Case("missing legacy model", f => { f.Manifest("bad.json", "bad", "Bad"); Rejected(f.Read(), "does not exist", "missing prepared bundle rejected"); });
        Case("raw path not disguised as legacy", f => { f.FileOfLength("nested/raw.vrm", 20); f.Manifest("bad.json", "bad", "Bad", "nested/raw.vrm"); Rejected(f.Read(), "prepared", "legacy manifest cannot disguise raw VRM as a prepared bundle"); });
        Case("oversized manifest", f => { File.WriteAllText(Path.Combine(f.Avatars, "big.json"), new string(' ', ModelCatalog.MaxManifestBytes + 1)); Rejected(f.Read(), "16384", "manifest byte-size guard remains active"); });
        foreach (var prefab in new[] { "model.prefab", "assets/../model.prefab", "assets//model.prefab", "assets/model.fbx", "assets\\model.prefab" })
            Case("invalid prefab", f => { f.FileOfLength("sample.bundle", 1); f.Manifest("bad.json", "bad", "Bad", prefab: prefab); Rejected(f.Read(), "prefab", "invalid legacy prefab path rejected"); });
        Case("file symlink", f => {
            var target = f.FileOfLength("nested/real.vrm", 20); var link = Path.Combine(f.Avatars, "linked.vrm");
            try { File.CreateSymbolicLink(link, target); }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException || error is PlatformNotSupportedException)
            { skippedLinks++; Console.WriteLine("SKIP file symbolic-link creation unavailable: " + error.GetType().Name); return; }
            try { Rejected(f.Read(), "Linked", "linked raw VRM rejected"); }
            finally { File.Delete(link); }
        });
        Case("directory symlink", f => {
            var target = Path.Combine(f.Root, "real-avatars"); Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "model.vrm"), new byte[20]);
            Directory.Delete(f.Avatars);
            try { Directory.CreateSymbolicLink(f.Avatars, target); }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException || error is PlatformNotSupportedException)
            { skippedLinks++; Console.WriteLine("SKIP directory symbolic-link creation unavailable: " + error.GetType().Name); return; }
            try { Rejected(f.Read(), "Linked", "linked avatar directory rejected"); }
            finally { Directory.Delete(f.Avatars); }
        });
        Console.WriteLine(assertions + " model-catalog assertions passed across " + scenarios + " scenarios; skipped link scenarios=" + skippedLinks + ". No VRM content was parsed and no game/Unity scene ran.");
    }
}
