using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;
using PsdlViewer.Core;

namespace PsdlViewer.Render {
    /// <summary>
    /// Writes the city as glTF 2.0: a .gltf file, a .bin buffer next to it and the textures as upright PNG files in a
    /// &lt;name&gt;_textures folder. Every PKG mesh is stored once and shared by the nodes that place it.
    /// </summary>
    internal class GltfExporter {
        const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
        const int Float = 5126, UnsignedShort = 5123, UnsignedInt = 5125;

        internal class Options {
            public string Name;
            public PsdlFile Psdl;
            public Settings Settings;
            /// <summary>Layers to export; null leaves the layer out.</summary>
            public IReadOnlyCollection<CityScene.Placement> Inst, Pathsets, Props;
            public IReadOnlyCollection<MeshBatch> Decals;
            public bool Unlit;
        }

        readonly Options options;
        readonly string textureFolder, textureFolderName;
        readonly MemoryStream bin = new();
        readonly BinaryWriter writer;
        readonly JsonArray nodes = new(), meshes = new(), materials = new(), textures = new(), images = new(), accessors = new(), bufferViews = new();
        readonly Dictionary<(TextureEntry, Color), int> materialIndex = new();
        readonly Dictionary<TextureEntry, int> textureIndex = new();
        readonly Dictionary<string, int> pkgMeshIndex = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> imageFiles = new(StringComparer.OrdinalIgnoreCase);
        readonly SortedSet<string> missing = new(StringComparer.OrdinalIgnoreCase);

        GltfExporter(string path, Options options) {
            this.options = options;
            writer = new BinaryWriter(bin);
            textureFolderName = Path.GetFileNameWithoutExtension(path) + "_textures";
            textureFolder = Path.Combine(Path.GetDirectoryName(path) ?? "", textureFolderName);
        }

        /// <summary>Exports the city to path (any thread). Returns a summary for the status bar.</summary>
        public static string Export(string path, Options options, Progress progress) {
            var e = new GltfExporter(path, options);
            e.Write(path, progress);
            string summary = $"Exported {e.nodes.Count} nodes, {e.meshes.Count} meshes and {e.textures.Count} textures to {path}";
            if (e.missing.Count > 0) {
                GD.PushWarning($"glTF export: textures not found or unsupported, exported as plain white materials: {string.Join(", ", e.missing)}");
                summary += $" ({e.missing.Count} missing textures, see the console)";
            }
            return summary;
        }

