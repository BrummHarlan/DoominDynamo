using System.Collections.Generic;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// The in-memory Doom map being assembled: editing-level structures (vertices,
    /// linedefs, sidedefs, sectors, things) filled by <see cref="MapBuilder"/>, plus
    /// the derived BSP/blockmap structures (segs, subsectors, nodes) filled by
    /// <see cref="BspBuilder"/>. All coordinates are final Doom map units and must
    /// stay within int16 range - MapBuilder's scaling/centering guarantees that.
    /// </summary>
    internal sealed class DoomMap
    {
        public readonly List<MapVertex> Vertices = new List<MapVertex>();
        public readonly List<MapLinedef> Linedefs = new List<MapLinedef>();
        public readonly List<MapSidedef> Sidedefs = new List<MapSidedef>();
        public readonly List<MapSector> Sectors = new List<MapSector>();
        public readonly List<MapThingRec> Things = new List<MapThingRec>();

        public readonly List<MapSeg> Segs = new List<MapSeg>();
        public readonly List<MapSubsector> Subsectors = new List<MapSubsector>();
        public readonly List<MapNode> Nodes = new List<MapNode>();
    }

    internal struct MapVertex
    {
        public int X;
        public int Y;

        public MapVertex(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    internal sealed class MapLinedef
    {
        public int V1;
        public int V2;
        public int Flags;
        public int Special;
        public int Tag;
        public int FrontSide;      // index into Sidedefs
        public int BackSide = -1;  // -1 = one-sided
    }

    internal sealed class MapSidedef
    {
        public int XOffset;
        public int YOffset;
        public string Upper = "-";
        public string Lower = "-";
        public string Middle = "-";
        public int Sector;
    }

    internal sealed class MapSector
    {
        public int FloorHeight;
        public int CeilingHeight;
        public string FloorFlat;
        public string CeilingFlat;
        public int LightLevel;
        public int Special;
        public int Tag;
    }

    internal sealed class MapThingRec
    {
        public int X;
        public int Y;
        public int AngleDegrees;
        public int Type;
        public int Flags;
    }

    internal sealed class MapSeg
    {
        public int V1;
        public int V2;
        public short AngleBams;
        public int Linedef;
        public int Side;    // 0 = same direction as linedef, 1 = opposite
        public int Offset;  // map units from the linedef's start vertex (v2 if Side==1)
    }

    internal struct MapSubsector
    {
        public int SegCount;
        public int FirstSeg;
    }

    internal sealed class MapNode
    {
        // Partition line. The engine's PointOnSide computes
        // cross(partition delta, point - partition origin) and calls cross < 0 the
        // FRONT (side 0) - i.e. front is to the RIGHT of the partition direction.
        public int X;
        public int Y;
        public int Dx;
        public int Dy;

        // Bounding boxes as (top, bottom, left, right) - the order Node.FromData reads.
        public int[] FrontBox = new int[4];
        public int[] BackBox = new int[4];

        // Child references: subsector indices are written as (0x8000 | index),
        // node indices written plain. Front child lands at lump offset +24, which
        // ManagedDoom reads into Children[0] - the one PointOnSide==0 selects.
        public int FrontChild;
        public int BackChild;
    }

    internal static class DoomConst
    {
        public const int LineFlagBlocking = 0x0001;
        public const int LineFlagTwoSided = 0x0004;
        public const int LineFlagUpperUnpegged = 0x0008;
        public const int LineFlagLowerUnpegged = 0x0010;

        public const int ThingAllSkills = 0x0007;

        // "DR" manual door: opens on use from either side (acts on the line's back
        // sector), waits, closes again; monsters can open it too.
        public const int SpecialDRDoor = 1;

        public const int SpecialS1Exit = 11;

        // Bounding box index order used by node records (matches vendor Box.cs).
        public const int BoxTop = 0;
        public const int BoxBottom = 1;
        public const int BoxLeft = 2;
        public const int BoxRight = 3;
    }
}
