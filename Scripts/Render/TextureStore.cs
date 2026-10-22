using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.Render {
    /// <summary>A texture looked up by name in the texture folders.</summary>
    public class TextureEntry {
        public string Name;
        public string File;
        public bool NotFound;
        public bool Unknown;
        public bool HasAlpha;
        /// <summary>Image as used for 3D rendering: UV v = 0 is the first row (the picture's bottom).</summary>
        public Image Image3D;
        public ImageTexture Texture3D;
        ImageTexture display;

        /// <summary>Upright image for previews.</summary>
        public ImageTexture DisplayTexture {
            get {
                if (display == null && Image3D != null) {
                    var img = (Image)Image3D.Duplicate();
                    if (img.HasMipmaps()) img.ClearMipmaps();
                    img.FlipY();
                    display = ImageTexture.CreateFromImage(img);
                }
                return display;
            }
        }

        public string Status => NotFound ? "not found" : Unknown ? "unsupported format" : $"{Image3D?.GetWidth()}x{Image3D?.GetHeight()} {Path.GetExtension(File)}";
    }

    /// <summary>Caches decoded textures. Image decoding is thread safe; GPU textures are created on the main thread.</summary>
    public static class TextureStore {
        static readonly string[] Extensions = { "tex", "gif", "jpg", "tga", "png", "bmp" };
        static readonly Dictionary<string, TextureEntry> cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new();
        static Image notFound, unknown;

        public static void LoadPlaceholders() {
            notFound = GD.Load<Texture2D>("res://Assets/Icons/ImageNotFound.png").GetImage();
            unknown = GD.Load<Texture2D>("res://Assets/Icons/ImageUnknown.png").GetImage();
            notFound.Convert(Image.Format.Rgba8);
            unknown.Convert(Image.Format.Rgba8);
            // the placeholders are drawn upright, store them like a file row ordered texture
            notFound.FlipY();
            unknown.FlipY();
        }

        public static void Flush() {
            lock (sync) cache.Clear();
        }

        public static string FindFile(string name, List<string> folders, IEnumerable<string> extensions) {
            foreach (string ext in extensions) {
                foreach (string folder in folders) {
                    if (string.IsNullOrEmpty(folder)) continue;
                    string p = Path.Combine(folder, name + "." + ext);
                    if (File.Exists(p)) return p;
                    p = Path.Combine(folder, name + "-0001." + ext);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        /// <summary>Finds and decodes a texture (any thread).</summary>
        public static TextureEntry Get(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            lock (sync) {
                if (cache.TryGetValue(name, out var cached)) return cached;
            }
            var entry = new TextureEntry { Name = name };
            entry.File = FindFile(name, Settings.TextureSearchPaths, Extensions);
            try {
                if (entry.File == null) entry.NotFound = true;
                else entry.Image3D = Decode(entry.File, out entry.HasAlpha);
            } catch (Exception e) {
                GD.PushWarning($"Texture {name}: {e.Message}");
                entry.Image3D = null;
            }
            if (entry.Image3D == null) {
                if (!entry.NotFound) entry.Unknown = true;
                entry.Image3D = entry.NotFound ? notFound : unknown;
                entry.HasAlpha = false;
            } else {
                entry.Image3D.GenerateMipmaps();
            }
            lock (sync) {
                if (cache.TryGetValue(name, out var other)) return other;
                cache[name] = entry;
            }
            return entry;
        }

        static Image Decode(string path, out bool hasAlpha) {
            hasAlpha = false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tex") {
                var tex = TexFile.Load(path);
                if (tex == null) return null;
                hasAlpha = tex.HasAlpha;
                // TEX files are stored bottom row first: file order already matches v = 0 at the bottom
                return Image.CreateFromData(tex.Width, tex.Height, false, Image.Format.Rgba8, tex.Rgba);
            }
            byte[] data = File.ReadAllBytes(path);
            var img = new Image();
            Error err = ext switch {
                ".tga" => img.LoadTgaFromBuffer(data),
                ".jpg" => img.LoadJpgFromBuffer(data),
                ".png" => img.LoadPngFromBuffer(data),
                ".bmp" => img.LoadBmpFromBuffer(data),
                _ => Error.FileUnrecognized
            };
            if (err != Error.Ok || img.IsEmpty()) return null;
            hasAlpha = img.DetectAlpha() != Image.AlphaMode.None;
            img.Convert(Image.Format.Rgba8);
            // these formats store the top row first; flip so that v = 0 is the picture's bottom like in the original viewer
            img.FlipY();
            return img;
        }

        /// <summary>Creates (once) the GPU texture; main thread only.</summary>
        public static ImageTexture GetTexture(TextureEntry entry) {
            if (entry == null) return null;
            entry.Texture3D ??= ImageTexture.CreateFromImage(entry.Image3D);
            return entry.Texture3D;
        }
    }
}
