using System;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;
using PsdlViewer.Render;

namespace PsdlViewer.UI {
    /// <summary>Editor for one room attribute (the original viewer's Attribute*View classes).</summary>
    public partial class AttributeEditor : VBoxContainer {
        public event Action Changed;
        readonly RoomAttribute attribute;
        readonly PropertyGrid grid = new();
        ListEditor list;
        TextureRect preview;
        Label previewLabel;

        public AttributeEditor(RoomAttribute attribute) {
            this.attribute = attribute;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddChild(grid);
            grid.Changed += Notify;
            grid.AddInfo("Attribute:", () => $"0x{attribute.Id:x4}  {attribute.Name}  (type {attribute.Type:x}, subtype {attribute.Subtype})");
            grid.AddCheck("Last attribute", () => attribute.Last, v => attribute.Last = v, "The game renders nothing after the last attribute");
            if (attribute is CountedAttribute counted) {
                grid.AddCheck("Explicit count", () => counted.ExplicitCount, v => counted.ExplicitCount = v,
                    "Store the element count in an extra word (subtype 0) instead of the subtype");
            }
            switch (attribute) {
                case TextureReferenceAttribute t:
                    grid.AddHex("Index:", () => t.Index & 0xffff, v => t.Index = v >= 0xffff ? -1 : v, 4, "Texture index, ffff = none");
                    grid.AddInfo("Texture:", () => ListEditor.TextureInfo(t.Index));
                    previewLabel = new Label();
                    preview = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(160, 160) };
                    AddChild(preview);
                    AddChild(previewLabel);
                    UpdatePreview();
                    break;
                case DividedRoadAttribute d:
                    grid.AddHex("Divider texture:", () => d.DividerTexture & 0xffff, v => d.DividerTexture = v >= 0xffff ? -1 : v, 4, "First of the four divider textures");
                    grid.AddInfo("", () => ListEditor.TextureInfo(d.DividerTexture));
                    grid.AddOption("Divider type", DividedRoadAttribute.DividerNames, () => d.DividerType, v => d.DividerType = v);
                    grid.AddCheck("Unknown0", () => d.GetFlag(DividedRoadAttribute.FlagUnknown0), v => d.SetFlag(DividedRoadAttribute.FlagUnknown0, v));
                    grid.AddCheck("Unknown1", () => d.GetFlag(DividedRoadAttribute.FlagUnknown1), v => d.SetFlag(DividedRoadAttribute.FlagUnknown1, v));
                    grid.AddCheck("Unknown2", () => d.GetFlag(DividedRoadAttribute.FlagUnknown2), v => d.SetFlag(DividedRoadAttribute.FlagUnknown2, v));
                    grid.AddCheck("Unknown3", () => d.GetFlag(DividedRoadAttribute.FlagUnknown3), v => d.SetFlag(DividedRoadAttribute.FlagUnknown3, v));
                    grid.AddFloat("Value", () => d.ScaledValue, v => d.ScaledValue = v, "Flat: texture repeat count. Elevated: height and side strip width in metres");
                    grid.AddCheck("Close start", () => d.GetFlag(DividedRoadAttribute.FlagCloseStart), v => d.SetFlag(DividedRoadAttribute.FlagCloseStart, v));
                    grid.AddCheck("Close end", () => d.GetFlag(DividedRoadAttribute.FlagCloseEnd), v => d.SetFlag(DividedRoadAttribute.FlagCloseEnd, v));
                    AddVertices(d.Vertices, "Vertices (6 per cross section)");
                    break;
                case RoofTriangleFanAttribute r:
                    grid.AddHex("Height:", () => r.HeightRef, v => r.HeightRef = v, 4, "Index in the height list");
                    grid.AddInfo("", () => ListEditor.HeightInfo(r.HeightRef));
                    AddVertices(r.Vertices, "Vertices");
                    break;
                case StripAttribute s:
                    AddVertices(s.Vertices, $"Vertices ({s.VerticesPerSection} per cross section)");
                    break;
                case CountedAttribute c:
                    AddVertices(c.Vertices, "Vertices");
                    break;
                case SliverAttribute s:
                    grid.AddHex("Top height:", () => s.TopHeight, v => s.TopHeight = v);
                    grid.AddInfo("", () => ListEditor.HeightInfo(s.TopHeight));
                    grid.AddHex("Texture scale:", () => s.TextureScale, v => s.TextureScale = v, 4, "Index in the height list");
                    grid.AddInfo("", () => ListEditor.HeightInfo(s.TextureScale));
                    AddVertices(s.Vertices, "Vertices", false);
                    break;
                case FacadeBoundAttribute b:
                    grid.AddHex("Angle:", () => b.Angle, v => b.Angle = v, 4, "Angle between the facade and the sunlight (0-255)");
                    grid.AddHex("Top height:", () => b.TopHeight, v => b.TopHeight = v);
                    grid.AddInfo("", () => ListEditor.HeightInfo(b.TopHeight));
                    AddVertices(b.Vertices, "Vertices", false);
                    break;
                case FacadeAttribute f:
                    grid.AddHex("Bottom height:", () => f.BottomHeight, v => f.BottomHeight = v);
                    grid.AddInfo("", () => ListEditor.HeightInfo(f.BottomHeight));
                    grid.AddHex("Top height:", () => f.TopHeight, v => f.TopHeight = v);
                    grid.AddInfo("", () => ListEditor.HeightInfo(f.TopHeight));
                    grid.AddInt("U repeat:", () => f.URepeat, v => f.URepeat = Math.Clamp(v, 0, 0xffff));
                    grid.AddInt("V repeat:", () => f.VRepeat, v => f.VRepeat = Math.Clamp(v, 0, 0xffff));
                    AddVertices(f.Vertices, "Vertices", false);
                    break;
                case TunnelAttribute t:
                    BuildTunnel(t);
                    break;
                case RawAttribute raw:
                    list = new ListEditor("Data (raw words)");
                    list.BindInts(() => raw.Words, null, false, true, "Word");
                    list.Changed += Notify;
                    AddChild(list);
                    break;
            }
        }

