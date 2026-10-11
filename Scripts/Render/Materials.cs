using System.Collections.Generic;
using Godot;

namespace PsdlViewer.Render {
    /// <summary>Material cache for one lighting mode.</summary>
    public class Materials {
        public readonly bool Lit;
        readonly Dictionary<TextureEntry, StandardMaterial3D> city = new();
        readonly Dictionary<(TextureEntry, Color), StandardMaterial3D> pkg = new();
        StandardMaterial3D vertexColor, lines, linesOnTop, water;

        public Materials(bool lit) {
            Lit = lit;
        }

        StandardMaterial3D Base() {
            return new StandardMaterial3D {
                ShadingMode = Lit ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Back,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                Roughness = 1
            };
        }

        void ApplyTexture(StandardMaterial3D m, TextureEntry texture) {
            m.AlbedoTexture = TextureStore.GetTexture(texture);
            if (texture.HasAlpha) {
                m.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                m.AlphaScissorThreshold = 0.5f;
            }
        }

        /// <summary>Textured city surface; null texture gives the untextured (white, vertex coloured) material.</summary>
        public Material ForCity(TextureEntry texture) {
            if (texture == null) return VertexColor();
            if (city.TryGetValue(texture, out var m)) return m;
            m = Base();
            ApplyTexture(m, texture);
            city[texture] = m;
            return m;
        }

        public Material VertexColor() {
            if (vertexColor != null) return vertexColor;
            vertexColor = Base();
            vertexColor.VertexColorUseAsAlbedo = true;
            return vertexColor;
        }

        public Material ForPkg(TextureEntry texture, Color diffuse) {
            var key = (texture, diffuse);
            if (pkg.TryGetValue(key, out var m)) return m;
            m = Base();
            if (texture != null) ApplyTexture(m, texture);
            else m.AlbedoColor = diffuse;
            pkg[key] = m;
            return m;
        }

        /// <summary>Unshaded vertex coloured lines.</summary>
        public Material Lines(bool onTop = false) {
            if (onTop) {
                if (linesOnTop != null) return linesOnTop;
                linesOnTop = MakeLines();
                linesOnTop.NoDepthTest = true;
                linesOnTop.RenderPriority = 10;
                return linesOnTop;
            }
            return lines ??= MakeLines();
        }

        static StandardMaterial3D MakeLines() {
            return new StandardMaterial3D {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }

        public Material Water() {
            return water ??= new StandardMaterial3D {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.3f, 0.7f, 1f, 0.5f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }
    }
}
