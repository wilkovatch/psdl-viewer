using System;
using System.Collections.Generic;
using Godot;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Render {
    /// <summary>
    /// Places the roadside props of the room paths (the last PSDL section): the prop rule of the first room of a path selects
    /// the rules whose props are repeated along the left and right sidewalks of the roads of every room of the path.
    /// Also places the traffic lights of the BAI road ends.
    /// </summary>
    public static class Propulator {
        /// <summary>The inner sidewalk edge is raised to the top of the curb.</summary>
        const float CurbHeight = 0.15f;
        const uint Seed = int.MaxValue / 2;

        /// <summary>Calls add with the PKG name and the transform of every prop. Safe to call from any thread.</summary>
        public static void Place(PsdlFile psdl, PropRuleSet rules, Action<string, Transform3D> add, Progress progress) {
            progress.Begin("Placing props", psdl.RoomPaths.Count);
            // how far into the road each definition is and how many times it was used, kept along the whole path
            var state = new Dictionary<PropDef, (float offset, int used)>();
            for (int p = 0; p < psdl.RoomPaths.Count; p++) {
                progress.Value = p;
                var path = psdl.RoomPaths[p];
                if (path.Rooms.Count == 0 || !ValidRoom(psdl, path.Rooms[0])) continue;
                var sides = rules.ForRoom(psdl.Rooms[path.Rooms[0]].PropRule);
                if (sides == null) continue;
                var (left, right) = sides.Value;
                state.Clear();
                foreach (int room in path.Rooms) {
                    if (!ValidRoom(psdl, room)) continue;
                    // every room repeats the same random sequence
                    var random = new XorShift(Seed);
                    foreach (var attribute in psdl.Rooms[room].Attributes) {
                        if (!Sidewalks(psdl, attribute, out var leftOuter, out var leftInner, out var rightInner, out var rightOuter)) continue;
                        var center = new Vector3[leftInner.Length];
                        for (int i = 0; i < center.Length; i++) center[i] = (leftInner[i] + rightInner[i]) / 2;
                        PlaceSide(leftOuter, leftInner, center, left, state, random, add);
                        PlaceSide(rightOuter, rightInner, center, right, state, random, add);
                    }
                }
            }
        }

        /// <summary>
        /// Calls add with the PKG name and the transform of the traffic light of every BAI road end that has the traffic light
        /// rule and a light position: the single light where its side of the road has one lane, the dual one otherwise.
        /// </summary>
        public static void PlaceTrafficLights(BaiFile bai, PropRuleSet rules, Action<string, Transform3D> add) {
            foreach (var route in bai.Routes) {
                // the side stored first goes with the end stored first
                PlaceTrafficLight(route.End, route.Right, rules, add);
                PlaceTrafficLight(route.Start, route.Left, rules, add);
            }
        }

        static void PlaceTrafficLight(BaiRoadEnd end, BaiRoadData side, PropRuleSet rules, Action<string, Transform3D> add) {
            if (end.Intersection == BaiRoadEnd.None || end.VehicleRule != BaiRoadEnd.RuleTrafficLight) return;
            // no light is shown at (0, 0, 0)
            if (end.Origin == System.Numerics.Vector3.Zero) return;
            var origin = end.Origin.ToGodot();            string model = side.Lanes <= 1 ? rules.SingleTrafficLight : rules.DualTrafficLight;
            add(model, new Transform3D(CityScene.FacingBasis(end.Axis.ToGodot() - origin), origin));
        }

        static bool ValidRoom(PsdlFile psdl, int room) => room >= 0 && room < psdl.Rooms.Count;

        /// <summary>The outer and inner (curb) edges of both sidewalks of a road with sidewalks or a divided road.</summary>
        static bool Sidewalks(PsdlFile psdl, RoomAttribute attribute, out Vector3[] leftOuter, out Vector3[] leftInner, out Vector3[] rightInner, out Vector3[] rightOuter) {
            leftOuter = leftInner = rightInner = rightOuter = null;
            List<int> vertices;
            int stride, lo, li, ri, ro;
            if (attribute is StripAttribute { Type: AttributeType.RoadWithSidewalks } strip) {
                vertices = strip.Vertices;
                (stride, lo, li, ri, ro) = (4, 0, 1, 2, 3);
            } else if (attribute is DividedRoadAttribute divided) {
                vertices = divided.Vertices;
                (stride, lo, li, ri, ro) = (6, 0, 1, 4, 5);
            } else {
                return false;
            }
            int sections = vertices.Count / stride;
            if (sections < 2) return false;
            foreach (int v in vertices) {
                if (v < 0 || v >= psdl.Vertices.Count) return false;
            }
            Vector3 At(int section, int offset) => psdl.Vertices[vertices[section * stride + offset]].ToGodot();
            var up = new Vector3(0, CurbHeight, 0);
            leftOuter = new Vector3[sections];
            leftInner = new Vector3[sections];
            rightInner = new Vector3[sections];
            rightOuter = new Vector3[sections];
            for (int i = 0; i < sections; i++) {
                leftOuter[i] = At(i, lo);
                leftInner[i] = At(i, li) + up;
                rightInner[i] = At(i, ri) + up;
                rightOuter[i] = At(i, ro);
            }
            return true;
        }

        /// <summary>
        /// Walks along the road centre placing the props of each definition every Spacing metres, starting StartDist metres
        /// after where the previous road of the path left off; each prop is placed at a random point across the sidewalk.
        /// </summary>
        static void PlaceSide(Vector3[] outer, Vector3[] inner, Vector3[] center, PropRule rule, Dictionary<PropDef, (float offset, int used)> state,
            XorShift random, Action<string, Transform3D> add) {
            foreach (var def in rule.Defs) {
                if (def.Models.Count == 0 || def.MaxUse <= 0) continue;
                state.TryGetValue(def, out var s);
                float current = def.StartDist + s.offset;
                int used = s.used;
                for (int i = 0; i + 1 < center.Length; i++) {
                    float length = center[i].DistanceTo(center[i + 1]);
                    while (current <= length && used < def.MaxUse) {
                        float t = length > 0 ? current / length : 0;
                        float across = random.Range(def.MinLerp, def.MaxLerp);
                        string model = def.Models[random.Range(def.Models.Count)];
                        var position = inner[i].Lerp(inner[i + 1], t).Lerp(outer[i].Lerp(outer[i + 1], t), across);
                        var road = center[i].Lerp(center[i + 1], t);
                        // the props face the road with their -X side
                        add(model, new Transform3D(CityScene.FacingBasis(position - road), position));
                        current += def.Spacing;
                        used++;
                    }
                    // used up on this road
                    if (current <= length) break;
                    current -= length;
                }
                state[def] = (current, used);
            }
        }

        /// <summary>xorshift128 random numbers with a fixed seed, so the props are placed the same way every time.</summary>
        class XorShift {
            uint x, y, z, w;

            public XorShift(uint seed) {
                x = seed;
                y = x * 1812433253 + 1;
                z = y * 1812433253 + 1;
                w = z * 1812433253 + 1;
            }

            uint Next() {
                uint t = x ^ (x << 11);
                x = y;
                y = z;
                z = w;
                return w = w ^ (w >> 19) ^ t ^ (t >> 8);
            }

            public float Range(float min, float max) => min + (max - min) * ((Next() & 0x7fffff) / 8388607f);

            public int Range(int count) => (int)(Next() % (uint)count);
        }
    }
}
