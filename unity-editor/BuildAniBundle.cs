// Copy into Assets/Editor of a NEW Unity 2022.3.8f1 Windows project.
// This helper only writes inside that project; it never edits the source FBX.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class BuildAniBundle
{
    private const string Destination = "Assets/AvatarReplacement";
    [Serializable] private sealed class PreparedModel { public string artifactName; public string modelId; public string sourceFbxSha256; public string humanoidMapSha256; }
    [Serializable] private sealed class FaceMesh { public string name; public string[] channels; public float[] defaultWeights; }
    [Serializable] private sealed class FaceBone { public string humanName, boneName; public Vector3 localPosition; public Quaternion localRotation; }
    [Serializable] private sealed class RuntimeFaceProfile
    {
        public int schemaVersion = 1;
        public string modelId, bundleSha256;
        public string mapping = "ani-arkit-v1", jawPolicy = "prefab-rest";
    }
    private static readonly string[] RequiredFaceChannels = {
        "browDownLeft", "browDownRight", "eyeSquintLeft", "eyeSquintRight", "eyeBlinkLeft", "eyeBlinkRight",
        "mouthFrownLeft", "mouthFrownRight", "jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthStretchLeft", "mouthStretchRight",
        "mouthSmileLeft", "mouthSmileRight", "browOuterUpLeft", "browOuterUpRight", "browInnerUp", "eyeWideLeft", "eyeWideRight"
    };
    [Serializable] private sealed class ModelProfile
    {
        public string unityVersion, modelId, sourceFbxSha256, humanoidMapSha256, bundleSha256, prefab;
        public bool humanoidValid;
        public FaceMesh[] facialMeshes;
        public FaceBone[] facialBones;
        public string validation = "Unity structural import only; runtime and visual calibration NOT RUN";
    }

    [MenuItem("Avatar Replacement/Prepare Ani Output Copy")]
    public static void Prepare()
    {
        if (Application.isBatchMode) throw new InvalidOperationException("Use PrepareBatch for noninteractive execution.");
        var source = EditorUtility.OpenFolderPanel("Select canonical Ani output directory", "", "");
        if (!string.IsNullOrEmpty(source)) PrepareCore(source);
    }

    public static void PrepareBatch() { Batch(() => PrepareCore(Argument("-avatarSource"))); }
    public static void BuildBatch() { Batch(() => BuildCore(Argument("-avatarOutput"))); }
    public static void PrepareAndBuildBatch()
    {
        Batch(() =>
        {
            var source = Argument("-avatarSource");
            var output = ValidateOutput(Argument("-avatarOutput"));
            PrepareCore(source);
            BuildCore(output);
        });
    }

    private static void Batch(Action action)
    {
        try { action(); Debug.Log("AVATAR_BUILD_SUCCESS"); if (Application.isBatchMode) EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); if (Application.isBatchMode) EditorApplication.Exit(1); else throw; }
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var indices = Enumerable.Range(0, args.Length).Where(i => args[i] == name).ToArray();
        if (indices.Length != 1 || indices[0] + 1 == args.Length || args[indices[0] + 1].StartsWith("-"))
            throw new ArgumentException("Required unique argument and value: " + name);
        return args[indices[0] + 1];
    }

    private static void PrepareCore(string source)
    {
        if (Directory.Exists(Destination)) throw new InvalidOperationException("Use a fresh project or remove the previous generated Assets/AvatarReplacement copy yourself.");
        source = Path.GetFullPath(source);
        var modelId = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!Regex.IsMatch(modelId, @"\A[a-z0-9][a-z0-9_-]{0,63}\z")) throw new InvalidOperationException("Source folder must have a simple lowercase model identifier.");
        var artifactName = modelId == "05-kimono" ? "ani-kimono" : "ani-" + modelId;
        var fbxName = modelId + ".fbx";
        var fbx = Path.Combine(source, fbxName);
        var mapPath = Path.Combine(source, "humanoid.json");
        var reportPath = Path.Combine(source, "report.json");
        if (!File.Exists(fbx) || !File.Exists(mapPath) || !File.Exists(reportPath)) throw new FileNotFoundException(fbxName + ", humanoid.json and report.json are required.");
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null) throw new InvalidOperationException("Use a fresh Built-in Render Pipeline project.");
        var materialMap = ReadFlatMap(reportPath, "materials");
        foreach (var pair in materialMap)
        {
            if (Path.GetFileName(pair.Value) != pair.Value || !File.Exists(Path.Combine(source, "textures", pair.Value)))
                throw new InvalidOperationException("Missing or unsafe mapped albedo filename: " + pair.Value);
        }
        Directory.CreateDirectory(Destination + "/Source");
        File.Copy(fbx, Destination + "/Source/" + fbxName);
        foreach (var folder in new[] { "textures", modelId + ".fbm" })
        {
            var from = Path.Combine(source, folder);
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar);
                var to = Path.Combine(Destination, "Source", folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to);
            }
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var path = Destination + "/Source/" + fbxName;
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.importBlendShapes = true;
        importer.importCameras = false;
        importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.SaveAndReimport();
        var imported = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var transforms = imported.GetComponentsInChildren<Transform>(true);
        var bones = new List<HumanBone>();
        foreach (var pair in ReadFlatMap(mapPath, "humanoid"))
        {
            var name = pair.Key;
            // HumanTrait uses spaces for finger names; the supplied map already does so.
            if (!HumanTrait.BoneName.Contains(name)) throw new InvalidOperationException("Unknown human bone: " + name);
            if (transforms.Count(t => t.name == pair.Value) != 1) throw new InvalidOperationException("Missing or ambiguous source bone: " + pair.Value);
            bones.Add(new HumanBone { humanName = name, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } });
        }
        if (bones.Count < 15) throw new InvalidOperationException("Incomplete humanoid map.");
        var description = importer.humanDescription;
        description.human = bones.ToArray();
        importer.humanDescription = description;
        importer.SaveAndReimport();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var copy = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            var animator = copy.GetComponent<Animator>();
            if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("Unity did not produce a valid Humanoid avatar. Inspect Rig > Configure and fix the COPY before building.");
            if (copy.GetComponentsInChildren<Animator>(true).Length != 1)
                throw new InvalidOperationException("Expected one root Animator.");
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.enabled = false;
            ApplyMaterials(copy, materialMap);
            foreach (var r in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;
            copy.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(copy, Destination + "/" + artifactName + ".prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(copy); }
        AssetDatabase.SaveAssets();
        File.WriteAllText(Destination + "/prepared-model.json", JsonUtility.ToJson(new PreparedModel { artifactName = artifactName, modelId = modelId, sourceFbxSha256 = Hash(fbx), humanoidMapSha256 = Hash(mapPath) }, true));
        File.WriteAllText(Destination + "/source-hashes.txt", "Unity " + Application.unityVersion + "\nFBX " + Hash(fbx) + "\nHumanoid map " + Hash(mapPath) + "\nReport " + Hash(reportPath) + "\n");
        Debug.Log("Prepared Ani copy. Inspect rig, scale, materials, textures and shader compatibility before using Build Bundle.");
    }

    [MenuItem("Avatar Replacement/Build Windows Bundle")]
    public static void Build()
    {
        BuildCore(Path.GetFullPath("AvatarBundleOutput"));
    }

    private static void BuildCore(string output)
    {
        output = ValidateOutput(output);
        var preparedPath = Destination + "/prepared-model.json";
        var prepared = File.Exists(preparedPath) ? JsonUtility.FromJson<PreparedModel>(File.ReadAllText(preparedPath)) :
            new PreparedModel { artifactName = "ani-kimono", modelId = "05-kimono" }; // Compatibility with earlier prepared projects.
        if (prepared == null || !Regex.IsMatch(prepared.artifactName ?? "", @"\A[a-z0-9][a-z0-9_-]{0,79}\z")) throw new InvalidOperationException("Invalid prepared model identity.");
        var prefabPath = Destination + "/" + prepared.artifactName + ".prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (!prefab || prefab.activeSelf || prefab.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
            throw new InvalidOperationException("An inactive, model-only prepared prefab is required.");
        var animator = prefab.GetComponent<Animator>();
        if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman || prefab.GetComponentsInChildren<Animator>(true).Length != 1)
            throw new InvalidOperationException("One valid Humanoid root Animator is required.");
        foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            foreach (var material in r.sharedMaterials)
                if (!material || !material.shader || !material.mainTexture) throw new InvalidOperationException("Missing material/shader/albedo on " + r.name);
        var faceSupported = prepared.modelId == "01-classic" || prepared.modelId == "05-kimono";
        if (faceSupported) ValidateFace(prefab, animator);
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild { assetBundleName = prepared.artifactName + ".bundle", assetNames = new[] { prefabPath } };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (!manifest) throw new InvalidOperationException("AssetBundle build failed.");
        var bundle = Path.Combine(output, prepared.artifactName + ".bundle");
        if (!File.Exists(bundle)) throw new InvalidOperationException("Bundle output missing despite manifest.");
        File.WriteAllText(Path.Combine(output, "build-evidence.txt"), File.ReadAllText(Destination + "/source-hashes.txt") + "Bundle " + Hash(bundle) + "\nHumanoid valid: true\nRuntime/appearance verification: NOT RUN\n");
        var profile = new ModelProfile { unityVersion = Application.unityVersion, modelId = prepared.modelId,
            sourceFbxSha256 = prepared.sourceFbxSha256, humanoidMapSha256 = prepared.humanoidMapSha256,
            bundleSha256 = Hash(bundle), prefab = prefabPath.ToLowerInvariant(), humanoidValid = true,
            facialMeshes = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh && r.sharedMesh.blendShapeCount > 0)
                .Select(r => new FaceMesh { name = r.name, channels = Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.sharedMesh.GetBlendShapeName).ToArray(),
                    defaultWeights = Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray() }).ToArray(),
            facialBones = new[] { HumanBodyBones.Head, HumanBodyBones.Jaw, HumanBodyBones.LeftEye, HumanBodyBones.RightEye }
                .Select(id => { var bone = animator.GetBoneTransform(id); return new FaceBone { humanName = id.ToString(), boneName = bone ? bone.name : null,
                    localPosition = bone ? bone.localPosition : Vector3.zero, localRotation = bone ? bone.localRotation : Quaternion.identity }; }).ToArray() };
        File.WriteAllText(Path.Combine(output, "model-profile.json"), JsonUtility.ToJson(profile, true));
        if (faceSupported)
            File.WriteAllText(bundle + ".face.json", JsonUtility.ToJson(new RuntimeFaceProfile { modelId = "ani-" + prepared.modelId, bundleSha256 = profile.bundleSha256 }, true));
        Debug.Log("Built " + bundle + ". No game installation or runtime verification performed.");
    }

    private static void ValidateFace(GameObject prefab, Animator animator)
    {
        var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var name in new[] { "Head", "Eye_L", "Eye_R" })
            if (renderers.Count(r => r.name == name && r.sharedMesh) != 1)
                throw new InvalidOperationException("Missing or ambiguous Ani face renderer: " + name);
        var head = renderers.Single(r => r.name == "Head");
        var channels = Enumerable.Range(0, head.sharedMesh.blendShapeCount).Select(head.sharedMesh.GetBlendShapeName).ToArray();
        foreach (var channel in RequiredFaceChannels)
            if (channels.Count(name => name == channel) != 1) throw new InvalidOperationException("Missing or ambiguous face channel: " + channel);
        foreach (var id in new[] { HumanBodyBones.Head, HumanBodyBones.Jaw, HumanBodyBones.LeftEye, HumanBodyBones.RightEye })
            if (!animator.GetBoneTransform(id)) throw new InvalidOperationException("Missing Ani humanoid facial bone: " + id);
    }

    private static string ValidateOutput(string output)
    {
        if (!Path.IsPathRooted(output)) throw new ArgumentException("-avatarOutput must be an absolute directory.");
        output = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(project, StringComparison.OrdinalIgnoreCase) ||
            (output + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must remain inside this disposable Unity project and outside Assets.");
        return output;
    }

    private static Dictionary<string, string> ReadFlatMap(string file, string property)
    {
        // Audited schema: a flat object of simple names. Reject escapes/duplicates.
        var match = Regex.Match(File.ReadAllText(file), "\"" + Regex.Escape(property) + "\"\\s*:\\s*\\{([^{}]*)\\}");
        if (!match.Success) throw new InvalidOperationException("Missing flat map: " + property);
        var pattern = "\"([^\"\\\\]+)\"\\s*:\\s*\"([^\"\\\\]+)\"";
        var body = match.Groups[1].Value;
        var result = new Dictionary<string, string>();
        foreach (Match pair in Regex.Matches(body, pattern)) result.Add(pair.Groups[1].Value, pair.Groups[2].Value);
        if (result.Count == 0 || Regex.Replace(Regex.Replace(body, pattern, ""), @"[\s,]", "").Length != 0)
            throw new InvalidOperationException("Unexpected map syntax: " + property);
        return result;
    }

    private static void ApplyMaterials(GameObject copy, Dictionary<string, string> map)
    {
        var shader = Shader.Find("Standard");
        if (!shader) throw new InvalidOperationException("Built-in Standard shader unavailable.");
        Directory.CreateDirectory(Destination + "/Materials");
        var materials = new Dictionary<string, Material>();
        foreach (var pair in map)
        {
            if (Path.GetFileName(pair.Key) != pair.Key) throw new InvalidOperationException("Unsafe material name.");
            var texturePath = Destination + "/Source/textures/" + pair.Value;
            var textureImporter = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            textureImporter.textureType = TextureImporterType.Default;
            textureImporter.sRGBTexture = true;
            textureImporter.SaveAndReimport();
            var material = new Material(shader) { name = pair.Key, mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) };
            if (!material.mainTexture) throw new InvalidOperationException("Albedo import failed: " + texturePath);
            material.SetFloat("_Metallic", 0); material.SetFloat("_Glossiness", 0.2f);
            AssetDatabase.CreateAsset(material, Destination + "/Materials/" + pair.Key + ".mat");
            materials.Add(pair.Key, material);
        }
        foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
        {
            var old = renderer.sharedMaterials;
            var assigned = new Material[old.Length];
            for (var i = 0; i < old.Length; i++)
            {
                if (!old[i]) throw new InvalidOperationException("Null material slot on " + renderer.name);
                if (!materials.TryGetValue(old[i].name, out assigned[i]) && !materials.TryGetValue(Regex.Replace(old[i].name, @"\.\d+$", ""), out assigned[i]))
                    throw new InvalidOperationException("No explicit albedo map for material " + old[i].name);
            }
            renderer.sharedMaterials = assigned;
        }
    }

    private static string Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
