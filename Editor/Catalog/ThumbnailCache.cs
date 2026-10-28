using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Product thumbnails for the list. <see cref="TryGet"/> never blocks: it returns the texture when it is loaded and
    /// otherwise null (draw a placeholder) while the file is read from the cache or downloaded in the background;
    /// <see cref="Changed"/> fires when a texture arrives so the window can repaint. Failures stay quiet and keep
    /// returning null for the rest of the editor session.
    /// Files live in the project's Library/OnAirDeck/Thumbnails: it is a regenerable cache that Unity and version
    /// control already ignore, it needs no extra permissions, and it goes away with the project. Cap
    /// <see cref="ThumbnailCachePolicy.MaxCacheBytes"/>, oldest use evicted first.
    /// </summary>
    internal static class ThumbnailCache
    {
        public static event Action Changed;

        private const int MaxActive = 3;
        private const int MaxTexturesInMemory = 128;

        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly List<string> TextureOrder = new List<string>();
        private static readonly HashSet<string> Pending = new HashSet<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly Queue<string> Waiting = new Queue<string>();
        private static int _active;
        private static string _root;

        static ThumbnailCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
        }

        /// <summary>Folder of the cached files (created on first write).</summary>
        public static string Root
        {
            get
            {
                if (_root == null) _root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "OnAirDeck", "Thumbnails");
                return _root;
            }
        }

        /// <summary>The thumbnail if it is ready, else null (a load is started once). Main thread, no blocking.</summary>
        public static Texture2D TryGet(string url)
        {
            if (!ThumbnailCachePolicy.IsAllowedUrl(url)) return null;
            Texture2D texture;
            if (Textures.TryGetValue(url, out texture))
            {
                if (texture != null) return texture;
                Textures.Remove(url);
                TextureOrder.Remove(url);
            }
            if (Failed.Contains(url) || Pending.Contains(url)) return null;
            Pending.Add(url);
            Waiting.Enqueue(url);
            Pump();
            return null;
        }

        private static void Pump()
        {
            while (_active < MaxActive && Waiting.Count > 0)
            {
                var url = Waiting.Dequeue();
                _active++;
                LoadAsync(url);
            }
        }

        private static async void LoadAsync(string url)
        {
            try
            {
                var path = Path.Combine(Root, ThumbnailCachePolicy.FileNameFor(url));
                var bytes = await Task.Run(() => ReadCached(path));
                var fromDisk = bytes != null;
                if (bytes == null) bytes = await DownloadAsync(url);
                var texture = bytes == null ? null : Decode(bytes);
                if (texture == null)
                {
                    if (fromDisk) await Task.Run(() => TryDelete(path));
                    Failed.Add(url);
                    return;
                }
                if (!fromDisk)
                {
                    var data = bytes;
                    await Task.Run(() => Save(path, data));
                }
                Store(url, texture);
                var handler = Changed;
                if (handler != null) handler();
            }
            catch (Exception)
            {
                Failed.Add(url); // a thumbnail is never worth an error in the Console
            }
            finally
            {
                Pending.Remove(url);
                _active--;
                Pump();
            }
        }

        private static async Task<byte[]> DownloadAsync(string url)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 15;
                await request.SendAsync();
                if (!request.Succeeded()) return null;
                var data = request.downloadHandler.data;
                return data != null && data.Length > 0 && data.Length <= ThumbnailCachePolicy.MaxFileBytes ? data : null;
            }
        }

        private static Texture2D Decode(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (texture.LoadImage(bytes)) return texture;
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        private static void Store(string url, Texture2D texture)
        {
            Textures[url] = texture;
            TextureOrder.Add(url);
            while (TextureOrder.Count > MaxTexturesInMemory)
            {
                var oldest = TextureOrder[0];
                TextureOrder.RemoveAt(0);
                Texture2D old;
                if (Textures.TryGetValue(oldest, out old))
                {
                    Textures.Remove(oldest);
                    if (old != null) UnityEngine.Object.DestroyImmediate(old);
                }
            }
        }

        private static void DestroyAll()
        {
            foreach (var texture in Textures.Values)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            Textures.Clear();
            TextureOrder.Clear();
        }

        // ---------- disk (runs on a worker thread) ----------

        private static byte[] ReadCached(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // the use time that eviction orders by
                return bytes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Save(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                Evict();
            }
            catch (Exception)
            {
                // A full disk or a locked file only means this thumbnail is not cached.
            }
        }

        private static void Evict()
        {
            var files = new List<ThumbnailFile>();
            foreach (var path in Directory.GetFiles(Root, "*.img"))
            {
                var info = new FileInfo(path);
                files.Add(new ThumbnailFile { Path = path, Size = info.Length, LastUsedUtc = info.LastWriteTimeUtc });
            }
            foreach (var doomed in ThumbnailCachePolicy.PlanEviction(files, ThumbnailCachePolicy.MaxCacheBytes)) TryDelete(doomed.Path);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
