using System;
using System.Collections.Generic;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Core {
    public enum DataKind { Vertices, Heights, Textures, Blocks, Roads, Inst, Bai, Pathsets, Dimensions }

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
        /// <summary>Blocks selected; source is the object that caused the selection.</summary>
        public static event Action<int[], object> BlocksSelected;
        public static event Action<int[]> RoutesSelected;
        public static event Action<string> StatusMessage;

        public static int[] SelectedBlocks { get; private set; } = Array.Empty<int>();
        public static int[] SelectedVertices { get; private set; } = Array.Empty<int>();

        public static void SetPsdl(PsdlFile psdl, string path) {
            Psdl = psdl;
            PsdlPath = path ?? "";
            SelectedBlocks = Array.Empty<int>();
            SelectedVertices = Array.Empty<int>();
            PsdlReplaced?.Invoke();
        }

        public static void SetInst(InstFile inst, string path) {
            Inst = inst;
            InstPath = path ?? "";
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

        public static void SelectBlocks(int[] indices, object source) {
            SelectedBlocks = indices;
            BlocksSelected?.Invoke(indices, source);
        }

        public static void SelectRoutes(int[] indices) => RoutesSelected?.Invoke(indices);

        public static void Status(string message) => StatusMessage?.Invoke(message);

        // ---- block list edits that keep INST references in sync ----

        public static void InsertBlock(int index, Block block) {
            Psdl.InsertBlock(index, block);
            Inst.RemapBlocks(i => i >= index ? i + 1 : i);
        }

        public static void RemoveBlock(int index) {
            Psdl.RemoveBlock(index);
            Inst.RemapBlocks(i => i == index ? -1 : i > index ? i - 1 : i);
        }

        public static void MoveBlock(int from, int to) {
            Psdl.MoveBlock(from, to);
            Inst.RemapBlocks(i => PsdlFile.MoveMap(i, from, to));
        }
    }
}
