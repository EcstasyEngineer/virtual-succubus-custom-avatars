using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EcstasyEngineer.AvatarReplacement
{
    // Local build sidecars identify a prepared model. They are not a signature or
    // permission to load untrusted bundles. Bind still checks the actual clone.
    [DataContract]
    internal sealed class FacialProfile
    {
        [DataMember(IsRequired = true)] public int schemaVersion { get; set; }
        [DataMember(IsRequired = true)] public string modelId { get; set; }
        [DataMember(IsRequired = true)] public string mapping { get; set; }
        [DataMember(IsRequired = true)] public string jawPolicy { get; set; }
        [DataMember(IsRequired = true)] public string bundleSha256 { get; set; }

        internal static string VerifyBundle(string bundlePath)
        {
            string digest;
            using (var file = File.OpenRead(bundlePath))
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            // Preserve the previously audited bundle without requiring new files.
            if (digest == AniFacialAdapter.ApprovedBundleSha256) return "ani-05-kimono (legacy audited bundle)";
            var path = bundlePath + ".face.json";
            if (!File.Exists(path)) throw new InvalidDataException("Missing facial build profile beside this bundle.");
            FacialProfile profile;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 1 || file.Length > 4096) throw new InvalidDataException("Facial profile must be 1 to 4096 bytes.");
                var serializer = new DataContractJsonSerializer(typeof(FacialProfile), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 16 });
                profile = serializer.ReadObject(file) as FacialProfile;
            }
            Validate(profile, digest);
            return profile.modelId;
        }

        internal static void Validate(FacialProfile profile, string actualDigest)
        {
            if (profile == null || profile.schemaVersion != 1 || profile.mapping != "ani-arkit-v1" || profile.jawPolicy != "prefab-rest")
                throw new InvalidDataException("Unsupported facial profile schema, mapping or jaw policy.");
            if (profile.modelId != "ani-01-classic" && profile.modelId != "ani-05-kimono")
                throw new InvalidDataException("This facial mapping supports Ani Model 01 and kimono only.");
            if (profile.bundleSha256 == null || !Regex.IsMatch(profile.bundleSha256, "\\A[0-9a-fA-F]{64}\\z") ||
                !string.Equals(profile.bundleSha256, actualDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Facial profile does not match the bundle bytes; rebuild the matching pair.");
        }
    }
}
