using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace PsdlViewer.Core {
    /// <summary>Persistent options (the original viewer's options.xml).</summary>
    public class Settings {
        public const int DrawingLines = 0, DrawingFill = 1;
        public const int LightingUnlit = 0, LightingLit = 1;
        public const int ColouringGrey = 0, ColouringRandom = 1, ColouringTextureIndex = 2, ColouringTextureMap = 3;
        public static readonly string[] DrawingNames = { "Lines", "Fill" };
        public static readonly string[] LightingNames = { "Unlit", "Lit" };
        public static readonly string[] ColouringNames = { "Grey indexed", "Random", "Texture indexed", "Texture map" };
        public const int CameraSchemeA = 0, CameraSchemeB = 1;
        public static readonly string[] CameraControlNames = { "Scheme A", "Scheme B" };

        public string OpenPath = "";
        public string SavePath = "";
        public List<string> TexturePaths = new();
        public List<string> GeometryPaths = new();

        public int DrawingMode = DrawingFill;
        public int LightingMode = LightingUnlit;
        public int ColouringMode = ColouringTextureMap;
        // JSON names kept from when rooms were called blocks, so existing settings files still load
        [JsonPropertyName("ShowBlockIndices")] public bool ShowRoomIndices;
        [JsonPropertyName("RenderBlockAttributes")] public bool RenderRoomAttributes = true;
        [JsonPropertyName("ShowBlockBounds")] public bool ShowRoomBounds = true;
        public bool ShowCityBounds;
        public bool RenderInst = true;
        public bool ShowPathsets = true;
        public bool ShowBai = true;
        public float LabelSize = 3;
        public bool UseNativeFileDialogs = true;
        public bool LoadSiblingInst;
        public bool LoadSiblingBai;
        public float CameraFov = 60;
        public int CameraControls = CameraSchemeA;
        public bool EmulateMiddleButton;
        public float BackgroundBrightness = 0.08f;

        public static Settings Current { get; private set; } = new();
        public static event Action Changed;

        // texture and geometry folders next to the open PSDL file's folder; only for that file, never saved
        static string[] fileTexturePaths = Array.Empty<string>(), fileGeometryPaths = Array.Empty<string>();

        /// <summary>The configured texture folders followed by the ones found for the open PSDL file.</summary>
        public static List<string> TextureSearchPaths => SearchPaths(Current.TexturePaths, fileTexturePaths);
        /// <summary>The configured geometry folders followed by the ones found for the open PSDL file.</summary>
        public static List<string> GeometrySearchPaths => SearchPaths(Current.GeometryPaths, fileGeometryPaths);

        static List<string> SearchPaths(List<string> configured, string[] file) =>
            configured.Concat(file.Where(f => !configured.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();

        /// <summary>Uses the texture and geometry folders next to the PSDL file's folder (MM2 layout) while that file is open. Returns whether they changed.</summary>
        public static bool SetFileFolders(string psdlPath) {
            string city = Path.GetDirectoryName(Path.GetFullPath(psdlPath));
            string root = city == null ? null : Path.GetDirectoryName(city);
            string[] Find(string name) {
                string dir = root == null ? null : Path.Combine(root, name);
                return dir != null && Directory.Exists(dir) ? new[] { dir } : Array.Empty<string>();
            }
            string[] tex = Find("texture"), geo = Find("geometry");
            bool changed = !tex.SequenceEqual(fileTexturePaths) || !geo.SequenceEqual(fileGeometryPaths);
            fileTexturePaths = tex;
            fileGeometryPaths = geo;
            return changed;
        }

        static string FilePath => ProjectSettings.GlobalizePath("user://settings.json");
        static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, WriteIndented = true };

        public static void Load() {
            try {
                if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            } catch (Exception e) {
                GD.PushWarning("Could not read settings: " + e.Message);
                Current = new Settings();
            }
        }

        public static void Save() {
            try {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
            } catch (Exception e) {
                GD.PushWarning("Could not write settings: " + e.Message);
            }
        }

        public static void NotifyChanged() => Changed?.Invoke();

        public Settings Clone() {
            var copy = (Settings)MemberwiseClone();
            copy.TexturePaths = new List<string>(TexturePaths);
            copy.GeometryPaths = new List<string>(GeometryPaths);
            return copy;
        }

        public static void Replace(Settings settings) {
            Current = settings;
            NotifyChanged();
        }
    }
}
