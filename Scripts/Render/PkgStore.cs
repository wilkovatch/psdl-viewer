using System;
using System.Collections.Generic;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.Render {
    /// <summary>CPU side mesh of a PKG, built off the main thread.</summary>
    public class PkgMeshData {
        public string Key;
        public PkgFile Pkg;
        public List<(PkgShader shader, TextureEntry texture, Vector3[] pos, Vector3[] nrm, Vector2[] uv)> Surfaces = new();
        public ArrayMesh Mesh;
    }

    /// <summary>Caches PKG files and the meshes built from them.</summary>
    public static class PkgStore {
        static readonly Dictionary<string, PkgFile> files = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PkgMeshData> meshes = new(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new();

        public static void Flush() {
            lock (sync) {
                files.Clear();
                missing.Clear();
                meshes.Clear();
            }
        }

        /// <summary>Drops the GPU meshes (e.g. after the lighting mode changed) but keeps the parsed files.</summary>
        public static void ResetMeshes() {
            lock (sync) {
                foreach (var m in meshes.Values) m.Mesh = null;
            }
        }

        /// <summary>Loads a PKG by name from the geometry folders; null when it doesn't exist or can't be read.</summary>
        public static PkgFile GetPkg(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            lock (sync) {
                if (files.TryGetValue(name, out var cached)) return cached;
                if (missing.Contains(name)) return null;
            }
            PkgFile pkg = null;
            string path = TextureStore.FindFile(name, Settings.Current.GeometryPaths, new[] { "pkg" });
            if (path != null) {
                try {
                    pkg = PkgFile.Load(path);
                } catch (Exception e) {
                    GD.PushWarning($"PKG {name}: {e.Message}");
                }
            }
            lock (sync) {
                if (pkg == null) missing.Add(name);
                else files[name] = pkg;
            }
            return pkg;
        }

        /// <summary>Builds the CPU mesh (any thread). Triangles are converted to Godot's clockwise winding.</summary>
        public static PkgMeshData GetMeshData(string name, int paintJob, int lod = PkgFile.LodHigh) {
            string key = $"{name}|{paintJob}|{lod}";
            lock (sync) {
                if (meshes.TryGetValue(key, out var cached)) return cached;
            }
            var pkg = GetPkg(name);
            if (pkg == null) return null;
            var data = new PkgMeshData { Key = key, Pkg = pkg };
            foreach (var s in pkg.BuildSurfaces(lod, paintJob)) {
                if (s.Indices.Count == 0) continue;
                int n = s.Indices.Count;
                var pos = new Vector3[n];
                var nrm = new Vector3[n];
                var uv = new Vector2[n];
                for (int i = 0; i < n; i += 3) {
                    // counter-clockwise (a, b, c) -> clockwise (a, c, b)
                    int[] order = { s.Indices[i], s.Indices[i + 2], s.Indices[i + 1] };
                    for (int k = 0; k < 3; k++) {
                        int v = order[k];
                        pos[i + k] = s.Positions[v].ToGodot();
                        nrm[i + k] = s.Normals[v].ToGodot();
                        uv[i + k] = new Vector2(s.UVs[v].X, s.UVs[v].Y);
                    }
                }
                var texture = s.Shader.IsTextured ? TextureStore.Get(s.Shader.Texture) : null;
                data.Surfaces.Add((s.Shader, texture, pos, nrm, uv));
            }
            lock (sync) {
                if (meshes.TryGetValue(key, out var other)) return other;
                meshes[key] = data;
            }
            return data;
        }

        /// <summary>Creates the GPU mesh; main thread only.</summary>
        public static ArrayMesh GetMesh(PkgMeshData data, Materials materials) {
            if (data == null) return null;
            if (data.Mesh != null) return data.Mesh;
            var mesh = new ArrayMesh();
            foreach (var (shader, texture, pos, nrm, uv) in data.Surfaces) {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = pos;
                arrays[(int)Mesh.ArrayType.Normal] = nrm;
                arrays[(int)Mesh.ArrayType.TexUV] = uv;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                var d = shader.Diffuse;
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materials.ForPkg(texture, new Color(d.X, d.Y, d.Z, 1)));
            }
            data.Mesh = mesh;
            return mesh;
        }

        public static Vector3 ToGodot(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    }
}
