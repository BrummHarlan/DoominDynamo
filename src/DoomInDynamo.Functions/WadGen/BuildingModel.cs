using System.Collections.Generic;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// One straight run of wall centerline. The Revit extractor tessellates curved
    /// walls into several of these and cuts door openings out beforehand, so by the
    /// time WadGen sees a WallSegment it is always "solid wall for its whole length".
    /// Coordinates are Revit internal units (feet), in world coordinates.
    /// </summary>
    internal sealed class WallSegment
    {
        public double X1;
        public double Y1;
        public double X2;
        public double Y2;
        public double ThicknessFt;
        public double HeightFt;
    }

    /// <summary>A point known to lie inside a room - used only as a placement hint
    /// (the player start prefers to spawn near one), in feet.</summary>
    internal sealed class RoomPoint
    {
        public double X;
        public double Y;
    }

    /// <summary>
    /// One door opening the extractor cut out of a wall: where it sits on the
    /// centerline, which way the wall runs there, and how big the hole is. WadGen
    /// turns each one into a real working Doom door (a closed sector that raises
    /// on use) standing in the gap. All values in feet / unit vectors.
    /// </summary>
    internal sealed class DoorOpening
    {
        public double CX;
        public double CY;
        public double DirX;
        public double DirY;
        public double WidthFt;
        public double ThicknessFt;
    }

    /// <summary>
    /// One window opening cut out of a wall: like a <see cref="DoorOpening"/> plus
    /// the sill and head heights. WadGen turns it into a see-through (and
    /// shoot-through, but not walk-through) opening between those heights.
    /// Feet / unit vectors.
    /// </summary>
    internal sealed class WindowOpening
    {
        public double CX;
        public double CY;
        public double DirX;
        public double DirY;
        public double WidthFt;
        public double ThicknessFt;
        public double SillFt;
        public double HeadFt;

        /// <summary>Height of the wall the window sits in - a window in a low
        /// (see-over) wall must not grow a full-height bay above its head.</summary>
        public double HostHeightFt;
    }

    /// <summary>
    /// A box-shaped obstacle standing on the floor - furniture, casework, a column.
    /// WadGen turns it into either a solid pillar (tall enough to reach headroom)
    /// or a raised-floor block the player can see and shoot over. Plan rectangle
    /// given by center/axis/half-sizes, all in feet.
    /// </summary>
    internal sealed class Prism
    {
        public double CX;
        public double CY;
        public double DirX = 1;   // unit vector of the rectangle's local X axis
        public double DirY;
        public double HalfLenFt;  // along Dir
        public double HalfWidthFt;
        public double HeightFt;
    }

    /// <summary>
    /// One straight stair run: centerline from the bottom end to the top end,
    /// tread width, and total rise. WadGen slices it into climbable raised-floor
    /// steps (Doom's step limit is 24 units, so risers get clamped). Feet.
    /// </summary>
    internal sealed class StairFlight
    {
        public double X1;
        public double Y1;
        public double X2;
        public double Y2;
        public double WidthFt;
        public double RiseFt;
    }

    /// <summary>
    /// Everything WadGen needs to build a map, extracted from Revit (or synthesized
    /// by the smoke test). Deliberately free of any Revit API types so the whole
    /// geometry/binary pipeline compiles and runs without Revit present.
    /// </summary>
    internal sealed class BuildingModel
    {
        public List<WallSegment> Walls = new List<WallSegment>();
        public List<RoomPoint> Rooms = new List<RoomPoint>();
        public List<DoorOpening> Doors = new List<DoorOpening>();
        public List<WindowOpening> Windows = new List<WindowOpening>();
        public List<Prism> Prisms = new List<Prism>();
        public List<StairFlight> Stairs = new List<StairFlight>();
        public string LevelName = "";
        public string DocumentTitle = "";
    }
}
