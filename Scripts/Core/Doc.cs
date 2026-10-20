using System;
using System.Collections.Generic;
using System.Linq;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Core {
    public enum DataKind { Vertices, Heights, Textures, Rooms, RoomPaths, Inst, Bai, Pathsets, Dimensions }

    /// <summary>
    /// The open documents (PSDL, INST, BAI, pathsets) plus the selection events that link the views together,
    /// the counterpart of the original viewer's Config class.
    /// </summary>
    public static class Doc {
        public static PsdlFile Psdl { get; private set; } = new();
        public static InstFile Inst { get; private set; } = new();
        public static BaiFile Bai { get; private set; } = new();
        public static List<Pathset> Pathsets { get; } = new();
        public static string PsdlPath = "";
        public static string InstPath = "";
        public static string BaiPath = "";

        /// <summary>A new PSDL replaced the current one: every list must reload.</summary>
        public static event Action PsdlReplaced;
        public static event Action InstReplaced;
        public static event Action BaiReplaced;
        /// <summary>The content of a list changed (rows added, removed or edited).</summary>
        public static event Action<DataKind> DataChanged;
        /// <summary>Vertices picked in a vertex list (indices into the vertex list).</summary>
        public static event Action<int[]> VerticesSelected;
        /// <summary>Vertex references picked in a perimeter/attribute list; vertex lists follow.</summary>
        public static event Action<int[]> VertexRefsSelected;
        /// <summary>Rooms selected; source is the object that caused the selection.</summary>
        public static event Action<int[], object> RoomsSelected;
        /// <summary>INST components selected; source is the object that caused the selection.</summary>
        public static event Action<int[], object> InstSelected;
        /// <summary>Attributes picked in the attribute list of a room.</summary>
        public static event Action<object> RoomAttributesSelected;
        public static event Action<int[]> RoutesSelected;
        public static event Action<string> StatusMessage;

        public static int[] SelectedRooms { get; private set; } = Array.Empty<int>();
        public static int[] SelectedVertices { get; private set; } = Array.Empty<int>();
        public static int[] SelectedInst { get; private set; } = Array.Empty<int>();
        public static Room AttributeRoom { get; private set; }
        public static RoomAttribute[] SelectedAttributes { get; private set; } = Array.Empty<RoomAttribute>();

        public static void SetPsdl(PsdlFile psdl, string path) {
            Psdl = psdl;
            PsdlPath = path ?? "";
            SelectedRooms = Array.Empty<int>();
            SelectedVertices = Array.Empty<int>();
            AttributeRoom = null;
            SelectedAttributes = Array.Empty<RoomAttribute>();
            PsdlReplaced?.Invoke();
        }

        public static void SetInst(InstFile inst, string path) {
            Inst = inst;
            InstPath = path ?? "";
            SelectedInst = Array.Empty<int>();
            InstReplaced?.Invoke();
        }

        public static void SetBai(BaiFile bai, string path) {
            Bai = bai;
            BaiPath = path ?? "";
            BaiReplaced?.Invoke();
        }

        public static void NotifyChanged(DataKind kind) => DataChanged?.Invoke(kind);

        public static void SelectVertices(int[] indices) {
            SelectedVertices = indices;
            VerticesSelected?.Invoke(indices);
        }

        public static void SelectVertexRefs(int[] indices) => VertexRefsSelected?.Invoke(indices);

        public static void SelectRooms(int[] indices, object source) {
            bool changed = !indices.OrderBy(i => i).SequenceEqual(SelectedRooms.OrderBy(i => i));
            SelectedRooms = indices;
            // vertices picked for the previous room no longer apply
            if (changed && SelectedVertices.Length > 0) {
                SelectVertexRefs(Array.Empty<int>());
                SelectVertices(Array.Empty<int>());
            }
            RoomsSelected?.Invoke(indices, source);
        }

        public static void SelectInst(int[] indices, object source) {
            SelectedInst = indices;
            InstSelected?.Invoke(indices, source);
        }

        public static void SelectRoomAttributes(Room room, RoomAttribute[] attributes, object source) {
            AttributeRoom = room;
            SelectedAttributes = room == null ? Array.Empty<RoomAttribute>() : attributes;
            RoomAttributesSelected?.Invoke(source);
        }

        public static void SelectRoutes(int[] indices) => RoutesSelected?.Invoke(indices);

        /// <summary>Clears every selection; the views follow through the usual selection events.</summary>
        public static void DeselectAll() {
            SelectInst(Array.Empty<int>(), null);
            SelectRoomAttributes(null, null, null);
            SelectRooms(Array.Empty<int>(), null);
            SelectVertexRefs(Array.Empty<int>());
            SelectVertices(Array.Empty<int>());
            SelectRoutes(Array.Empty<int>());
        }

        public static void Status(string message) => StatusMessage?.Invoke(message);

        // ---- room list edits that keep INST references in sync ----

        public static void InsertRoom(int index, Room room) {
            Psdl.InsertRoom(index, room);
            Inst.RemapRooms(i => i >= index ? i + 1 : i);
        }

        public static void RemoveRoom(int index) {
            Psdl.RemoveRoom(index);
            int count = Inst.Components.Count;
            Inst.RemapRooms(i => i == index ? -1 : i > index ? i - 1 : i);
            // removed objects shift the component indices
            if (Inst.Components.Count != count && SelectedInst.Length > 0) SelectInst(Array.Empty<int>(), null);
        }

        public static void MoveRoom(int from, int to) {
            Psdl.MoveRoom(from, to);
            Inst.RemapRooms(i => PsdlFile.MoveMap(i, from, to));
        }
    }
}
