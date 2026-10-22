using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace PsdlViewer.Formats {
    /// <summary>One side (right or left) of a BAI route.</summary>
    public class BaiRoadData {
        public const int AmbientAll = 0, AmbientPedestrians = 1, AmbientVehicles = 2, AmbientNone = 3;
        public static readonly string[] AmbientNames = { "Vehicles and pedestrians", "Pedestrians only", "Vehicles only", "Nobody" };

        public int Lanes;
        public int Trams;
        public int Trains;
        /// <summary>Number of sidewalks, always 1 (the layout below assumes it).</summary>
        public int Sidewalks = 1;
        /// <summary>Ambients allowed on this side (see the Ambient* constants).</summary>
        public int AmbientTypes;
        /// <summary>Lanes * points distances along each lane, lane-major.</summary>
        public List<float> LaneDistances = new();
        /// <summary>Distances along the outer edge (sidewalk), one per point.</summary>
        public List<float> Distances = new();
        /// <summary>
        /// 11 + Lanes floats: one more outer edge distance, 10 unknown values, then one unknown value per lane
        /// (probably lane centre distances and the half width again).
        /// </summary>
        public List<float> Unknown0 = new();
        public List<Vector3> LaneVertices = new();
        public List<Vector3> SidewalkCenter = new();
        public List<Vector3> TramVertices = new();
        public List<Vector3> TrainVertices = new();
        public List<Vector3> SidewalkInner = new();
        public List<Vector3> SidewalkOuter = new();
    }

    public class BaiRoadEnd {
        public const uint None = 0xcdcdcdcd;
        public const int RuleStopSign = 0, RuleTrafficLight = 1, RuleAlwaysStop = 2, RuleNeverStop = 3;
        public static readonly string[] RuleNames = { "Stop sign", "Traffic light", "Always stop", "Never stop" };

        /// <summary>ID of the intersection at this end, 0xcdcdcdcd for none.</summary>
        public uint Intersection;
        /// <summary>Always 0xcdcd (uninitialised memory in the original tools).</summary>
        public int Unknown0;
        /// <summary>How vehicles behave at the intersection (see the Rule* constants).</summary>
        public uint VehicleRule;
        /// <summary>Index of this road in the intersection's road list, or 0xcdcdcdcd.</summary>
        public uint IntersectionRoadIndex;
        /// <summary>Traffic light position; no light is shown at (0, 0, 0).</summary>
        public Vector3 Origin;
        /// <summary>Traffic light orientation.</summary>
        public Vector3 Axis;
    }

    public class BaiRoute {
        public const int FlagDivided = 1, FlagAlley = 2, FlagFreeway = 4, FlagFlat = 8;
        public static readonly string[] FlagNames = { "Divided", "Alleyway/walkway", "Freeway", "Flat" };
        public static readonly string[] FlagDescriptions = {
            "Divided road",
            "Alleyway or walkway",
            "Freeway: vehicles get extra speed",
            "Constant slope: makes the orientation checks of the traffic cheaper"
        };

        public int Id;
        public int Flags;
        public List<int> Rooms = new();
        /// <summary>Half the width of the road.</summary>
        public float HalfWidth;
        /// <summary>Base speed of the vehicles (MM2 overrides it with the values of the race aimap file).</summary>
        public float BaseSpeed;
        public BaiRoadData Right = new();
        public BaiRoadData Left = new();
        public List<float> CentralDistances = new();
        public List<Vector3> CentralLine = new();
        public List<Vector3> XOrientation = new();
        public List<Vector3> YOrientation = new();
        public List<Vector3> ZOrientation = new();
        /// <summary>Direction of the road at each point.</summary>
        public List<Vector3> Tangents = new();
        public BaiRoadEnd End = new();
        public BaiRoadEnd Start = new();

        public int Points => CentralLine.Count;
    }

    public class BaiIntersection {
        public int Id;
        public int Room;
        public Vector3 Center;
        public List<uint> Routes = new();
    }

    /// <summary>AI road network ("CAI1"). Every field is kept so that files can be written back unchanged.</summary>
    public class BaiFile {
        public List<BaiRoute> Routes = new();
        public List<BaiIntersection> Intersections = new();
        /// <summary>
        /// Road lists per PSDL room (plus the unused first entry) for the large (vehicle) and small (pedestrian) AI bubbles; null
        /// when the file has no culling section.
        /// </summary>
        public List<List<int>> CullLarge;
        public List<List<int>> CullSmall;
        /// <summary>Unparsed bytes at the end of the file, written back unchanged.</summary>
        public byte[] Trailer = System.Array.Empty<byte>();

        public static BaiFile Load(string path, Progress progress = null) {
            var bai = new BaiFile();
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            progress?.Begin("Reading header", stream.Length);
            FileLog.Print($"Reading BAI file {path}");
            string magic = r.ReadMagic();
            FileLog.Print("Header: " + magic);
            if (magic != "CAI1") throw new InvalidDataException("Not a BAI file.");
            int intersections = r.ReadUInt16();
            int routes = r.ReadUInt16();
            FileLog.Print($"{intersections} intersections, {routes} routes");
            if (progress != null) progress.Note = "Reading routes";
            for (int i = 0; i < routes; i++) {
                bai.Routes.Add(ReadRoute(r));
                if (progress != null) progress.Value = stream.Position;
            }
            if (progress != null) progress.Note = "Reading intersections";
            for (int i = 0; i < intersections; i++) {
                var x = new BaiIntersection {
                    Id = r.ReadUInt16(),
                    Room = r.ReadUInt16() - 1,
                    Center = r.ReadVec3()
                };
                int n = r.ReadUInt16();
                for (int k = 0; k < n; k++) x.Routes.Add(r.ReadUInt32());
                bai.Intersections.Add(x);
            }
            if (stream.Length - stream.Position >= 4) {
                long start = stream.Position;
                try {
                    uint rooms = r.ReadUInt32();
                    bai.CullLarge = ReadCulling(r, rooms);
                    bai.CullSmall = ReadCulling(r, rooms);
                } catch (EndOfStreamException) {
                    stream.Position = start;
                    bai.CullLarge = bai.CullSmall = null;
                }
            }
            if (bai.CullLarge != null) FileLog.Print($"Culling lists for {bai.CullLarge.Count} rooms");
            bai.Trailer = r.ReadBytes((int)(stream.Length - stream.Position));
            FileLog.Print($"Bytes left in file: {bai.Trailer.Length}");
            return bai;
        }

        static List<List<int>> ReadCulling(BinaryReader r, uint rooms) {
            var lists = new List<List<int>>();
            for (uint i = 0; i < rooms; i++) {
                int n = r.ReadUInt16();
                var list = new List<int>(n);
                for (int k = 0; k < n; k++) list.Add(r.ReadUInt16());
                lists.Add(list);
            }
            return lists;
        }

        static void WriteCulling(BinaryWriter w, List<List<int>> lists) {
            foreach (var list in lists) {
                w.Write((ushort)list.Count);
                foreach (int v in list) w.Write((ushort)v);
            }
        }

        static BaiRoute ReadRoute(BinaryReader r) {
            var route = new BaiRoute { Id = r.ReadUInt16() };
            int points = r.ReadUInt16();
            route.Flags = r.ReadUInt16();
            int rooms = r.ReadUInt16();
            for (int i = 0; i < rooms; i++) route.Rooms.Add(r.ReadUInt16() - 1);
            route.HalfWidth = r.ReadSingle();
            route.BaseSpeed = r.ReadSingle();
            route.Right = ReadRoadData(r, points);
            route.Left = ReadRoadData(r, points);
            for (int i = 0; i < points; i++) route.CentralDistances.Add(r.ReadSingle());
            ReadVectors(r, route.CentralLine, points);
            ReadVectors(r, route.XOrientation, points);
            ReadVectors(r, route.YOrientation, points);
            ReadVectors(r, route.ZOrientation, points);
            ReadVectors(r, route.Tangents, points);
            route.End = ReadRoadEnd(r);
            route.Start = ReadRoadEnd(r);
            return route;
        }

        static BaiRoadData ReadRoadData(BinaryReader r, int points) {
            var d = new BaiRoadData {
                Lanes = r.ReadUInt16(),
                Trams = r.ReadUInt16(),
                Trains = r.ReadUInt16(),
                Sidewalks = r.ReadUInt16(),
                AmbientTypes = r.ReadUInt16()
            };
            for (int i = 0; i < d.Lanes * points; i++) d.LaneDistances.Add(r.ReadSingle());
            for (int i = 0; i < points; i++) d.Distances.Add(r.ReadSingle());
            for (int i = 0; i < 11 + d.Lanes; i++) d.Unknown0.Add(r.ReadSingle());
            ReadVectors(r, d.LaneVertices, d.Lanes * points);
            ReadVectors(r, d.SidewalkCenter, points);
            ReadVectors(r, d.TramVertices, d.Trams * points);
            ReadVectors(r, d.TrainVertices, d.Trains * points);
            ReadVectors(r, d.SidewalkInner, points);
            ReadVectors(r, d.SidewalkOuter, points);
            return d;
        }

        static BaiRoadEnd ReadRoadEnd(BinaryReader r) {
            return new BaiRoadEnd {
                Intersection = r.ReadUInt32(),
                Unknown0 = r.ReadUInt16(),
                VehicleRule = r.ReadUInt32(),
                IntersectionRoadIndex = r.ReadUInt32(),
                Origin = r.ReadVec3(),
                Axis = r.ReadVec3()
            };
        }

        static void ReadVectors(BinaryReader r, List<Vector3> list, int count) {
            for (int i = 0; i < count; i++) list.Add(r.ReadVec3());
        }

        public void Save(string path) {
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                w.Write(BinaryUtil.Text.GetBytes("CAI1"));
                w.Write((ushort)Intersections.Count);
                w.Write((ushort)Routes.Count);
                foreach (var route in Routes) WriteRoute(w, route);
                foreach (var x in Intersections) {
                    w.Write((ushort)x.Id);
                    w.Write((ushort)(x.Room + 1));
                    w.Write(x.Center);
                    w.Write((ushort)x.Routes.Count);
                    foreach (uint id in x.Routes) w.Write(id);
                }
                if (CullLarge != null && CullSmall != null) {
                    w.Write((uint)CullLarge.Count);
                    WriteCulling(w, CullLarge);
                    WriteCulling(w, CullSmall);
                }
                w.Write(Trailer);
            }
            File.Move(temp, path, true);
        }

        static void WriteRoute(BinaryWriter w, BaiRoute route) {
            w.Write((ushort)route.Id);
            w.Write((ushort)route.Points);
            w.Write((ushort)route.Flags);
            w.Write((ushort)route.Rooms.Count);
            foreach (int b in route.Rooms) w.Write((ushort)(b + 1));
            w.Write(route.HalfWidth);
            w.Write(route.BaseSpeed);
            WriteRoadData(w, route.Right);
            WriteRoadData(w, route.Left);
            foreach (float f in route.CentralDistances) w.Write(f);
            WriteVectors(w, route.CentralLine);
            WriteVectors(w, route.XOrientation);
            WriteVectors(w, route.YOrientation);
            WriteVectors(w, route.ZOrientation);
            WriteVectors(w, route.Tangents);
            WriteRoadEnd(w, route.End);
            WriteRoadEnd(w, route.Start);
        }

        static void WriteRoadData(BinaryWriter w, BaiRoadData d) {
            w.Write((ushort)d.Lanes);
            w.Write((ushort)d.Trams);
            w.Write((ushort)d.Trains);
            w.Write((ushort)d.Sidewalks);
            w.Write((ushort)d.AmbientTypes);
            foreach (float f in d.LaneDistances) w.Write(f);
            foreach (float f in d.Distances) w.Write(f);
            foreach (float f in d.Unknown0) w.Write(f);
            WriteVectors(w, d.LaneVertices);
            WriteVectors(w, d.SidewalkCenter);
            WriteVectors(w, d.TramVertices);
            WriteVectors(w, d.TrainVertices);
            WriteVectors(w, d.SidewalkInner);
            WriteVectors(w, d.SidewalkOuter);
        }

        static void WriteRoadEnd(BinaryWriter w, BaiRoadEnd e) {
            w.Write(e.Intersection);
            w.Write((ushort)e.Unknown0);
            w.Write(e.VehicleRule);
            w.Write(e.IntersectionRoadIndex);
            w.Write(e.Origin);
            w.Write(e.Axis);
        }

        static void WriteVectors(BinaryWriter w, List<Vector3> list) {
            foreach (var v in list) w.Write(v);
        }
    }
}
