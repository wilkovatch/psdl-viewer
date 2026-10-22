using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace PsdlViewer.Formats {
    public class PkgShader {
        public string Texture;
        public Vector4 Diffuse = Vector4.One;
        public Vector4 Ambient = Vector4.One;
        public Vector4 Specular;
        public Vector4 Emissive;
        public float Shininess;

        public bool IsTextured => !string.IsNullOrEmpty(Texture);
    }

    public class PkgStrip {
        public int PrimitiveType;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] UVs;
        public int[] Indices;
    }

    public class PkgSection {
        public int Flags;
        public int ShaderOffset;
        public List<PkgStrip> Strips = new();
    }

    public class PkgGeometry {
        public string Name;
        public uint Fvf;
        public List<PkgSection> Sections = new();
    }

    /// <summary>Triangle soup for one shader of a PKG level of detail.</summary>
    public class PkgSurface {
        public PkgShader Shader;
        public List<Vector3> Positions = new();
        public List<Vector3> Normals = new();
        public List<Vector2> UVs = new();
        /// <summary>Counter-clockwise triangle list (OpenGL/Java3D convention).</summary>
        public List<int> Indices = new();
    }

    /// <summary>Model file ("PKG2"/"PKG3").</summary>
    public class PkgFile {
        public const int LodVeryLow = 0, LodLow = 1, LodMedium = 2, LodHigh = 3;
        public static readonly string[] LodNames = { "VL", "L", "M", "H" };

        public string Name;
        public List<PkgGeometry>[] Lods = { new(), new(), new(), new() };
        public int ShaderType;
        public int PaintJobs = 1;
        public int ShadersPerPaintJob;
        public List<PkgShader> Shaders = new();
        public Vector3 Offset;
        public Vector3 Min = new(float.MaxValue);
        public Vector3 Max = new(float.MinValue);

        public static PkgFile Load(string path) {
            var pkg = new PkgFile { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            string magic = r.ReadMagic();
            bool hasLength;
            if (magic == "PKG2") hasLength = false;
            else if (magic == "PKG3") hasLength = true;
            else throw new InvalidDataException("Not a known PKG file.");
            while (stream.Position + 4 <= stream.Length) {
                if (r.ReadMagic() != "FILE") throw new InvalidDataException("Expected \"FILE\".");
                string name = (r.ReadLengthString() ?? "").ToUpperInvariant();
                long length = hasLength ? r.ReadUInt32() : -1;
                long start = stream.Position;
                int lod = LodOf(name);
                if (lod >= 0) {
                    pkg.Lods[lod].Add(pkg.ReadGeometry(r, name));
                } else if (name == "SHADERS") {
                    pkg.ReadShaders(r);
                } else if (name == "OFFSET") {
                    pkg.Offset = r.ReadVec3();
                } else if (name == "XREFS" && !hasLength) {
                    uint n = r.ReadUInt32();
                    stream.Seek(n * (48 + 32), SeekOrigin.Current);
                } else if (hasLength) {
                    stream.Seek(length, SeekOrigin.Current);
                } else {
                    break; // unknown PKG2 chunk, its size can't be determined
                }
                if (hasLength) stream.Position = start + length;
            }
            return pkg;
        }

        static int LodOf(string name) {
            if (name.EndsWith("VL")) return LodVeryLow;
            if (name.EndsWith("L")) return LodLow;
            if (name.EndsWith("M")) return LodMedium;
            if (name.EndsWith("H")) return LodHigh;
            return -1;
        }

        PkgGeometry ReadGeometry(BinaryReader r, string name) {
            var g = new PkgGeometry { Name = name };
            uint sections = r.ReadUInt32();
            r.ReadUInt32(); // total vertices
            r.ReadUInt32(); // total indices
            r.ReadUInt32(); // sections again
            g.Fvf = r.ReadUInt32();
            for (uint s = 0; s < sections; s++) {
                var section = new PkgSection();
                int strips = r.ReadUInt16();
                section.Flags = r.ReadUInt16();
                bool compact = (section.Flags & 0x100) != 0;
                section.ShaderOffset = compact ? r.ReadUInt16() : (int)r.ReadUInt32();
                for (int i = 0; i < strips; i++) section.Strips.Add(ReadStrip(r, g.Fvf, compact));
                g.Sections.Add(section);
            }
            return g;
        }

        PkgStrip ReadStrip(BinaryReader r, uint fvf, bool compact) {
            var strip = new PkgStrip();
            int vertices;
            if (compact) {
                strip.PrimitiveType = r.ReadUInt16();
                vertices = r.ReadUInt16();
            } else {
                strip.PrimitiveType = (int)r.ReadUInt32();
                vertices = (int)r.ReadUInt32();
            }
            strip.Positions = new Vector3[vertices];
            strip.Normals = (fvf & 0x10) != 0 ? new Vector3[vertices] : null;
            strip.UVs = new Vector2[vertices];
            int texCoords = (int)((fvf >> 8) & 0xf);
            for (int i = 0; i < vertices; i++) {
                if ((fvf & 0x2) != 0) {
                    var p = r.ReadVec3();
                    strip.Positions[i] = p;
                    Min = Vector3.Min(Min, p);
                    Max = Vector3.Max(Max, p);
                }
                if ((fvf & 0x10) != 0) {
                    if (compact) {
                        // Midnight Club packed normal; the encoding is unknown, recompute later
                        r.ReadBytes(3);
                        strip.Normals = null;
                    } else {
                        strip.Normals[i] = r.ReadVec3();
                    }
                }
                if ((fvf & 0x40) != 0) r.ReadUInt32(); // diffuse colour
                if ((fvf & 0x80) != 0) r.ReadUInt32(); // specular colour
                for (int t = 0; t < texCoords; t++) {
                    Vector2 uv = compact
                        ? new Vector2(r.ReadUInt16() / 128f, r.ReadUInt16() / 128f)
                        : new Vector2(r.ReadSingle(), r.ReadSingle());
                    if (t == 0) strip.UVs[i] = uv;
                }
            }
            int indices = compact ? r.ReadUInt16() : (int)r.ReadUInt32();
            strip.Indices = new int[indices];
            for (int i = 0; i < indices; i++) strip.Indices[i] = r.ReadUInt16();
            return strip;
        }

        void ReadShaders(BinaryReader r) {
            uint type = r.ReadUInt32();
            ShadersPerPaintJob = (int)r.ReadUInt32();
            PaintJobs = (int)(type & 0x7f);
            ShaderType = (int)(type >> 7);
            int n = PaintJobs * ShadersPerPaintJob;
            for (int i = 0; i < n; i++) {
                var s = new PkgShader { Texture = r.ReadLengthString() };
                if (ShaderType == 0) {
                    s.Diffuse = ReadColorF(r);
                    s.Ambient = ReadColorF(r);
                    s.Specular = ReadColorF(r);
                    s.Emissive = ReadColorF(r);
                    s.Shininess = r.ReadSingle();
                } else {
                    s.Diffuse = ReadColorB(r);
                    s.Ambient = ReadColorB(r);
                    s.Emissive = ReadColorB(r);
                    s.Shininess = r.ReadSingle();
                }
                Shaders.Add(s);
            }
        }

        static Vector4 ReadColorF(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Vector4 ReadColorB(BinaryReader r) => new(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f);

        public bool HasLod(int lod) => Lods[lod].Count != 0;

        /// <summary>
        /// Shadows, the parts of a broken banger (BREAK01_H...), which only appear once it is hit, and the lamps of the traffic
        /// lights (REDGLOWDAY_H, WALK_NIGHT_H...), which are switched on one at a time, are not drawn.
        /// </summary>
        static bool IsDrawn(PkgGeometry g) {
            string n = g.Name;
            return !n.StartsWith("SHADOW") && !n.StartsWith("BREAK") && !n.Contains("GLOWDAY") && !n.Contains("GLOWNIGHT") &&
                   !n.Contains("WALK_DAY") && !n.Contains("WALK_NIGHT");
        }

        /// <summary>Builds per-shader triangle lists for the given level of detail (falling back to lower LODs) and paint job.</summary>
        public List<PkgSurface> BuildSurfaces(int lod, int paintJob) {
            while (lod > 0 && !Lods[lod].Any(IsDrawn)) lod--;
            if (paintJob < 0 || paintJob >= Math.Max(PaintJobs, 1)) paintJob = 0;
            var surfaces = new Dictionary<int, PkgSurface>();
            foreach (var g in Lods[lod]) {
                if (!IsDrawn(g)) continue;
                foreach (var section in g.Sections) {
                    int shaderIndex = paintJob * ShadersPerPaintJob + section.ShaderOffset;
                    if (!surfaces.TryGetValue(shaderIndex, out var surface)) {
                        surface = new PkgSurface {
                            Shader = shaderIndex >= 0 && shaderIndex < Shaders.Count ? Shaders[shaderIndex] : new PkgShader()
                        };
                        surfaces[shaderIndex] = surface;
                    }
                    bool compact = (section.Flags & 0x100) != 0;
                    foreach (var strip in section.Strips) AddStrip(surface, strip, compact);
                }
            }
            return new List<PkgSurface>(surfaces.Values);
        }

        static void AddStrip(PkgSurface s, PkgStrip strip, bool compact) {
            int baseIndex = s.Positions.Count;
            int n = strip.Positions.Length;
            for (int i = 0; i < n; i++) {
                s.Positions.Add(strip.Positions[i]);
                s.UVs.Add(strip.UVs[i]);
                s.Normals.Add(strip.Normals != null ? strip.Normals[i] : Vector3.Zero);
            }
            var tris = new List<int>();
            if (strip.PrimitiveType == 3) {
                // triangle list, clockwise in the game's left handed space = counter-clockwise here
                for (int i = 0; i + 2 < strip.Indices.Length; i += 3) {
                    tris.Add(compact ? strip.Indices[i] & 0x3fff : strip.Indices[i]);
                    tris.Add(compact ? strip.Indices[i + 1] & 0x3fff : strip.Indices[i + 1]);
                    tris.Add(compact ? strip.Indices[i + 2] & 0x3fff : strip.Indices[i + 2]);
                }
            } else {
                // strips separated by the 0x8000 end flag (Midnight Club)
                var current = new List<int>();
                foreach (int raw in strip.Indices) {
                    current.Add(raw & 0x3fff);
                    if ((raw & 0x8000) != 0) {
                        StripToTriangles(current, tris);
                        current.Clear();
                    }
                }
                StripToTriangles(current, tris);
            }
            foreach (int t in tris) {
                if (t >= n) return; // corrupt strip, ignore the rest
            }
            bool computeNormals = strip.Normals == null;
            for (int i = 0; i + 2 < tris.Count; i += 3) {
                int a = baseIndex + tris[i], b = baseIndex + tris[i + 1], c = baseIndex + tris[i + 2];
                s.Indices.Add(a);
                s.Indices.Add(b);
                s.Indices.Add(c);
                if (computeNormals) {
                    var normal = Vector3.Cross(s.Positions[b] - s.Positions[a], s.Positions[c] - s.Positions[a]);
                    s.Normals[a] += normal;
                    s.Normals[b] += normal;
                    s.Normals[c] += normal;
                }
            }
            if (computeNormals) {
                for (int i = baseIndex; i < s.Normals.Count; i++) {
                    s.Normals[i] = s.Normals[i].LengthSquared() > 0 ? Vector3.Normalize(s.Normals[i]) : Vector3.UnitY;
                }
            }
        }

        static void StripToTriangles(List<int> strip, List<int> tris) {
            for (int i = 0; i + 2 < strip.Count; i++) {
                if ((i & 1) == 0) {
                    tris.Add(strip[i]);
                    tris.Add(strip[i + 1]);
                } else {
                    tris.Add(strip[i + 1]);
                    tris.Add(strip[i]);
                }
                tris.Add(strip[i + 2]);
            }
        }
    }
}
