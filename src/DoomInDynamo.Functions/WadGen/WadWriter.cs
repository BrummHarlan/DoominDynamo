using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// Serializes a finished <see cref="DoomMap"/> into a PWAD file. The same map is
    /// written twice, under both the E1M1 and MAP01 slots, so the result works no
    /// matter which IWAD family the player browses (doom1/doom -> ExMy naming,
    /// doom2/plutonia/tnt -> MAPxx naming). ManagedDoom's Wad.GetLumpNumber searches
    /// the directory back-to-front, so these PWAD lumps override the IWAD's own map.
    /// </summary>
    internal static class WadWriter
    {
        public static void Write(DoomMap map, string path)
        {
            // Linedef and sidedef indices are signed int16 in seg and linedef
            // records; the other counts are guarded where they grow (BspBuilder,
            // BlockmapBuilder), but sidedefs can outrun them on two-sided-heavy
            // maps (2 per door/low-wall/stair line), so check here at the choke
            // point before anything gets written.
            if (map.Linedefs.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many linedefs for the Doom map format (" + map.Linedefs.Count + ").");
            }
            if (map.Sidedefs.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many sidedefs for the Doom map format (" + map.Sidedefs.Count + ").");
            }
            if (map.Sectors.Count > short.MaxValue)
            {
                throw new InvalidOperationException("Too many sectors for the Doom map format (" + map.Sectors.Count + ").");
            }

            // MapBuilder budgets every geometry source into int16 coordinates; a
            // vertex outside the range means some future source bypassed that, and
            // wrapping it here would write a silently corrupt map.
            foreach (var v in map.Vertices)
            {
                if (v.X < short.MinValue || v.X > short.MaxValue ||
                    v.Y < short.MinValue || v.Y > short.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Vertex (" + v.X + ", " + v.Y + ") is outside Doom's 16-bit coordinate range - geometry escaped the map budget.");
                }
            }

            var lumps = new List<KeyValuePair<string, byte[]>>();
            foreach (var slot in new[] { "MAP01", "E1M1" })
            {
                AddMapLumps(lumps, map, slot);
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                // Header: identification, lump count, directory offset (patched below).
                w.Write(Encoding.ASCII.GetBytes("PWAD"));
                w.Write(lumps.Count);
                w.Write(0);

                var positions = new int[lumps.Count];
                for (var i = 0; i < lumps.Count; i++)
                {
                    positions[i] = (int)stream.Position;
                    w.Write(lumps[i].Value);
                }

                var directoryOffset = (int)stream.Position;
                for (var i = 0; i < lumps.Count; i++)
                {
                    w.Write(positions[i]);
                    w.Write(lumps[i].Value.Length);
                    w.Write(PadName(lumps[i].Key));
                }

                stream.Seek(8, SeekOrigin.Begin);
                w.Write(directoryOffset);
            }
        }

        private static void AddMapLumps(List<KeyValuePair<string, byte[]>> lumps, DoomMap map, string slot)
        {
            // Lump order is load-bearing: Map.cs addresses everything as fixed
            // offsets from the marker (THINGS = marker+1 ... BLOCKMAP = marker+10).
            lumps.Add(new KeyValuePair<string, byte[]>(slot, Array.Empty<byte>()));
            lumps.Add(new KeyValuePair<string, byte[]>("THINGS", BuildThings(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("LINEDEFS", BuildLinedefs(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("SIDEDEFS", BuildSidedefs(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("VERTEXES", BuildVertexes(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("SEGS", BuildSegs(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("SSECTORS", BuildSubsectors(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("NODES", BuildNodes(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("SECTORS", BuildSectors(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("REJECT", BuildReject(map)));
            lumps.Add(new KeyValuePair<string, byte[]>("BLOCKMAP", BlockmapBuilder.Build(map)));
        }

        private static byte[] BuildThings(DoomMap map)
        {
            return Build(map.Things.Count, 10, (w, i) =>
            {
                var t = map.Things[i];
                WriteInt16(w, t.X);
                WriteInt16(w, t.Y);
                WriteInt16(w, t.AngleDegrees);
                WriteInt16(w, t.Type);
                WriteInt16(w, t.Flags);
            });
        }

        private static byte[] BuildLinedefs(DoomMap map)
        {
            return Build(map.Linedefs.Count, 14, (w, i) =>
            {
                var l = map.Linedefs[i];
                WriteInt16(w, l.V1);
                WriteInt16(w, l.V2);
                WriteInt16(w, l.Flags);
                WriteInt16(w, l.Special);
                WriteInt16(w, l.Tag);
                WriteInt16(w, l.FrontSide);
                WriteInt16(w, l.BackSide);
            });
        }

        private static byte[] BuildSidedefs(DoomMap map)
        {
            return Build(map.Sidedefs.Count, 30, (w, i) =>
            {
                var s = map.Sidedefs[i];
                WriteInt16(w, s.XOffset);
                WriteInt16(w, s.YOffset);
                w.Write(PadName(s.Upper));
                w.Write(PadName(s.Lower));
                w.Write(PadName(s.Middle));
                WriteInt16(w, s.Sector);
            });
        }

        private static byte[] BuildVertexes(DoomMap map)
        {
            return Build(map.Vertices.Count, 4, (w, i) =>
            {
                WriteInt16(w, map.Vertices[i].X);
                WriteInt16(w, map.Vertices[i].Y);
            });
        }

        private static byte[] BuildSegs(DoomMap map)
        {
            return Build(map.Segs.Count, 12, (w, i) =>
            {
                var s = map.Segs[i];
                WriteInt16(w, s.V1);
                WriteInt16(w, s.V2);
                w.Write(s.AngleBams);
                WriteInt16(w, s.Linedef);
                WriteInt16(w, s.Side);
                WriteInt16(w, s.Offset);
            });
        }

        private static byte[] BuildSubsectors(DoomMap map)
        {
            return Build(map.Subsectors.Count, 4, (w, i) =>
            {
                WriteInt16(w, map.Subsectors[i].SegCount);
                WriteInt16(w, map.Subsectors[i].FirstSeg);
            });
        }

        private static byte[] BuildNodes(DoomMap map)
        {
            return Build(map.Nodes.Count, 28, (w, i) =>
            {
                var n = map.Nodes[i];
                WriteInt16(w, n.X);
                WriteInt16(w, n.Y);
                WriteInt16(w, n.Dx);
                WriteInt16(w, n.Dy);
                for (var b = 0; b < 4; b++)
                {
                    WriteInt16(w, n.FrontBox[b]);
                }
                for (var b = 0; b < 4; b++)
                {
                    WriteInt16(w, n.BackBox[b]);
                }
                WriteInt16(w, n.FrontChild);
                WriteInt16(w, n.BackChild);
            });
        }

        private static byte[] BuildSectors(DoomMap map)
        {
            return Build(map.Sectors.Count, 26, (w, i) =>
            {
                var s = map.Sectors[i];
                WriteInt16(w, s.FloorHeight);
                WriteInt16(w, s.CeilingHeight);
                w.Write(PadName(s.FloorFlat));
                w.Write(PadName(s.CeilingFlat));
                WriteInt16(w, s.LightLevel);
                WriteInt16(w, s.Special);
                WriteInt16(w, s.Tag);
            });
        }

        private static byte[] BuildReject(DoomMap map)
        {
            // All zeros = "no sector pair is rejected", i.e. monsters always try line
            // of sight - always safe, just no speedup. The engine pads a short lump
            // itself, but write the proper size anyway.
            var sectors = map.Sectors.Count;
            return new byte[(sectors * sectors + 7) / 8];
        }

        private static byte[] Build(int count, int recordSize, Action<BinaryWriter, int> writeRecord)
        {
            using (var ms = new MemoryStream(count * recordSize))
            using (var w = new BinaryWriter(ms))
            {
                for (var i = 0; i < count; i++)
                {
                    writeRecord(w, i);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Values out of int16 range indicate a builder bug (MapBuilder is
        /// responsible for keeping the map inside Doom's limits), except seg texture
        /// offsets which legitimately wrap on very long boundary lines - so wrap
        /// silently, exactly like the classic tools did.</summary>
        internal static void WriteInt16(BinaryWriter w, int value)
        {
            w.Write(unchecked((short)value));
        }

        internal static byte[] PadName(string name)
        {
            var bytes = new byte[8];
            var upper = (name ?? "-").ToUpperInvariant();
            for (var i = 0; i < upper.Length && i < 8; i++)
            {
                bytes[i] = (byte)upper[i];
            }
            return bytes;
        }
    }
}
