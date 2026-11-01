using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    // PNG/JPEG decoding runs on the main thread during VRM import (a typical model carries
    // 20-30 images, many 2048x2048). The first import decodes as usual and stores each sRGB
    // colour texture's decoded pixels; later imports upload those directly. Data is stored
    // uncompressed: Texture2D.Compress on the main thread made first loads ~3x slower.
    // Normal maps and linear data pass through untouched. Caching is best effort: any
    // failure decodes normally. The folder is capped by deleting the oldest entries.
    internal sealed class TextureCache : ITextureDeserializer
    {
        private const int Magic = 0x54415356, Version = 1, HeaderInts = 6;
        private const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;
        private readonly string directory;
        private readonly ITextureDeserializer decoder = new UnityTextureDeserializer(ImportedTexturesAccessibility.Readable);
        internal int Hits { get; private set; }
        internal int Stored { get; private set; }
        internal int Uncached { get; private set; }

        internal TextureCache(string directory) { this.directory = directory; }

        public async Task<Texture2D> LoadTextureAsync(DeserializingTextureInfo info, IAwaitCaller awaitCaller)
        {
            if (info.ImportTypes != TextureImportTypes.sRGB || info.ColorSpace != UniGLTF.ColorSpace.sRGB)
            {
                Uncached++;
                return await decoder.LoadTextureAsync(info, awaitCaller);
            }
            var image = info.ImageData.ToArray();
            var mipmap = info.UseMipmap;
            var path = await awaitCaller.Run(() => Path.Combine(directory, Key(image, mipmap) + ".tex"));
            var cached = await awaitCaller.Run(() => Read(path));
            if (cached != null)
            {
                var texture = Create(cached, info);
                if (texture) { Hits++; return texture; }
                TryDelete(path);
            }
            var decoded = await decoder.LoadTextureAsync(info, awaitCaller);
            try
            {
                if (decoded && decoded.isReadable)
                {
                    var header = new[] { Magic, Version, decoded.width, decoded.height, decoded.mipmapCount, (int)decoded.format };
                    var raw = decoded.GetRawTextureData();
                    decoded.Apply(false, true);
                    await awaitCaller.Run(() => { Write(path, header, raw); return true; });
                    Stored++;
                }
                else Uncached++;
            }
            catch (Exception) { Uncached++; }
            return decoded;
        }

        private static string Key(byte[] image, bool mipmap)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(image);
                return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant() + (mipmap ? "m" : "n");
            }
        }

        private sealed class Entry { internal int[] header; internal byte[] data; }

        private static Entry Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    var header = new int[HeaderInts];
                    for (var i = 0; i < HeaderInts; i++) header[i] = reader.ReadInt32();
                    if (header[0] != Magic || header[1] != Version) return null;
                    var length = reader.BaseStream.Length - reader.BaseStream.Position;
                    if (length <= 0 || length > 256L * 1024 * 1024) return null;
                    return new Entry { header = header, data = reader.ReadBytes((int)length) };
                }
            }
            catch (IOException) { return null; }
        }

        private static Texture2D Create(Entry entry, DeserializingTextureInfo info)
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(entry.header[2], entry.header[3], (TextureFormat)entry.header[5], entry.header[4], false)
                    { filterMode = info.FilterMode, wrapModeU = info.WrapModeU, wrapModeV = info.WrapModeV };
                texture.LoadRawTextureData(entry.data);
                texture.Apply(false, true);
                return texture;
            }
            catch (Exception)
            {
                if (texture) UnityEngine.Object.Destroy(texture);
                return null;
            }
        }

        private void Write(string path, int[] header, byte[] data)
        {
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                foreach (var value in header) writer.Write(value);
                writer.Write(data);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
            Prune();
        }

        private void Prune()
        {
            var files = new DirectoryInfo(directory).GetFiles("*.tex");
            var total = files.Sum(f => f.Length);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxCacheBytes) break;
                total -= file.Length;
                TryDelete(file.FullName);
            }
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    }
}
