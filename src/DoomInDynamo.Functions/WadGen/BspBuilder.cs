using System;
using System.Collections.Generic;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// Builds the SEGS/SSECTORS/NODES lumps. ManagedDoom is a vanilla port: unlike
    /// ZDoom-family engines it never rebuilds nodes itself, so the BSP written here
    /// is exactly what the renderer walks and what PointInSubsector uses for spawns
    /// and sound propagation - it has to be genuinely correct, not just present.
    ///
    /// Classic recursive seg partitioner (same scheme as id's original node
    /// builder): pick a seg, use its parent linedef's infinite line as the
    /// partition, sort every seg to the partition's front (right) or back (left)
    /// side - splitting the ones that straddle it - and recurse until each set is
    /// convex. Convex single-sector seg sets become subsectors.
    ///
    /// Side convention, verified against Geometry.PointOnSide: for partition
    /// (x, y, dx, dy), a point p is on the FRONT (side 0) when
    /// cross(d, p - o) = dx*(p.y - y) - dy*(p.x - x) &lt; 0, i.e. front is the
    /// right-hand side of the partition direction. Node children[0] (lump offset
    /// +24) must be the front child.
    /// </summary>
    internal static class BspBuilder
    {
        // "On the line" tolerance in map units. Splits produce fractional
        // coordinates; anything within a fiftieth of a unit of the partition is
        // treated as lying on it so rounding noise can't flip a side.
        private const double Eps = 0.02;

        private const int MaxDepth = 4000;

        private sealed class BSeg
        {
            public double X1, Y1, X2, Y2;
            public int Linedef;
            public int Side;
            public double Offset;
            public int FrontSector;
        }

        private struct Partition
        {
            // Stored exactly as the node record will be written, and used in that
            // (possibly halved) form for classification too, so the builder and the
            // engine always agree about which side a point is on.
            public int X, Y, Dx, Dy;
        }

        public static void Build(DoomMap map)
        {
            map.Segs.Clear();
            map.Subsectors.Clear();
            map.Nodes.Clear();

            var segs = new List<BSeg>();
            for (var i = 0; i < map.Linedefs.Count; i++)
            {
                var line = map.Linedefs[i];
                var v1 = map.Vertices[line.V1];
                var v2 = map.Vertices[line.V2];

                segs.Add(new BSeg
                {
                    X1 = v1.X, Y1 = v1.Y, X2 = v2.X, Y2 = v2.Y,
                    Linedef = i,
                    Side = 0,
                    Offset = 0,
                    FrontSector = map.Sidedefs[line.FrontSide].Sector
                });

                if (line.BackSide >= 0)
                {
                    segs.Add(new BSeg
                    {
                        X1 = v2.X, Y1 = v2.Y, X2 = v1.X, Y2 = v1.Y,
                        Linedef = i,
                        Side = 1,
                        Offset = 0,
                        FrontSector = map.Sidedefs[line.BackSide].Sector
                    });
                }
            }

            if (segs.Count == 0)
            {
                throw new InvalidOperationException("Cannot build a BSP for a map with no linedefs.");
            }

            var vertexLookup = new Dictionary<long, int>();
            for (var i = 0; i < map.Vertices.Count; i++)
            {
                vertexLookup[VertexKey(map.Vertices[i].X, map.Vertices[i].Y)] = i;
            }

            int[] rootBox;
            var root = BuildRecursive(map, segs, vertexLookup, 0, out rootBox);

            // A convex map (e.g. just the outer boundary) would produce zero nodes,
            // which ManagedDoom cannot represent: with an empty NODES lump its
            // renderer computes a start index of -1, and Node.GetSubsector(-1) is
            // 32767 - an instant out-of-range crash, not vanilla's "-1 means
            // subsector 0" special case. Wrap the lone subsector in one node whose
            // partition lies entirely to the left of the map so every point lands on
            // the front child; the never-matched back child points at the same
            // subsector and gets a far-away bounding box so rendering skips it.
            if ((root & 0x8000) != 0 && map.Nodes.Count == 0)
            {
                var guard = new MapNode
                {
                    X = rootBox[DoomConst.BoxLeft] - 64,
                    Y = 0,
                    Dx = 0,
                    Dy = 1,
                    FrontChild = root,
                    BackChild = root
                };
                Array.Copy(rootBox, guard.FrontBox, 4);
                guard.BackBox[DoomConst.BoxTop] = -32000;
                guard.BackBox[DoomConst.BoxBottom] = -32010;
                guard.BackBox[DoomConst.BoxLeft] = -32010;
                guard.BackBox[DoomConst.BoxRight] = -32000;
                map.Nodes.Add(guard);
            }

            if (map.Vertices.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many vertices for the Doom map format (" + map.Vertices.Count + ").");
            }
            if (map.Segs.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many segs for the Doom map format (" + map.Segs.Count + ").");
            }
        }

        private static int BuildRecursive(DoomMap map, List<BSeg> segs, Dictionary<long, int> vertexLookup, int depth, out int[] bbox)
        {
            Partition partition;
            if (IsConvex(segs) || depth >= MaxDepth || !TryChoosePartition(segs, out partition))
            {
                // Depth cap and no-valid-partition are belt-and-braces fallbacks: a
                // non-convex subsector renders with minor glitches but never crashes
                // or loops forever, which beats failing the whole export.
                return MakeSubsector(map, segs, vertexLookup, out bbox);
            }

            var front = new List<BSeg>();
            var back = new List<BSeg>();
            foreach (var seg in segs)
            {
                ClassifySeg(seg, partition, front, back);
            }

            if (front.Count == 0 || back.Count == 0)
            {
                return MakeSubsector(map, segs, vertexLookup, out bbox);
            }

            int[] frontBox;
            int[] backBox;
            var frontChild = BuildRecursive(map, front, vertexLookup, depth + 1, out frontBox);
            var backChild = BuildRecursive(map, back, vertexLookup, depth + 1, out backBox);

            var node = new MapNode
            {
                X = partition.X,
                Y = partition.Y,
                Dx = partition.Dx,
                Dy = partition.Dy,
                FrontChild = frontChild,
                BackChild = backChild
            };
            Array.Copy(frontBox, node.FrontBox, 4);
            Array.Copy(backBox, node.BackBox, 4);

            map.Nodes.Add(node);
            if (map.Nodes.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many BSP nodes for the Doom map format.");
            }

            bbox = MergeBox(frontBox, backBox);
            return map.Nodes.Count - 1;
        }

        private static bool IsConvex(List<BSeg> segs)
        {
            // A seg set is a valid subsector when no seg pokes out to the LEFT of
            // any other seg's line (all segs face into a common convex region) and
            // they agree on the front sector. Early-exit keeps this cheap for the
            // big non-convex sets near the root.
            for (var i = 0; i < segs.Count; i++)
            {
                var a = segs[i];
                var dx = a.X2 - a.X1;
                var dy = a.Y2 - a.Y1;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < Eps)
                {
                    continue;
                }

                for (var j = 0; j < segs.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    var b = segs[j];
                    if (b.FrontSector != a.FrontSector)
                    {
                        return false;
                    }

                    var d1 = (dx * (b.Y1 - a.Y1) - dy * (b.X1 - a.X1)) / len;
                    var d2 = (dx * (b.Y2 - a.Y1) - dy * (b.X2 - a.X1)) / len;
                    if (d1 > Eps || d2 > Eps)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryChoosePartition(List<BSeg> segs, out Partition best)
        {
            best = default(Partition);
            var bestScore = long.MaxValue;
            var found = false;

            var step = Math.Max(1, segs.Count / 60);
            for (var i = 0; i < segs.Count; i += step)
            {
                Partition candidate;
                if (!TryMakePartition(segs[i], out candidate))
                {
                    continue;
                }

                var frontCount = 0;
                var backCount = 0;
                var splitCount = 0;
                foreach (var seg in segs)
                {
                    switch (ClassifyOnly(seg, candidate))
                    {
                        case SegSide.Front:
                            frontCount++;
                            break;
                        case SegSide.Back:
                            backCount++;
                            break;
                        default:
                            splitCount++;
                            break;
                    }
                }

                // A usable partition must actually separate something.
                if (frontCount + splitCount == 0 || backCount + splitCount == 0)
                {
                    continue;
                }

                var score = 8L * splitCount + Math.Abs(frontCount - backCount);
                if (candidate.Dx != 0 && candidate.Dy != 0)
                {
                    score += 2; // mild preference for axis-aligned partitions
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    found = true;
                }
            }

            if (!found)
            {
                return TryAxisFallback(segs, out best);
            }

            return true;
        }

        /// <summary>
        /// Last-resort partition when no seg's own line separates the set (possible
        /// with clusters of mutually-facing loops): split on an axis-aligned line
        /// through the median endpoint coordinate. A partition line does not have to
        /// come from any linedef - the engine only ever evaluates it geometrically.
        /// </summary>
        private static bool TryAxisFallback(List<BSeg> segs, out Partition partition)
        {
            var xs = new List<int>(segs.Count * 2);
            var ys = new List<int>(segs.Count * 2);
            foreach (var seg in segs)
            {
                xs.Add((int)Math.Round(seg.X1));
                xs.Add((int)Math.Round(seg.X2));
                ys.Add((int)Math.Round(seg.Y1));
                ys.Add((int)Math.Round(seg.Y2));
            }
            xs.Sort();
            ys.Sort();

            var spanX = xs[xs.Count - 1] - xs[0];
            var spanY = ys[ys.Count - 1] - ys[0];

            // Vertical cut first if the set is wider than tall, otherwise
            // horizontal; try the other axis if the first fails to separate.
            var tryVerticalFirst = spanX >= spanY;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var vertical = tryVerticalFirst == (attempt == 0);
                var coords = vertical ? xs : ys;
                var median = coords[coords.Count / 2];

                var candidate = vertical
                    ? new Partition { X = median, Y = 0, Dx = 0, Dy = 1 }
                    : new Partition { X = 0, Y = median, Dx = 1, Dy = 0 };

                var frontCount = 0;
                var backCount = 0;
                var splitCount = 0;
                foreach (var seg in segs)
                {
                    switch (ClassifyOnly(seg, candidate))
                    {
                        case SegSide.Front:
                            frontCount++;
                            break;
                        case SegSide.Back:
                            backCount++;
                            break;
                        default:
                            splitCount++;
                            break;
                    }
                }

                if (frontCount + splitCount > 0 && backCount + splitCount > 0 &&
                    (frontCount != segs.Count) && (backCount != segs.Count))
                {
                    partition = candidate;
                    return true;
                }
            }

            partition = default(Partition);
            return false;
        }

        private static bool TryMakePartition(BSeg seg, out Partition partition)
        {
            // Use the seg's full direction but rounded endpoints; initial segs have
            // integer endpoints already, split segs are within Eps of them. Node
            // deltas are int16, so halve oversized ones - the only lines long enough
            // to need it are the axis-aligned outer boundary's, where integer
            // halving is exact and the line is unchanged.
            var x = (int)Math.Round(seg.X1);
            var y = (int)Math.Round(seg.Y1);
            var dx = (int)Math.Round(seg.X2) - x;
            var dy = (int)Math.Round(seg.Y2) - y;

            while (Math.Abs(dx) > 32000 || Math.Abs(dy) > 32000)
            {
                dx /= 2;
                dy /= 2;
            }

            partition = new Partition { X = x, Y = y, Dx = dx, Dy = dy };
            return dx != 0 || dy != 0;
        }

        private enum SegSide
        {
            Front,
            Back,
            Straddle
        }

        private static SegSide ClassifyOnly(BSeg seg, Partition p)
        {
            var len = Math.Sqrt((double)p.Dx * p.Dx + (double)p.Dy * p.Dy);
            var d1 = (p.Dx * (seg.Y1 - p.Y) - p.Dy * (seg.X1 - p.X)) / len;
            var d2 = (p.Dx * (seg.Y2 - p.Y) - p.Dy * (seg.X2 - p.X)) / len;

            var on1 = Math.Abs(d1) <= Eps;
            var on2 = Math.Abs(d2) <= Eps;

            if (on1 && on2)
            {
                // Collinear: goes with the side its direction faces, so the
                // partition seg itself (and anything parallel to it) lands on the
                // front. Matches the classic node builders.
                var dot = (double)p.Dx * (seg.X2 - seg.X1) + (double)p.Dy * (seg.Y2 - seg.Y1);
                return dot >= 0 ? SegSide.Front : SegSide.Back;
            }

            if (d1 <= Eps && d2 <= Eps)
            {
                return SegSide.Front;
            }

            if (d1 >= -Eps && d2 >= -Eps)
            {
                return SegSide.Back;
            }

            return SegSide.Straddle;
        }

        private static void ClassifySeg(BSeg seg, Partition p, List<BSeg> front, List<BSeg> back)
        {
            switch (ClassifyOnly(seg, p))
            {
                case SegSide.Front:
                    front.Add(seg);
                    return;
                case SegSide.Back:
                    back.Add(seg);
                    return;
            }

            var len = Math.Sqrt((double)p.Dx * p.Dx + (double)p.Dy * p.Dy);
            var d1 = (p.Dx * (seg.Y1 - p.Y) - p.Dy * (seg.X1 - p.X)) / len;
            var d2 = (p.Dx * (seg.Y2 - p.Y) - p.Dy * (seg.X2 - p.X)) / len;

            var t = d1 / (d1 - d2);
            if (t < 1e-9)
            {
                t = 1e-9;
            }
            if (t > 1 - 1e-9)
            {
                t = 1 - 1e-9;
            }

            var sx = seg.X1 + t * (seg.X2 - seg.X1);
            var sy = seg.Y1 + t * (seg.Y2 - seg.Y1);
            var firstLen = Math.Sqrt((sx - seg.X1) * (sx - seg.X1) + (sy - seg.Y1) * (sy - seg.Y1));

            var first = new BSeg
            {
                X1 = seg.X1, Y1 = seg.Y1, X2 = sx, Y2 = sy,
                Linedef = seg.Linedef, Side = seg.Side,
                Offset = seg.Offset, FrontSector = seg.FrontSector
            };
            var second = new BSeg
            {
                X1 = sx, Y1 = sy, X2 = seg.X2, Y2 = seg.Y2,
                Linedef = seg.Linedef, Side = seg.Side,
                Offset = seg.Offset + firstLen, FrontSector = seg.FrontSector
            };

            // d1 < 0 means the seg STARTS on the front side.
            if (d1 < 0)
            {
                front.Add(first);
                back.Add(second);
            }
            else
            {
                back.Add(first);
                front.Add(second);
            }
        }

        private static int MakeSubsector(DoomMap map, List<BSeg> segs, Dictionary<long, int> vertexLookup, out int[] bbox)
        {
            bbox = new int[4];
            bbox[DoomConst.BoxTop] = int.MinValue;
            bbox[DoomConst.BoxBottom] = int.MaxValue;
            bbox[DoomConst.BoxLeft] = int.MaxValue;
            bbox[DoomConst.BoxRight] = int.MinValue;

            var first = map.Segs.Count;
            foreach (var seg in segs)
            {
                var x1 = (int)Math.Round(seg.X1);
                var y1 = (int)Math.Round(seg.Y1);
                var x2 = (int)Math.Round(seg.X2);
                var y2 = (int)Math.Round(seg.Y2);

                // Angle from the un-rounded endpoints (more precise), stored as
                // 16-bit BAMS: full circle = 65536.
                var bams = (int)Math.Round(Math.Atan2(seg.Y2 - seg.Y1, seg.X2 - seg.X1) * 32768.0 / Math.PI);

                map.Segs.Add(new MapSeg
                {
                    V1 = GetVertex(map, vertexLookup, x1, y1),
                    V2 = GetVertex(map, vertexLookup, x2, y2),
                    AngleBams = unchecked((short)bams),
                    Linedef = seg.Linedef,
                    Side = seg.Side,
                    Offset = (int)Math.Round(seg.Offset)
                });

                bbox[DoomConst.BoxTop] = Math.Max(bbox[DoomConst.BoxTop], Math.Max(y1, y2));
                bbox[DoomConst.BoxBottom] = Math.Min(bbox[DoomConst.BoxBottom], Math.Min(y1, y2));
                bbox[DoomConst.BoxLeft] = Math.Min(bbox[DoomConst.BoxLeft], Math.Min(x1, x2));
                bbox[DoomConst.BoxRight] = Math.Max(bbox[DoomConst.BoxRight], Math.Max(x1, x2));
            }

            map.Subsectors.Add(new MapSubsector { SegCount = segs.Count, FirstSeg = first });
            if (map.Subsectors.Count >= 0x8000)
            {
                // Index 0x7FFF is unusable: 0x8000|0x7FFF is int16 -1, which the
                // engine reserves as the "no nodes" sentinel meaning subsector 0.
                throw new InvalidOperationException("Too many subsectors for the Doom map format (" + map.Subsectors.Count + ").");
            }

            return 0x8000 | (map.Subsectors.Count - 1);
        }

        private static int GetVertex(DoomMap map, Dictionary<long, int> lookup, int x, int y)
        {
            var key = VertexKey(x, y);
            int index;
            if (lookup.TryGetValue(key, out index))
            {
                return index;
            }

            map.Vertices.Add(new MapVertex(x, y));
            index = map.Vertices.Count - 1;
            lookup[key] = index;
            return index;
        }

        private static long VertexKey(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        private static int[] MergeBox(int[] a, int[] b)
        {
            var box = new int[4];
            box[DoomConst.BoxTop] = Math.Max(a[DoomConst.BoxTop], b[DoomConst.BoxTop]);
            box[DoomConst.BoxBottom] = Math.Min(a[DoomConst.BoxBottom], b[DoomConst.BoxBottom]);
            box[DoomConst.BoxLeft] = Math.Min(a[DoomConst.BoxLeft], b[DoomConst.BoxLeft]);
            box[DoomConst.BoxRight] = Math.Max(a[DoomConst.BoxRight], b[DoomConst.BoxRight]);
            return box;
        }
    }
}
