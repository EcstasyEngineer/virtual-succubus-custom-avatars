using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace EcstasyEngineer.AvatarReplacement
{
    // Raw VRM files are discovered directly. Legacy manifests retain experimental FBX bundles.
    internal sealed class ModelCatalog
    {
        internal const int MaxFiles = 32, MaxManifestBytes = 16 * 1024;

        [DataContract]
        public sealed class Entry
        {
            [DataMember(Name = "id", IsRequired = true)] public string Id;
            [DataMember(Name = "name", IsRequired = true)] public string Name;
            [DataMember(Name = "bundle", IsRequired = true)] public string Bundle;
            [DataMember(Name = "prefab", IsRequired = true)] public string Prefab;
            [DataMember(Name = "format")] public string Format = null;
            public string SupportLabel => Format == "vrm" ? "VRM file: direct import" : "FBX / bundle: experimental";
            // Resolved data is deliberately not part of the manifest contract.
            public string BundlePath;
            public string ManifestPath;
        }

        private readonly string directory;
        private readonly ManualLogSource log;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<string> errors = new List<string>();
        public IReadOnlyList<Entry> Entries => entries;
        public IReadOnlyList<string> Errors => errors;
        public string DirectoryPath => directory;

        public ModelCatalog(string bepRoot, ManualLogSource log)
        {
            if (string.IsNullOrWhiteSpace(bepRoot)) throw new ArgumentException("BepInEx root is required.", nameof(bepRoot));
            directory = Path.GetFullPath(Path.Combine(bepRoot, "avatars"));
            this.log = log;
        }

        public void Refresh()
        {
            entries.Clear(); errors.Clear();
            try
            {
                RequireLocalPath(directory);
                RejectLinks(directory);
                if (!Directory.Exists(directory))
                {
                    Error("Add local .vrm files to BepInEx/avatars, then Refresh.");
                    return;
                }
                // Take at most 33 names: a huge directory must not make Refresh unbounded.
                var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(f), ".vrm", StringComparison.OrdinalIgnoreCase)).Take(MaxFiles + 1).ToArray();
                if (files.Length > MaxFiles)
                {
                    Error("The catalog exceeds 32 model files/manifests; remove extras before refreshing. No models loaded.");
                    return;
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var candidates = new List<Entry>();
                foreach (var file in files)
                {
                    try { candidates.Add(string.Equals(Path.GetExtension(file), ".vrm", StringComparison.OrdinalIgnoreCase) ? ReadVrm(file) : Read(file)); }
                    catch (Exception error) { Error(Path.GetFileName(file) + ": " + SafeMessage(error.Message)); }
                }
                // Reject every participant in a conflict, rather than making file order select a winner.
                var duplicateIds = Duplicates(candidates.Select(e => e.Id));
                var duplicateNames = Duplicates(candidates.Select(e => e.Name));
                var duplicateTargets = Duplicates(candidates.Select(TargetKey));
                foreach (var entry in candidates)
                {
                    if (duplicateIds.Contains(entry.Id)) Error(Path.GetFileName(entry.ManifestPath) + ": duplicate model id " + entry.Id + ".");
                    else if (duplicateNames.Contains(entry.Name)) Error(Path.GetFileName(entry.ManifestPath) + ": duplicate display name " + entry.Name + ".");
                    else if (duplicateTargets.Contains(TargetKey(entry))) Error(Path.GetFileName(entry.ManifestPath) + ": duplicate bundle/prefab target.");
                    else entries.Add(entry);
                }
                if (files.Length == 0) Error("No .vrm files or experimental bundle manifests found in BepInEx/avatars.");
            }
            catch (Exception error) { entries.Clear(); Error("Catalog unavailable: " + SafeMessage(error.Message)); }
        }

        private Entry ReadVrm(string file)
        {
            RejectLinks(file);
            var path = Path.GetFullPath(file);
            RequireLocalPath(path);
            var length = new FileInfo(path).Length;
            if (length < 20 || length > 256L * 1024 * 1024) throw new InvalidDataException("VRM must be 20 bytes to 256 MiB.");
            return new Entry { Id = "vrm:" + Path.GetFileName(file), Name = Clean(Path.GetFileNameWithoutExtension(file), 80, "filename"),
                Format = "vrm", Bundle = Path.GetFileName(file), BundlePath = path, Prefab = "", ManifestPath = path };
        }

        private Entry Read(string file)
        {
            RejectLinks(file);
            Entry entry;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaxManifestBytes) throw new InvalidDataException("Manifest must contain 1 to 16384 bytes.");
                var serializer = new DataContractJsonSerializer(typeof(Entry), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 32 });
                entry = serializer.ReadObject(stream) as Entry;
            }
            if (entry == null) throw new InvalidDataException("Expected a JSON object.");
            entry.Id = Clean(entry.Id, 64, "id");
            if (!Regex.IsMatch(entry.Id, "^[A-Za-z0-9][A-Za-z0-9._-]*$") || string.Equals(entry.Id, "original", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("id must use letters, digits, dot, underscore or hyphen; original is reserved.");
            entry.Name = Clean(entry.Name, 80, "name");
            if (entry.Format != null && entry.Format != "vrm0" && entry.Format != "fbx") throw new InvalidDataException("Unsupported model format label.");
            entry.Bundle = Clean(entry.Bundle, 4096, "bundle");
            entry.Prefab = Clean(entry.Prefab, 512, "prefab");
            if (!entry.Prefab.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
                !entry.Prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || entry.Prefab.Contains("\\") ||
                entry.Prefab.Split('/').Any(p => p == "" || p == "." || p == ".."))
                throw new InvalidDataException("prefab must be an exact assets/.../*.prefab name inside the bundle.");
            if (entry.Bundle.Contains("://") || entry.Bundle.StartsWith("\\\\", StringComparison.Ordinal) || entry.Bundle.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidDataException("Only local bundle files are supported; URLs and network paths are rejected.");
            if (entry.Bundle.Contains(":") && !Regex.IsMatch(entry.Bundle, "^[A-Za-z]:[\\\\/]"))
                throw new InvalidDataException("Use an absolute local drive path or a path relative to the manifest.");
            if (entry.Bundle.IndexOf(':', 2) >= 0 || (Path.IsPathRooted(entry.Bundle) && !Regex.IsMatch(entry.Bundle, "^[A-Za-z]:[\\\\/]")))
                throw new InvalidDataException("Drive-relative paths and alternate data streams are not supported.");
            entry.ManifestPath = Path.GetFullPath(file);
            entry.BundlePath = Path.GetFullPath(Path.IsPathRooted(entry.Bundle) ? entry.Bundle : Path.Combine(Path.GetDirectoryName(file), entry.Bundle));
            RequireLocalPath(entry.BundlePath);
            var extension = Path.GetExtension(entry.BundlePath);
            if (!string.Equals(extension, ".bundle", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".assetbundle", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("bundle must be a prepared .bundle or .assetbundle file; raw FBX/VRM is unsupported.");
            RejectLinks(entry.BundlePath);
            if (!File.Exists(entry.BundlePath)) throw new FileNotFoundException("Bundle file does not exist.");
            return entry;
        }

        private static string Clean(string value, int maximum, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(field + " is required.");
            value = value.Trim();
            if (value.Length > maximum || value.Any(char.IsControl)) throw new InvalidDataException(field + " exceeds its length limit or contains control characters.");
            return value;
        }

        private static void RequireLocalPath(string path)
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || !Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]"))
                throw new InvalidDataException("A local Windows drive path is required.");
            if (new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network)
                throw new InvalidDataException("Network drives are not supported for model catalogs.");
        }

        private static void RejectLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked files or directories are not supported.");
        }

        private static HashSet<string> Duplicates(IEnumerable<string> values) => new HashSet<string>(
            values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        private static string TargetKey(Entry entry) => entry.BundlePath + "\n" + entry.Prefab;
        private static string SafeMessage(string value) => new string((value ?? "Unknown error").Where(c => !char.IsControl(c)).Take(220).ToArray());
        private void Error(string message) { errors.Add(message); log?.LogWarning("Avatar catalog: " + message); }
    }
}
