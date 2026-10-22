using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.Render {
    /// <summary>Caches the banger data (tune/banger/*.dgBangerData) of the props.</summary>
    public static class BangerStore {
        static readonly Dictionary<string, BangerData> cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new();
        static BangerData defaults;
        static bool defaultsLoaded;

        public static void Flush() {
            lock (sync) {
                cache.Clear();
                defaults = null;
                defaultsLoaded = false;
            }
        }

        /// <summary>
        /// Offset from a banger's position to the origin of its meshes (its centre of gravity) in object space; zero when it has
        /// no banger data. Safe to call from any thread.
        /// </summary>
        public static Vector3 GetCG(string name) {
            var data = Get(name);
            return data == null ? Vector3.Zero : data.CG.ToGodot();
        }

        static BangerData Get(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            lock (sync) {
                if (cache.TryGetValue(name, out var cached)) return cached;
            }
            var data = Load(name, Defaults());
            lock (sync) {
                cache[name] = data;
            }
            return data;
        }

        static BangerData Defaults() {
            lock (sync) {
                if (defaultsLoaded) return defaults;
            }
            var data = Load(BangerData.DefaultName, null);
            lock (sync) {
                defaults = data;
                defaultsLoaded = true;
            }
            return data;
        }

        static BangerData Load(string name, BangerData fallback) {
            foreach (string tune in Settings.TuneSearchPaths) {
                string path = Path.Combine(tune, BangerData.Folder, name + BangerData.Extension);
                if (!File.Exists(path)) continue;
                try {
                    return BangerData.Load(path, fallback);
                } catch (Exception e) {
                    GD.PushWarning($"Banger data {name}: {e.Message}");
                }
            }
            return null;
        }
    }
}
