using System;
using System.Collections.Generic;
using System.IO;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// Builds the BLOCKMAP lump: a 128x128-unit grid where each cell lists the
    /// linedefs crossing it. This is what all of Doom's movement collision iterates,
    /// so it must be complete - a linedef missing from a cell it crosses is a wall
    /// you can walk through.
    /// </summary>
    internal static class BlockmapBuilder
    {
        private const int BlockSize = 128;

        public static byte[] Build(DoomMap map)
        {
            if (map.Vertices.Count == 0)
            {
                throw new InvalidOperationException("Cannot build a blockmap for an empty map.");
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var v in map.Vertices)
            {
                minX = Math.Min(minX, v.X);
                minY = Math.Min(minY, v.Y);
                maxX = Math.Max(maxX, v.X);
                maxY = Math.Max(maxY, v.Y);
            }

            var originX = minX - 8;
            var originY = minY - 8;
            var width = ((maxX - originX) / BlockSize) + 1;
            var height = ((maxY - originY) / BlockSize) + 1;

            var blocks = new List<int>[width * height];
            for (var i = 0; i < blocks.Length; i++)
            {
                blocks[i] = new List<int>();
            }

            for (var lineIndex = 0; lineIndex < map.Linedefs.Count; lineIndex++)
            {
                var line = map.Linedefs[lineIndex];
                var v1 = map.Vertices[line.V1];
                var v2 = map.Vertices[line.V2];

                // Conservative rasterization: test the segment against every block in
                // its bounding box. Building-scale maps have short lines, so the waste
                // over a true traversal is negligible and there is no gap risk.
                var bx1 = (Math.Min(v1.X, v2.X) - originX) / BlockSize;
                var bx2 = (Math.Max(v1.X, v2.X) - originX) / BlockSize;
                var by1 = (Math.Min(v1.Y, v2.Y) - originY) / BlockSize;
                var by2 = (Math.Max(v1.Y, v2.Y) - originY) / BlockSize;

                for (var by = by1; by <= by2; by++)
                {
                    for (var bx = bx1; bx <= bx2; bx++)
                    {
                        var blockLeft = originX + bx * BlockSize;
                        var blockBottom = originY + by * BlockSize;
                        if (SegmentIntersectsRect(
                                v1.X, v1.Y, v2.X, v2.Y,
                                blockLeft, blockBottom, blockLeft + BlockSize, blockBottom + BlockSize))
                        {
                            blocks[by * width + bx].Add(lineIndex);
                        }
                    }
                }
            }

            // Lump layout: 4 header words, W*H offset words, then the block lists.
            // ManagedDoom's IterateLines starts reading entries AT the offset target
            // and stops on -1, so each offset points at the first real entry - the
            // traditional leading 0 word is still written before each list (some
            // editors expect it) but skipped by the offset. Empty blocks share one
            // -1 terminator word.
            var table = new List<short>();
            table.Add(unchecked((short)originX));
            table.Add(unchecked((short)originY));
            table.Add((short)width);
            table.Add((short)height);

            var offsets = new int[width * height];
            for (var i = 0; i < offsets.Length; i++)
            {
                table.Add(0); // placeholder, patched below
            }

            var sharedEmptyOffset = table.Count;
            table.Add(-1);

            for (var i = 0; i < blocks.Length; i++)
            {
                if (blocks[i].Count == 0)
                {
                    offsets[i] = sharedEmptyOffset;
                    continue;
                }

                table.Add(0);
                offsets[i] = table.Count;
                foreach (var lineIndex in blocks[i])
                {
                    table.Add((short)lineIndex);
                }
                table.Add(-1);
            }

            // Offsets are read back as signed int16 word indices, so the whole lump
            // is capped at 32767 words - the classic 64KB blockmap limit.
            if (table.Count > short.MaxValue)
            {
                throw new InvalidOperationException(
                    "The building is too large for a Doom blockmap (" + table.Count +
                    " words, limit " + short.MaxValue + "). Export a smaller level.");
            }

            for (var i = 0; i < offsets.Length; i++)
            {
                table[4 + i] = (short)offsets[i];
            }

            using (var ms = new MemoryStream(table.Count * 2))
            using (var w = new BinaryWriter(ms))
            {
                foreach (var word in table)
                {
                    w.Write(word);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Segment/axis-aligned-rectangle overlap via the separating axis
        /// test: reject if the segment's bbox misses the rect, or if the whole rect
        /// lies strictly on one side of the segment's line.</summary>
        internal static bool SegmentIntersectsRect(
            int x1, int y1, int x2, int y2,
            int rectLeft, int rectBottom, int rectRight, int rectTop)
        {
            if (Math.Max(x1, x2) < rectLeft || Math.Min(x1, x2) > rectRight ||
                Math.Max(y1, y2) < rectBottom || Math.Min(y1, y2) > rectTop)
            {
                return false;
            }

            long dx = x2 - x1;
            long dy = y2 - y1;

            var s1 = Math.Sign(dx * (rectBottom - y1) - dy * (rectLeft - x1));
            var s2 = Math.Sign(dx * (rectBottom - y1) - dy * (rectRight - x1));
            var s3 = Math.Sign(dx * (rectTop - y1) - dy * (rectLeft - x1));
            var s4 = Math.Sign(dx * (rectTop - y1) - dy * (rectRight - x1));

            var allPositive = s1 > 0 && s2 > 0 && s3 > 0 && s4 > 0;
            var allNegative = s1 < 0 && s2 < 0 && s3 < 0 && s4 < 0;
            return !allPositive && !allNegative;
        }
    }
}
