// Generic VRM 0.x and 1.0 runtime support only. Never reads/imports a user's model.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

public static class BuildVrmRuntime
{
    const string Generated = "Assets/VrmRuntimeSupport";
    static readonly string[] RuntimeNames = { "VRM", "VRM10", "FastSpringBone10", "VrmLib", "VRM10.MToon10", "UniGLTF", "UniGLTF.Utils", "UniGLTF.UniUnlit", "UniHumanoid", "MToon", "SpringBoneJobs", "Unity.Mathematics" };
    // Shipped by the game itself (same Unity version); packaging a second copy would conflict.
    static readonly string[] HostProvided = { "Unity.Timeline" };
    [Serializable] class Entry { public string file, sha256; public string[] references; }
    [Serializable] class Evidence {
        public int schemaVersion = 1;
        public string unityVersion, uniVrmVersion = "0.131.3", sourceCommit = "9750dadc592f421c356bf0376c73e61abbe323e3";
        public string runtimeAcceptance = "not-tested", bundleSha256;
        public bool containsAvatarData = false;
        public Entry[] assemblies;
        public string[] shaders, assets;
    }
    public static void BuildBatch()
    {
        try { Build(); Debug.Log("VRM_RUNTIME_BUILD_SUCCESS"); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
    static void Build()
    {
        if (Application.unityVersion != "2022.3.8f1") throw new InvalidOperationException("Unity 2022.3.8f1 required.");
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null) throw new InvalidOperationException("Built-in renderer required.");
        var args = Environment.GetCommandLineArgs();
        var pos = Array.IndexOf(args, "-runtimeOutput");
        if (pos < 0 || pos + 1 == args.Length) throw new ArgumentException("-runtimeOutput required.");
        var output = Path.GetFullPath(args[pos + 1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Output must be new/empty.");
        Directory.CreateDirectory(output);
        // Player compilation excludes every UNITY_EDITOR branch and Editor assembly.
        var compiled = Path.Combine("Library", "VrmRuntimePlayerAssemblies");
        var settings = new ScriptCompilationSettings { target = BuildTarget.StandaloneWindows64, group = BuildTargetGroup.Standalone, options = ScriptCompilationOptions.None };
        var compilation = PlayerBuildInterface.CompilePlayerScripts(settings, compiled);
        if (compilation.assemblies == null || compilation.assemblies.Count == 0) throw new InvalidOperationException("Player compilation failed.");
        var managed = Path.Combine(output, "managed"); Directory.CreateDirectory(managed);
        // Cecil rewrites without symbols, removing compiler debug paths rather than publishing PDBs.
        var cecil = Assembly.LoadFrom(Path.Combine(EditorApplication.applicationContentsPath, "Managed", "Unity.Cecil.dll"));
        var definition = cecil.GetType("Mono.Cecil.AssemblyDefinition", true);
        var resolverType = cecil.GetType("Mono.Cecil.DefaultAssemblyResolver", true);
        var resolver = Activator.CreateInstance(resolverType);
        foreach (var directory in new[] { Path.GetFullPath(compiled), Path.Combine(EditorApplication.applicationContentsPath, "Managed"), Path.Combine(EditorApplication.applicationContentsPath, "Managed/UnityEngine"), Path.GetDirectoryName(typeof(object).Assembly.Location) })
            resolverType.GetMethod("AddSearchDirectory").Invoke(resolver, new object[] { directory });
        var parametersType = cecil.GetType("Mono.Cecil.ReaderParameters", true);
        var parameters = Activator.CreateInstance(parametersType);
        parametersType.GetProperty("AssemblyResolver").SetValue(parameters, resolver);
        var read = definition.GetMethod("ReadAssembly", new[] { typeof(string), parametersType });
        var write = definition.GetMethod("Write", new[] { typeof(string) });
        var entries = new List<Entry>();
        foreach (var name in RuntimeNames)
        {
            var source = Path.Combine(compiled, name + ".dll");
            if (!File.Exists(source)) throw new FileNotFoundException("Missing player dependency", name);
            var destination = Path.Combine(managed, name + ".dll");
            var assembly = read.Invoke(null, new object[] { source, parameters });
            try { write.Invoke(assembly, new object[] { destination }); } finally { ((IDisposable)assembly).Dispose(); }
            var refs = Assembly.Load(File.ReadAllBytes(destination)).GetReferencedAssemblies().Select(x => x.Name).OrderBy(x => x).ToArray();
            if (refs.Any(x => x.StartsWith("UnityEditor", StringComparison.Ordinal))) throw new InvalidOperationException("Editor dependency leaked: " + name);
            foreach (var dep in refs)
                if (!RuntimeNames.Contains(dep) && !HostProvided.Contains(dep) && !dep.StartsWith("UnityEngine", StringComparison.Ordinal) && !dep.StartsWith("System", StringComparison.Ordinal) && dep != "mscorlib" && dep != "netstandard")
                    throw new InvalidOperationException("Unpackaged dependency: " + dep);
            entries.Add(new Entry { file = "managed/" + name + ".dll", sha256 = Hash(destination), references = refs });
        }
        Directory.CreateDirectory(Generated); AssetDatabase.Refresh();
        var assets = new List<string>();
        // Enumerate official shaders only; never enumerate Assets or user model resources.
        foreach (var package in new[] { "Packages/com.vrmc.gltf", "Packages/com.vrmc.univrm", "Packages/com.vrmc.vrm" })
            assets.AddRange(AssetDatabase.FindAssets("t:Shader", new[] { package }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(x => !x.Contains("/Editor/") && !x.Contains("/Samples"))
                // The game uses the built-in renderer; URP variants cannot compile here.
                .Where(x => !AssetDatabase.LoadAssetAtPath<Shader>(x).name.Contains("Universal Render Pipeline")));
        var shaderNames = new List<string>();
        foreach (var path in assets) shaderNames.Add(AssetDatabase.LoadAssetAtPath<Shader>(path).name);
        foreach (var name in new[] { "Standard", "Unlit/Color", "Unlit/Texture", "Unlit/Transparent", "Unlit/Transparent Cutout" })
        {
            var shader = Shader.Find(name); if (!shader) throw new InvalidOperationException("Missing built-in shader: " + name);
            var path = Generated + "/" + name.Replace('/', '_').Replace(' ', '_') + ".mat";
            if (!File.Exists(path)) AssetDatabase.CreateAsset(new Material(shader), path);
            assets.Add(path); shaderNames.Add(name);
        }
        if (!shaderNames.Contains("VRM/MToon") || !shaderNames.Contains("VRM10/MToon10") || !shaderNames.Contains("UniGLTF/UniUnlit") || !shaderNames.Contains("Hidden/UniGLTF/StandardMapImporter")) throw new InvalidOperationException("Required generic import shaders missing.");
        AssetDatabase.SaveAssets();
        foreach (var dependency in AssetDatabase.GetDependencies(assets.ToArray(), true))
            if (dependency.StartsWith("Assets/", StringComparison.Ordinal) && !dependency.StartsWith(Generated + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Non-generic asset dependency: " + dependency);
        var build = new AssetBundleBuild { assetBundleName = "vrm-runtime-shaders.bundle", assetNames = assets.OrderBy(x => x).ToArray() };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (!manifest) throw new InvalidOperationException("Generic shader bundle build failed.");
        var evidence = new Evidence { unityVersion = Application.unityVersion, assemblies = entries.ToArray(), shaders = shaderNames.Distinct().OrderBy(x => x).ToArray(), assets = build.assetNames,
            bundleSha256 = Hash(Path.Combine(output, build.assetBundleName)) };
        File.WriteAllText(Path.Combine(output, "runtime-support-evidence.json"), JsonUtility.ToJson(evidence, true));
    }
    static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
}