        void AddVertices(System.Collections.Generic.List<int> vertices, string title, bool resizable = true) {
            list = new ListEditor(title + "  (drag vertices here from a vertex list)");
            list.BindInts(() => vertices, ListEditor.VertexInfo, true, resizable, "Vertex");
            list.Changed += Notify;
            AddChild(list);
        }

        void BuildTunnel(TunnelAttribute t) {
            grid.AddCheck("Left side", () => t.GetFlag(TunnelAttribute.LeftSide), v => t.SetFlag(TunnelAttribute.LeftSide, v));
            grid.AddCheck("Right side", () => t.GetFlag(TunnelAttribute.RightSide), v => t.SetFlag(TunnelAttribute.RightSide, v));
            grid.AddOption("Style", new[] { "Railing", "Wall" }, () => t.GetFlag(TunnelAttribute.StyleWall) ? 1 : 0, v => t.SetFlag(TunnelAttribute.StyleWall, v == 1));
            grid.AddOption("Ceiling", new[] { "None", "Flat", "Curved", "Both" },
                () => (t.GetFlag(TunnelAttribute.FlatCeiling) ? 1 : 0) + (t.GetFlag(TunnelAttribute.CurvedCeiling) ? 2 : 0),
                v => {
                    t.SetFlag(TunnelAttribute.FlatCeiling, (v & 1) != 0);
                    t.SetFlag(TunnelAttribute.CurvedCeiling, (v & 2) != 0);
                });
            grid.AddCheck("Closed start left", () => t.GetFlag(TunnelAttribute.ClosedStartLeft), v => t.SetFlag(TunnelAttribute.ClosedStartLeft, v));
            grid.AddCheck("Closed end left", () => t.GetFlag(TunnelAttribute.ClosedEndLeft), v => t.SetFlag(TunnelAttribute.ClosedEndLeft, v));
            grid.AddCheck("Closed start right", () => t.GetFlag(TunnelAttribute.ClosedStartRight), v => t.SetFlag(TunnelAttribute.ClosedStartRight, v));
            grid.AddCheck("Closed end right", () => t.GetFlag(TunnelAttribute.ClosedEndRight), v => t.SetFlag(TunnelAttribute.ClosedEndRight, v));
            grid.AddCheck("Offset start left", () => t.GetFlag(TunnelAttribute.OffsetStartLeft), v => t.SetFlag(TunnelAttribute.OffsetStartLeft, v));
            grid.AddCheck("Offset end left", () => t.GetFlag(TunnelAttribute.OffsetEndLeft), v => t.SetFlag(TunnelAttribute.OffsetEndLeft, v));
            grid.AddCheck("Offset start right", () => t.GetFlag(TunnelAttribute.OffsetStartRight), v => t.SetFlag(TunnelAttribute.OffsetStartRight, v));
            grid.AddCheck("Offset end right", () => t.GetFlag(TunnelAttribute.OffsetEndRight), v => t.SetFlag(TunnelAttribute.OffsetEndRight, v));
            grid.AddCheck("Curved sides", () => t.GetFlag(TunnelAttribute.CurvedSides), v => t.SetFlag(TunnelAttribute.CurvedSides, v));
            grid.AddCheck("Culled", () => t.GetFlag(TunnelAttribute.Culled), v => t.SetFlag(TunnelAttribute.Culled, v), "Disables the outer faces of railings");
            grid.AddCheck("Unknown", () => t.GetFlag(TunnelAttribute.Unknown), v => t.SetFlag(TunnelAttribute.Unknown, v));
            grid.AddFloat("Height", () => t.Height, v => t.Height = v, "Height in metres");
            if (t.Words.Count > 2 || t.Junction) grid.AddHex("Unknown value:", () => t.UnknownValue, v => t.UnknownValue = v);
            if (!t.Junction) return;
            grid.AddInt("Ceiling fan origin:", () => t.CeilingFanOrigin, v => t.CeilingFanOrigin = v, "Perimeter index where the ceiling fan starts");
            grid.AddHex("Wall direction bits A:", () => (int)t.WallDirectionA, v => t.WallDirectionA = (uint)v, 8);
            grid.AddHex("Wall direction bits B:", () => (int)t.WallDirectionB, v => t.WallDirectionB = (uint)v, 8);
            AddChild(new Label { Text = "Wall visibility bits (wall i runs from perimeter point i-1 to i)", Modulate = new Color(0.75f, 0.8f, 0.9f) });
            var walls = new GridContainer { Columns = 8 };
            for (int i = 0; i < 32; i++) {
                int bit = i;
                var check = new CheckBox { Text = i.ToString(), ButtonPressed = (t.WallVisibility & (1u << bit)) != 0 };
                check.Toggled += on => {
                    t.WallVisibility = on ? t.WallVisibility | (1u << bit) : t.WallVisibility & ~(1u << bit);
                    Notify();
                };
                walls.AddChild(check);
            }
            AddChild(walls);
        }

        void UpdatePreview() {
            if (preview == null || attribute is not TextureReferenceAttribute t) return;
            string name = Doc.Psdl.TextureAt(t.Index);
            var entry = TextureStore.Get(name);
            preview.Texture = entry?.DisplayTexture;
            previewLabel.Text = entry == null ? "No image" : $"{entry.Name}: {entry.Status}";
        }

        void Notify() {
            grid.Refresh();
            UpdatePreview();
            Changed?.Invoke();
        }
    }
}