        void Write(string path, Progress progress) {
            // the textured city, whatever colouring the view uses
            var settings = options.Settings.Clone();
            settings.ColouringMode = Settings.ColouringTextureMap;
            settings.RenderRoomAttributes = true;
            var city = CityBuilder.Build(options.Psdl, settings, progress);

            var rootChildren = new JsonArray();
            AddNode(new JsonObject { ["name"] = string.IsNullOrEmpty(options.Name) ? "City" : options.Name, ["children"] = rootChildren });

            var psdlChildren = Group(rootChildren, "PSDL");
            progress.Begin("Exporting PSDL geometry", city.Batches.Count);
            int i = 0;
            foreach (var batch in city.Batches.Values) {
                progress.Value = i++;
                AddBatch(psdlChildren, batch, string.IsNullOrEmpty(batch.Texture?.Name) ? "untextured" : batch.Texture.Name);
            }
            if (options.Inst != null) AddPlacements(Group(rootChildren, "INST"), options.Inst, progress, "Exporting INST");
            if (options.Pathsets != null) {
                var children = Group(rootChildren, "Pathsets");
                AddPlacements(children, options.Pathsets, progress, "Exporting pathsets");
                foreach (var batch in options.Decals ?? Array.Empty<MeshBatch>()) AddBatch(children, batch, "decal_" + batch.Texture?.Name);
            }
            if (options.Props != null) AddPlacements(Group(rootChildren, "Props"), options.Props, progress, "Exporting props");

            progress.Begin("Writing glTF", 1);
            writer.Flush();
            string binName = Path.GetFileNameWithoutExtension(path) + ".bin";
            var gltf = new JsonObject {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "PSDL Viewer" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) })
            };
            if (options.Unlit) gltf["extensionsUsed"] = new JsonArray("KHR_materials_unlit");
            Put(gltf, "nodes", nodes);
            Put(gltf, "meshes", meshes);
            Put(gltf, "materials", materials);
            Put(gltf, "textures", textures);
            Put(gltf, "images", images);
            if (textures.Count > 0) {
                gltf["samplers"] = new JsonArray(new JsonObject {
                    ["magFilter"] = 9729, // LINEAR
                    ["minFilter"] = 9987, // LINEAR_MIPMAP_LINEAR
                    ["wrapS"] = 10497, // REPEAT
                    ["wrapT"] = 10497
                });
            }
            Put(gltf, "accessors", accessors);
            Put(gltf, "bufferViews", bufferViews);
            if (bin.Length > 0) {
                gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length, ["uri"] = Uri.EscapeDataString(binName) });
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path) ?? "", binName), bin.ToArray());
            }
            File.WriteAllText(path, gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>glTF doesn't allow empty arrays.</summary>
        static void Put(JsonObject o, string key, JsonArray array) {
            if (array.Count > 0) o[key] = array;
        }

        int AddNode(JsonObject node) {
            nodes.Add(node);
            return nodes.Count - 1;
        }

        /// <summary>Adds an empty child node and returns its children array.</summary>
        JsonArray Group(JsonArray parent, string name) {
            var children = new JsonArray();
            parent.Add(AddNode(new JsonObject { ["name"] = name, ["children"] = children }));
            return children;
        }

        void AddBatch(JsonArray parent, MeshBatch batch, string name) {
            var primitive = Primitive(batch.Positions, batch.Normals, batch.UVs, batch.Colors, batch.Texture, Colors.White);
            if (primitive == null) return;
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = new JsonArray(primitive) });
            parent.Add(AddNode(new JsonObject { ["name"] = name, ["mesh"] = meshes.Count - 1 }));
        }

        void AddPlacements(JsonArray parent, IReadOnlyCollection<CityScene.Placement> placements, Progress progress, string note) {
            progress.Begin(note, placements.Count);
            int i = 0;
            foreach (var p in placements) {
                progress.Value = i++;
                int mesh = PkgMesh(p.Mesh);
                if (mesh < 0) continue;
                for (int k = 0; k < p.Transforms.Count; k++) {
                    var node = new JsonObject { ["name"] = $"{p.Mesh.Pkg.Name}_{k}", ["mesh"] = mesh };
                    if (p.Transforms[k] != Transform3D.Identity) node["matrix"] = Matrix(p.Transforms[k]);
                    parent.Add(AddNode(node));
                }
            }
        }

        /// <summary>Column major 4x4 matrix.</summary>
        static JsonArray Matrix(Transform3D t) {
            var b = t.Basis;
            var (x, y, z, o) = (b.Column0, b.Column1, b.Column2, t.Origin);
            return new JsonArray(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, o.X, o.Y, o.Z, 1f);
        }

        /// <summary>Mesh index of a PKG mesh (one primitive per surface), or -1 when it has no triangles.</summary>
        int PkgMesh(PkgMeshData data) {
            if (pkgMeshIndex.TryGetValue(data.Key, out int index)) return index;
            var primitives = new JsonArray();
            foreach (var (shader, texture, pos, nrm, uv) in data.Surfaces) {
                var d = shader.Diffuse;
                var primitive = Primitive(pos, nrm, uv, null, texture, new Color(d.X, d.Y, d.Z, 1));
                if (primitive != null) primitives.Add(primitive);
            }
            index = -1;
            if (primitives.Count > 0) {
                meshes.Add(new JsonObject { ["name"] = data.Pkg.Name, ["primitives"] = primitives });
                index = meshes.Count - 1;
            }
            pkgMeshIndex[data.Key] = index;
            return index;
        }

        // ---- geometry ----

        /// <summary>
        /// Encodes a clockwise (Godot) triangle soup as an indexed counter-clockwise glTF primitive, with identical vertices merged
        /// and v flipped for the upright PNGs. Returns null when there are no triangles.
        /// </summary>
        JsonObject Primitive(IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm, IReadOnlyList<Vector2> uv, IReadOnlyList<Color> col, TextureEntry texture, Color diffuse) {
            int n = pos.Count - pos.Count % 3;
            if (n == 0) return null;
            int material = Material(texture, diffuse);
            // untextured (or missing texture) materials don't need texture coordinates
            bool hasUV = uv != null && uv.Count >= n && texture != null && textureIndex[texture] >= 0;
            bool hasNormals = nrm != null && nrm.Count >= n;
            bool coloured = false;
            if (col != null && col.Count >= n) {
                for (int i = 0; i < n && !coloured; i++) coloured = col[i] != Colors.White;
            }
            var map = new Dictionary<(Vector3, Vector3, Vector2, Color), int>();
            var vp = new List<Vector3>();
            var vn = new List<Vector3>();
            var vt = new List<Vector2>();
            var vc = new List<Color>();
            var indices = new List<int>(n);
            for (int i = 0; i < n; i += 3) {
                for (int k = 0; k < 3; k++) {
                    // (a, b, c) -> (a, c, b)
                    int s = i + (3 - k) % 3;
                    var p = pos[s];
                    var normal = hasNormals ? nrm[s] : Vector3.Up;
                    normal = normal.IsFinite() && normal.LengthSquared() > 1e-12f ? normal.Normalized() : Vector3.Up;
                    var t = hasUV ? new Vector2(uv[s].X, 1 - uv[s].Y) : Vector2.Zero;
                    var c = coloured ? col[s] : Colors.White;
                    var key = (p, normal, t, c);
                    if (!map.TryGetValue(key, out int v)) {
                        v = vp.Count;
                        map[key] = v;
                        vp.Add(p);
                        vn.Add(normal);
                        vt.Add(t);
                        vc.Add(c);
                    }
                    indices.Add(v);
                }
            }

            var min = vp[0];
            var max = vp[0];
            foreach (var p in vp) {
                min = min.Min(p);
                max = max.Max(p);
            }
            int offset = BeginView();
            foreach (var p in vp) Write(p);
            var attributes = new JsonObject {
                ["POSITION"] = Accessor(EndView(offset, ArrayBuffer), Float, vp.Count, "VEC3", new JsonArray(min.X, min.Y, min.Z), new JsonArray(max.X, max.Y, max.Z))
            };
            offset = BeginView();
            foreach (var p in vn) Write(p);
            attributes["NORMAL"] = Accessor(EndView(offset, ArrayBuffer), Float, vn.Count, "VEC3");
            if (hasUV) {
                offset = BeginView();
                foreach (var t in vt) {
                    writer.Write(t.X);
                    writer.Write(t.Y);
                }
                attributes["TEXCOORD_0"] = Accessor(EndView(offset, ArrayBuffer), Float, vt.Count, "VEC2");
            }
            if (coloured) {
                offset = BeginView();
                foreach (var c in vc) {
                    writer.Write(c.R);
                    writer.Write(c.G);
                    writer.Write(c.B);
                    writer.Write(c.A);
                }
                attributes["COLOR_0"] = Accessor(EndView(offset, ArrayBuffer), Float, vc.Count, "VEC4");
            }
            bool small = vp.Count <= ushort.MaxValue;
            offset = BeginView();
            foreach (int v in indices) {
                if (small) writer.Write((ushort)v);
                else writer.Write((uint)v);
            }
            int indexAccessor = Accessor(EndView(offset, ElementArrayBuffer), small ? UnsignedShort : UnsignedInt, indices.Count, "SCALAR");
            return new JsonObject { ["attributes"] = attributes, ["indices"] = indexAccessor, ["material"] = material };
        }

        void Write(Vector3 v) {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        /// <summary>Starts a buffer view at a 4 byte boundary and returns its offset.</summary>
        int BeginView() {
            writer.Flush();
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            return (int)bin.Length;
        }

        int EndView(int offset, int target) {
            writer.Flush();
            bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bin.Length - offset, ["target"] = target });
            return bufferViews.Count - 1;
        }

        int Accessor(int view, int componentType, int count, string type, JsonArray min = null, JsonArray max = null) {
            var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (min != null) a["min"] = min;
            if (max != null) a["max"] = max;
            accessors.Add(a);
            return accessors.Count - 1;
        }

        // ---- materials and textures ----

        /// <summary>Like <see cref="Materials.ForCity"/> and <see cref="Materials.ForPkg"/>: the texture if there is one, otherwise the diffuse colour.</summary>
        int Material(TextureEntry texture, Color diffuse) {
            var key = (texture, diffuse);
            if (materialIndex.TryGetValue(key, out int index)) return index;
            var pbr = new JsonObject { ["metallicFactor"] = 0f, ["roughnessFactor"] = 1f };
            var m = new JsonObject { ["name"] = texture?.Name ?? (diffuse == Colors.White ? "untextured" : "colour_" + diffuse.ToHtml(false)), ["pbrMetallicRoughness"] = pbr };
            int t = texture == null ? -1 : Texture(texture);
            if (t >= 0) {
                pbr["baseColorTexture"] = new JsonObject { ["index"] = t };
                if (texture.HasAlpha) {
                    m["alphaMode"] = "MASK";
                    m["alphaCutoff"] = 0.5f;
                }
            } else if (texture == null && diffuse != Colors.White) {
                pbr["baseColorFactor"] = new JsonArray(diffuse.R, diffuse.G, diffuse.B, diffuse.A);
            }
            if (options.Unlit) m["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() };
            materials.Add(m);
            index = materials.Count - 1;
            materialIndex[key] = index;
            return index;
        }

        /// <summary>Writes the texture as an upright PNG; -1 for missing or unsupported textures (the viewer's placeholders aren't exported).</summary>
        int Texture(TextureEntry entry) {
            if (textureIndex.TryGetValue(entry, out int index)) return index;
            index = -1;
            if (entry.NotFound || entry.Unknown || entry.Image3D == null) {
                missing.Add(entry.Name);
            } else {
                string file = UniqueFileName(entry.Name);
                Directory.CreateDirectory(textureFolder);
                var img = (Image)entry.Image3D.Duplicate();
                if (img.HasMipmaps()) img.ClearMipmaps();
                // Image3D has the picture's bottom row first (v = 0), see TextureStore
                img.FlipY();
                string full = Path.Combine(textureFolder, file);
                var err = img.SavePng(full);
                if (err != Error.Ok) throw new IOException($"Could not write \"{full}\": {err}");
                images.Add(new JsonObject { ["name"] = entry.Name, ["uri"] = Uri.EscapeDataString(textureFolderName) + "/" + Uri.EscapeDataString(file) });
                textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = images.Count - 1 });
                index = textures.Count - 1;
            }
            textureIndex[entry] = index;
            return index;
        }

        string UniqueFileName(string name) {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            string stem = new string(chars);
            string file = stem + ".png";
            for (int k = 2; !imageFiles.Add(file); k++) file = $"{stem}_{k}.png";
            return file;
        }
    }
}
