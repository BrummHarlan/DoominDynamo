using System;
using System.Collections.Generic;
using System.Globalization;

namespace DoomInDynamo.WadGen
{
    /// <summary>
    /// Turns a <see cref="BuildingModel"/> into the editing-level Doom map: every
    /// wall segment becomes a thin solid "pillar" (a closed loop of one-sided
    /// linedefs standing in a single big sector), an outer boundary box seals the
    /// map, and items/monsters are scattered by rejection sampling. Running
    /// <see cref="BspBuilder"/> afterwards makes the result playable.
    /// </summary>
    internal sealed class MapBuilder
    {
        // 16 map units per foot puts a 3 ft door at 48 units (the 32-unit-radius
        // player fits) and a 9 ft ceiling at 144 units (between Doom's usual 128
        // and 160) - buildings come out feeling like Doom levels, not dollhouses.
        private const double UnitsPerFoot = 16.0;

        // The binding size constraint is NOT the int16 coordinate range but the
        // BLOCKMAP: its offset table alone spends (span/128+1)^2 words of a lump
        // that BlockMap.FromWad reads via signed int16 word offsets (32767 words
        // total). A 10000-unit half extent keeps the offset table around 25k words
        // with headroom for the block lists; huge buildings get scaled down to it
        // instead of failing in BlockmapBuilder.
        private const double MaxHalfExtent = 10000.0;
        private const int BoundaryMargin = 192;

        private static readonly string[] WallTextures = { "STARTAN3", "STONE2", "BROWN1", "SLADWALL" };
        private const string BoundaryTexture = "BROWNGRN";
        private const string FloorFlat = "FLOOR4_8";
        private const string CeilingFlat = "CEIL3_5";

        // All in shareware Doom 1, Doom 2 and Freedoom, like every other name here.
        private const string DoorTexture = "BIGDOOR2";
        private const string DoorTrackTexture = "DOORTRAK";
        private const string SwitchTexture = "SW1COMP";
        private const string PrismTexture = "SUPPORT2";
        private const string StepTexture = "STEP2";

        // A wall this close to the ceiling reads as full-height; anything lower
        // becomes a raised-floor "low wall" you can see and shoot over.
        private const int FullHeightSlack = 8;

        // Minimum headroom to leave above a walkable raised floor (player is 56
        // units tall); furniture taller than this becomes a solid pillar instead.
        private const int Headroom = 56;

        // The door box sits inside the opening, inset from the wall's cut ends so
        // its jamb lines never coincide with the pillar end caps (coincident
        // linedefs with contradicting sector references confuse the BSP).
        private const double DoorInset = 4.0;

        // Same treatment for prisms and stair strips: furniture sits flush against
        // walls and columns sit on gridlines inside them, so un-inset outlines
        // would land exactly on wall face lines after rounding.
        private const double BoxInset = 4.0;

        // Decoration whose scaled coordinates would land outside the wall-derived
        // budget is skipped rather than allowed to wrap int16 coordinates - the
        // scale only accounts for walls, so a stray site bench half a mile away
        // must not corrupt (or dollhouse-shrink) the map.
        private const double UsableHalfExtent = MaxHalfExtent - BoundaryMargin - 64;

        private sealed class ThingDef
        {
            public readonly int Type;
            public readonly int Weight;
            public readonly double Clearance;
            public readonly bool Blocking;
            public readonly double Radius;

            // The name is documentation for the tables below; the game only sees Type.
            public ThingDef(int type, int weight, double clearance, bool blocking, double radius, string name)
            {
                Type = type;
                Weight = weight;
                Clearance = clearance;
                Blocking = blocking;
                Radius = radius;
            }
        }

        // Every type here exists in shareware Doom 1, registered Doom, Doom 2 and
        // Freedoom - no Doom-2-only things (no super shotgun, no chaingunners), so
        // the map works with whatever IWAD the player browses.
        // Radii are the engine's own (DoomInfo.MobjInfos): they matter because Doom
        // collides things per-axis (square boxes), so spawn spacing has to be
        // measured the same way - see IsFree.
        private static readonly ThingDef[] ItemPool =
        {
            new ThingDef(2014, 12, 24, false, 20, "health bonus"),
            new ThingDef(2015, 12, 24, false, 20, "armor bonus"),
            new ThingDef(2011, 10, 24, false, 20, "stimpack"),
            new ThingDef(2012, 6, 24, false, 20, "medikit"),
            new ThingDef(2018, 3, 24, false, 20, "green armor"),
            new ThingDef(2007, 10, 24, false, 20, "ammo clip"),
            new ThingDef(2008, 8, 24, false, 20, "shell pack"),
            new ThingDef(2048, 4, 24, false, 20, "ammo box"),
            new ThingDef(2049, 3, 24, false, 20, "shell box"),
            new ThingDef(2001, 3, 24, false, 20, "shotgun"),
            new ThingDef(2002, 2, 24, false, 20, "chaingun"),
            new ThingDef(8, 2, 24, false, 20, "backpack"),
            new ThingDef(2035, 6, 26, true, 10, "barrel"),
        };

        private static readonly ThingDef[] MonsterPool =
        {
            new ThingDef(3004, 10, 33, true, 20, "zombieman"),
            new ThingDef(9, 6, 33, true, 20, "shotgun guy"),
            new ThingDef(3001, 8, 33, true, 20, "imp"),
            new ThingDef(3002, 3, 46, true, 30, "demon"),
        };

        private readonly DoomMap map = new DoomMap();
        private readonly Dictionary<long, int> vertexLookup = new Dictionary<long, int>();

        // Un-rounded pillar outlines, kept for the placement tests.
        private readonly List<double[][]> pillarPolygons = new List<double[][]>();

        private int pillarMinX = int.MaxValue, pillarMinY = int.MaxValue;
        private int pillarMaxX = int.MinValue, pillarMaxY = int.MinValue;

        // The sealed play area, captured when the boundary is added (AddLoop keeps
        // growing the pillar bbox afterwards, so placement must not derive from it).
        private int boundaryLeft, boundaryBottom, boundaryRight, boundaryTop;
        private int ringAnchorX, ringAnchorY;

        // Ceiling height BEFORE the 64-unit format clamp: the full-height wall test
        // must compare against this, or tiny-scale maps (where the clamp raises the
        // ceiling above every wall) would demote the whole building to low walls.
        private int ceilingSourceUnits;

        public string Report { get; private set; } = "";

        public static DoomMap Build(BuildingModel model, int seed, int itemCount, bool includeMonsters, out string report)
        {
            var builder = new MapBuilder();
            builder.BuildCore(model, seed, itemCount, includeMonsters);
            report = builder.Report;
            return builder.map;
        }

        private void BuildCore(BuildingModel model, int seed, int itemCount, bool includeMonsters)
        {
            var walls = new List<WallSegment>();
            foreach (var wall in model.Walls)
            {
                var lengthFt = Math.Sqrt(
                    (wall.X2 - wall.X1) * (wall.X2 - wall.X1) +
                    (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
                if (lengthFt > 0.05)
                {
                    walls.Add(wall);
                }
            }

            if (walls.Count == 0)
            {
                throw new InvalidOperationException("The model contains no usable wall geometry.");
            }

            double minXFt = double.MaxValue, minYFt = double.MaxValue;
            double maxXFt = double.MinValue, maxYFt = double.MinValue;
            foreach (var wall in walls)
            {
                minXFt = Math.Min(minXFt, Math.Min(wall.X1, wall.X2));
                minYFt = Math.Min(minYFt, Math.Min(wall.Y1, wall.Y2));
                maxXFt = Math.Max(maxXFt, Math.Max(wall.X1, wall.X2));
                maxYFt = Math.Max(maxYFt, Math.Max(wall.Y1, wall.Y2));
            }

            // Ceiling from the length-weighted MEDIAN wall height, not the max: one
            // tall atrium or parapet wall must not raise the roof of the whole map.
            // Walls at/near that height stay solid full-height walls; meaningfully
            // shorter ones become see-over low walls with their real height.
            var ceilingSourceFt = WeightedMedianHeight(walls);

            var centerX = (minXFt + maxXFt) / 2;
            var centerY = (minYFt + maxYFt) / 2;
            var halfExtentFt = Math.Max(
                Math.Max(maxXFt - centerX, centerX - minXFt),
                Math.Max(maxYFt - centerY, centerY - minYFt)) + 2.0;

            var scale = Math.Min(
                UnitsPerFoot,
                (MaxHalfExtent - BoundaryMargin - 64) / halfExtentFt);

            ceilingSourceUnits = (int)Math.Round(ceilingSourceFt * scale);
            map.Sectors.Add(new MapSector
            {
                FloorHeight = 0,
                CeilingHeight = Clamp(ceilingSourceUnits, 64, 512),
                FloorFlat = FloorFlat,
                CeilingFlat = CeilingFlat,
                LightLevel = 192,
                Special = 0,
                Tag = 0
            });

            var pillarCount = 0;
            foreach (var wall in walls)
            {
                if (AddWallPillar(wall, centerX, centerY, scale, pillarCount))
                {
                    pillarCount++;
                }
            }

            if (pillarCount == 0)
            {
                throw new InvalidOperationException("All walls were too short to convert at this scale.");
            }

            var doorCount = 0;
            foreach (var door in model.Doors)
            {
                if (AddDoorBox(door, centerX, centerY, scale))
                {
                    doorCount++;
                }
            }

            var windowCount = 0;
            foreach (var window in model.Windows)
            {
                if (AddWindowBox(window, centerX, centerY, scale, windowCount))
                {
                    windowCount++;
                }
            }

            var prismCount = 0;
            foreach (var prism in model.Prisms)
            {
                if (AddPrism(prism, centerX, centerY, scale))
                {
                    prismCount++;
                }
            }

            var stairCount = 0;
            foreach (var stair in model.Stairs)
            {
                if (AddStairFlight(stair, centerX, centerY, scale))
                {
                    stairCount++;
                }
            }

            AddExitPedestal();
            AddBoundary();

            var rng = new Random(seed);
            var placed = new List<PlacedThing>();
            var playerNote = PlacePlayerStart(model, centerX, centerY, scale, rng, placed);
            var itemNote = PlaceItems(itemCount, includeMonsters, rng, placed);

            Report =
                "Exported level '" + model.LevelName + "' from '" + model.DocumentTitle + "': " +
                walls.Count + " wall segments -> " + pillarCount + " walls, " +
                doorCount + " working doors (press Space to open), " +
                windowCount + " see-through windows, " +
                prismCount + " furnishings/columns, " + stairCount + " stair flights, " +
                map.Linedefs.Count + " linedefs, ceiling " + map.Sectors[0].CeilingHeight +
                " units, scale " + scale.ToString("0.##", CultureInfo.InvariantCulture) +
                " units/ft. " + playerNote + " " + itemNote +
                " To finish the level: the exit switch stands on a pedestal just outside" +
                " the building's top-right corner (check the automap with Tab) - press Space on it.";
        }

        private bool AddWallPillar(WallSegment wall, double centerX, double centerY, double scale, int pillarIndex)
        {
            var x1 = (wall.X1 - centerX) * scale;
            var y1 = (wall.Y1 - centerY) * scale;
            var x2 = (wall.X2 - centerX) * scale;
            var y2 = (wall.Y2 - centerY) * scale;

            var dx = x2 - x1;
            var dy = y2 - y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 4.0)
            {
                return false;
            }

            dx /= length;
            dy /= length;

            // Left-hand normal; half-thickness floored at 3 units so a rounded
            // pillar can never collapse into a zero-area sliver.
            var nx = -dy;
            var ny = dx;
            var halfW = Math.Max(wall.ThicknessFt * scale / 2.0, 3.0);

            var corners = new[]
            {
                new[] { x1 + nx * halfW, y1 + ny * halfW },
                new[] { x2 + nx * halfW, y2 + ny * halfW },
                new[] { x2 - nx * halfW, y2 - ny * halfW },
                new[] { x1 - nx * halfW, y1 - ny * halfW },
            };

            // Counter-clockwise winding puts each edge's right-hand (front) side on
            // the OUTSIDE of the loop, which is where the surrounding sector is.
            if (SignedArea(corners) < 0)
            {
                Array.Reverse(corners);
            }

            var texture = WallTextures[pillarIndex % WallTextures.Length];
            var ceiling = map.Sectors[0].CeilingHeight;
            var wallTop = (int)Math.Round(wall.HeightFt * scale);

            if (wallTop >= Math.Min(ceilingSourceUnits, ceiling) - FullHeightSlack)
            {
                // At (or near) ceiling height: a plain solid wall.
                AddLoop(corners, texture);
            }
            else
            {
                // Meaningfully lower than the room: a raised-floor "low wall" - you
                // see and shoot over it at its real height, and if it's curb-height
                // (<= 24 units, Doom's step limit) you can simply walk over it.
                AddRaisedBox(corners, Clamp(wallTop, 8, ceiling - FullHeightSlack), texture);
            }

            pillarPolygons.Add(corners);
            return true;
        }

        /// <summary>
        /// A box whose interior is a new sector with a raised floor under the main
        /// ceiling: four two-sided linedefs, fronts facing the surrounding sector,
        /// the riser drawn as a lower texture (lower-unpegged so it reads floor-up).
        /// The Doom idiom for anything you can see over: low walls, furniture,
        /// stair treads.
        /// </summary>
        private int AddRaisedBox(double[][] corners, int floorHeight, string texture)
        {
            map.Sectors.Add(new MapSector
            {
                FloorHeight = floorHeight,
                CeilingHeight = map.Sectors[0].CeilingHeight,
                FloorFlat = FloorFlat,
                CeilingFlat = CeilingFlat,
                LightLevel = 192,
                Special = 0,
                Tag = 0
            });
            var sector = map.Sectors.Count - 1;

            var indices = new int[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var x = (int)Math.Round(corners[i][0]);
                var y = (int)Math.Round(corners[i][1]);
                indices[i] = GetVertex(x, y);

                pillarMinX = Math.Min(pillarMinX, x);
                pillarMinY = Math.Min(pillarMinY, y);
                pillarMaxX = Math.Max(pillarMaxX, x);
                pillarMaxY = Math.Max(pillarMaxY, y);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                var next = (i + 1) % corners.Length;
                if (indices[i] == indices[next])
                {
                    continue;
                }

                map.Sidedefs.Add(new MapSidedef { Lower = texture, Sector = 0 });
                var front = map.Sidedefs.Count - 1;
                map.Sidedefs.Add(new MapSidedef { Lower = texture, Sector = sector });
                var back = map.Sidedefs.Count - 1;

                map.Linedefs.Add(new MapLinedef
                {
                    V1 = indices[i],
                    V2 = indices[next],
                    Flags = DoomConst.LineFlagTwoSided | DoomConst.LineFlagLowerUnpegged,
                    Special = 0,
                    Tag = 0,
                    FrontSide = front,
                    BackSide = back
                });
            }

            return sector;
        }

        /// <summary>Furniture, casework or a column: solid pillar when it reaches
        /// head height, otherwise a raised-floor block you can see over (and walk
        /// onto, if it's low enough to step up).</summary>
        private bool AddPrism(Prism prism, double centerX, double centerY, double scale)
        {
            var length = Math.Sqrt(prism.DirX * prism.DirX + prism.DirY * prism.DirY);
            if (length < 1e-9)
            {
                return false;
            }

            var dx = prism.DirX / length;
            var dy = prism.DirY / length;
            var nx = -dy;
            var ny = dx;

            var cx = (prism.CX - centerX) * scale;
            var cy = (prism.CY - centerY) * scale;

            // Inset like the door boxes: furniture sits flush against walls, and an
            // un-inset edge would round onto the wall face line - the coincident-
            // linedef hazard the DoorInset comment describes. Pieces too small to
            // survive the inset weren't going to read as obstacles anyway.
            var halfLen = prism.HalfLenFt * scale - BoxInset;
            var halfW = prism.HalfWidthFt * scale - BoxInset;
            if (halfLen < 4.0 || halfW < 4.0)
            {
                return false;
            }

            // Decoration outside the wall-derived coordinate budget is skipped -
            // see UsableHalfExtent.
            if (Math.Max(Math.Abs(cx), Math.Abs(cy)) + halfLen + halfW > UsableHalfExtent)
            {
                return false;
            }

            var corners = new[]
            {
                new[] { cx - dx * halfLen + nx * halfW, cy - dy * halfLen + ny * halfW },
                new[] { cx + dx * halfLen + nx * halfW, cy + dy * halfLen + ny * halfW },
                new[] { cx + dx * halfLen - nx * halfW, cy + dy * halfLen - ny * halfW },
                new[] { cx - dx * halfLen - nx * halfW, cy - dy * halfLen - ny * halfW },
            };
            if (SignedArea(corners) < 0)
            {
                Array.Reverse(corners);
            }

            // A column fully embedded in a wall (gridline columns usually are)
            // contributes nothing as an obstacle, and its buried box would only
            // seed contradicting sector references inside the solid region.
            foreach (var polygon in pillarPolygons)
            {
                var inside = 0;
                foreach (var corner in corners)
                {
                    if (PointInPolygon(polygon, corner[0], corner[1]))
                    {
                        inside++;
                    }
                }
                if (inside == corners.Length)
                {
                    return false;
                }
            }

            var ceiling = map.Sectors[0].CeilingHeight;
            var top = (int)Math.Round(prism.HeightFt * scale);

            if (top >= ceiling - Headroom)
            {
                // No room to stand on it anyway - full solid block (columns land
                // here almost always).
                AddLoop(corners, PrismTexture);
            }
            else
            {
                AddRaisedBox(corners, Clamp(top, 8, ceiling - Headroom), PrismTexture);
            }

            pillarPolygons.Add(corners);
            return true;
        }

        /// <summary>
        /// A straight stair run as a strip of ascending raised-floor step sectors,
        /// each riser clamped under Doom's 24-unit step limit so the whole flight
        /// is climbable. Consecutive steps share one two-sided linedef (front on
        /// the lower side); the strip's perimeter borders the main sector.
        /// </summary>
        private bool AddStairFlight(StairFlight stair, double centerX, double centerY, double scale)
        {
            var x1 = (stair.X1 - centerX) * scale;
            var y1 = (stair.Y1 - centerY) * scale;
            var x2 = (stair.X2 - centerX) * scale;
            var y2 = (stair.Y2 - centerY) * scale;

            var dx = x2 - x1;
            var dy = y2 - y1;
            var rawLength = Math.Sqrt(dx * dx + dy * dy);
            if (rawLength < 32.0 + 2 * BoxInset)
            {
                return false;
            }
            dx /= rawLength;
            dy /= rawLength;

            // Inset the strip from whatever the run ends/sides touch (walls,
            // usually) for the same coincident-linedef reason as prisms and doors.
            x1 += dx * BoxInset;
            y1 += dy * BoxInset;
            x2 -= dx * BoxInset;
            y2 -= dy * BoxInset;
            var runLength = rawLength - 2 * BoxInset;
            var halfW = stair.WidthFt * scale / 2.0 - BoxInset;
            if (halfW < 12.0)
            {
                return false;
            }

            if (Math.Max(Math.Abs(x1), Math.Abs(y1)) + halfW > UsableHalfExtent ||
                Math.Max(Math.Abs(x2), Math.Abs(y2)) + halfW > UsableHalfExtent)
            {
                return false;
            }

            var nx = -dy;
            var ny = dx;

            var ceiling = map.Sectors[0].CeilingHeight;
            var stepCount = Clamp((int)(runLength / 16.0), 2, 16);

            // The headroom cap uses integer division (floors), so stepCount * riser
            // can never exceed ceiling - Headroom - rounding it up would leave the
            // top treads without the 56 units the player needs to fit. Flights that
            // can't afford a 4-unit riser aren't worth building at all.
            var riserCap = (ceiling - Headroom) / stepCount;
            if (riserCap < 4)
            {
                return false;
            }
            var riser = Clamp(
                (int)Math.Round(Math.Min(20.0, stair.RiseFt * scale / stepCount)),
                4,
                Math.Min(20, riserCap));

            // Vertex rail along both sides of the strip: cross-line k sits at
            // k/stepCount of the run.
            var left = new int[stepCount + 1];
            var right = new int[stepCount + 1];
            for (var k = 0; k <= stepCount; k++)
            {
                var px = x1 + dx * runLength * k / stepCount;
                var py = y1 + dy * runLength * k / stepCount;
                var lx = (int)Math.Round(px + nx * halfW);
                var ly = (int)Math.Round(py + ny * halfW);
                var rx = (int)Math.Round(px - nx * halfW);
                var ry = (int)Math.Round(py - ny * halfW);
                left[k] = GetVertex(lx, ly);
                right[k] = GetVertex(rx, ry);

                // Grow the play-area bbox like every other geometry adder does - an
                // entrance stair can run well past the facade, and the boundary,
                // exit pedestal and ring-anchor fallback all derive from this box.
                pillarMinX = Math.Min(pillarMinX, Math.Min(lx, rx));
                pillarMinY = Math.Min(pillarMinY, Math.Min(ly, ry));
                pillarMaxX = Math.Max(pillarMaxX, Math.Max(lx, rx));
                pillarMaxY = Math.Max(pillarMaxY, Math.Max(ly, ry));
            }

            var previousSector = 0; // the main sector is "step zero"
            for (var i = 1; i <= stepCount; i++)
            {
                map.Sectors.Add(new MapSector
                {
                    FloorHeight = i * riser,
                    CeilingHeight = ceiling,
                    FloorFlat = FloorFlat,
                    CeilingFlat = CeilingFlat,
                    LightLevel = 192,
                    Special = 0,
                    Tag = 0
                });
                var sector = map.Sectors.Count - 1;

                // Leading riser: L->R runs against the width normal, putting the
                // front side on the LOWER step behind it.
                AddStairLine(left[i - 1], right[i - 1], previousSector, sector);
                // Sides: fronts face the main sector outside the strip.
                AddStairLine(left[i], left[i - 1], 0, sector);
                AddStairLine(right[i - 1], right[i], 0, sector);

                previousSector = sector;
            }

            // Top end of the flight, front facing the main sector beyond it.
            AddStairLine(right[stepCount], left[stepCount], 0, previousSector);

            var footprint = new[]
            {
                new[] { x1 + nx * halfW, y1 + ny * halfW },
                new[] { x2 + nx * halfW, y2 + ny * halfW },
                new[] { x2 - nx * halfW, y2 - ny * halfW },
                new[] { x1 - nx * halfW, y1 - ny * halfW },
            };
            pillarPolygons.Add(footprint);
            return true;
        }

        private void AddStairLine(int v1, int v2, int frontSector, int backSector)
        {
            if (v1 == v2)
            {
                return;
            }

            map.Sidedefs.Add(new MapSidedef { Lower = StepTexture, Sector = frontSector });
            var front = map.Sidedefs.Count - 1;
            map.Sidedefs.Add(new MapSidedef { Lower = StepTexture, Sector = backSector });
            var back = map.Sidedefs.Count - 1;

            map.Linedefs.Add(new MapLinedef
            {
                V1 = v1,
                V2 = v2,
                Flags = DoomConst.LineFlagTwoSided | DoomConst.LineFlagLowerUnpegged,
                Special = 0,
                Tag = 0,
                FrontSide = front,
                BackSide = back
            });
        }

        /// <summary>
        /// The way out: a solid pedestal wearing switch textures, standing in the
        /// open ring just outside the building's north-east corner where the automap
        /// makes it easy to find. Every face carries the S1 exit special, so
        /// pressing Space on any side of it ends the level. (An invisible special
        /// on the boundary wall was tried first - technically beatable, practically
        /// unfindable.)
        /// </summary>
        private void AddExitPedestal()
        {
            var cx = pillarMaxX + 120;
            var cy = pillarMaxY + 120;
            const int half = 16;

            // CCW: fronts outward, same as every solid block.
            var corners = new[]
            {
                new[] { (double)(cx - half), (double)(cy - half) },
                new[] { (double)(cx + half), (double)(cy - half) },
                new[] { (double)(cx + half), (double)(cy + half) },
                new[] { (double)(cx - half), (double)(cy + half) },
            };

            var indices = new int[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var x = (int)Math.Round(corners[i][0]);
                var y = (int)Math.Round(corners[i][1]);
                indices[i] = GetVertex(x, y);

                pillarMinX = Math.Min(pillarMinX, x);
                pillarMinY = Math.Min(pillarMinY, y);
                pillarMaxX = Math.Max(pillarMaxX, x);
                pillarMaxY = Math.Max(pillarMaxY, y);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                var next = (i + 1) % corners.Length;
                map.Sidedefs.Add(new MapSidedef { Middle = SwitchTexture, Sector = 0 });
                map.Linedefs.Add(new MapLinedef
                {
                    V1 = indices[i],
                    V2 = indices[next],
                    Flags = DoomConst.LineFlagBlocking,
                    Special = DoomConst.SpecialS1Exit,
                    Tag = 0,
                    FrontSide = map.Sidedefs.Count - 1,
                    BackSide = -1
                });
            }

            pillarPolygons.Add(corners);
        }

        /// <summary>Wall height that at least half the total wall LENGTH reaches -
        /// the room height most of the building is actually built to.</summary>
        private static double WeightedMedianHeight(List<WallSegment> walls)
        {
            var entries = new List<double[]>(walls.Count);
            var totalLength = 0.0;
            foreach (var wall in walls)
            {
                var length = Math.Sqrt(
                    (wall.X2 - wall.X1) * (wall.X2 - wall.X1) +
                    (wall.Y2 - wall.Y1) * (wall.Y2 - wall.Y1));
                entries.Add(new[] { wall.HeightFt, length });
                totalLength += length;
            }

            entries.Sort((a, b) => a[0].CompareTo(b[0]));
            var accumulated = 0.0;
            foreach (var entry in entries)
            {
                accumulated += entry[1];
                if (accumulated >= totalLength / 2)
                {
                    return entry[0];
                }
            }

            return entries[entries.Count - 1][0];
        }

        /// <summary>Pulls a box's ends inward until its jamb cross-lines no longer
        /// cross any existing pillar outline - a door or window near a wall
        /// junction would otherwise thread its two-sided box straight through the
        /// adjacent wall's solid loop.</summary>
        private void TrimEndsClearOfPillars(double cx, double cy, double dx, double dy,
            double nx, double ny, double halfW, ref double negHalf, ref double posHalf)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var trimmed = false;
                foreach (var sign in new[] { -1.0, 1.0 })
                {
                    var half = sign < 0 ? negHalf : posHalf;
                    if (half <= 4.0)
                    {
                        continue;
                    }

                    var jx = cx + sign * dx * half;
                    var jy = cy + sign * dy * half;
                    var ax = jx + nx * halfW;
                    var ay = jy + ny * halfW;
                    var bx = jx - nx * halfW;
                    var by = jy - ny * halfW;

                    foreach (var polygon in pillarPolygons)
                    {
                        if (SegmentIntersectsPolygon(ax, ay, bx, by, polygon))
                        {
                            if (sign < 0)
                            {
                                negHalf = Math.Max(4.0, negHalf - 4.0);
                            }
                            else
                            {
                                posHalf = Math.Max(4.0, posHalf - 4.0);
                            }
                            trimmed = true;
                            break;
                        }
                    }
                }

                if (!trimmed)
                {
                    return;
                }
            }
        }

        private static bool SegmentIntersectsPolygon(double ax, double ay, double bx, double by, double[][] polygon)
        {
            if (PointInPolygon(polygon, ax, ay) || PointInPolygon(polygon, bx, by))
            {
                return true;
            }
            for (var i = 0; i < polygon.Length; i++)
            {
                var j = (i + 1) % polygon.Length;
                if (SegmentsIntersect(ax, ay, bx, by,
                        polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(
            double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy)
        {
            var d1 = Cross(cx, cy, dx, dy, ax, ay);
            var d2 = Cross(cx, cy, dx, dy, bx, by);
            var d3 = Cross(ax, ay, bx, by, cx, cy);
            var d4 = Cross(ax, ay, bx, by, dx, dy);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py)
        {
            return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        }

        /// <summary>
        /// Seals the slots between a door/window box's jambs and the wall's cut
        /// ends. The box is inset from the cut (see DoorInset), and without these
        /// the inset band is open floor-to-ceiling space that hitscan and sight
        /// pass straight through - a leak beside every window that bypasses the
        /// sill. One one-sided blocking line along each wall face bridges jamb to
        /// cut end, turning each slot into a sealed void pocket (the pillar-interior
        /// idiom); from outside they render as seamless wall. Curved walls can
        /// leave sub-unit residual gaps (chord vs arc) - cosmetic only.
        /// </summary>
        private void AddSlotSeals(double cx, double cy, double dx, double dy,
            double nx, double ny, double halfW, double negHalf, double posHalf,
            double fullHalf, string texture)
        {
            foreach (var side in new[] { 1.0, -1.0 })
            {
                foreach (var end in new[] { 1.0, -1.0 })
                {
                    var inner = end < 0 ? negHalf : posHalf;
                    if (fullHalf - inner < 1.0)
                    {
                        continue;
                    }

                    var p1X = cx + end * dx * inner + side * nx * halfW;
                    var p1Y = cy + end * dy * inner + side * ny * halfW;
                    var p2X = cx + end * dx * fullHalf + side * nx * halfW;
                    var p2Y = cy + end * dy * fullHalf + side * ny * halfW;

                    // Front (right of v1->v2) must face outward, i.e. toward
                    // side * n-hat: for side +1 the line must run against the wall
                    // direction, for side -1 along it.
                    var wantDx = side > 0 ? -dx : dx;
                    var flip = (p2X - p1X) * wantDx + (p2Y - p1Y) * (side > 0 ? -dy : dy) < 0;
                    var v1 = GetVertex((int)Math.Round(flip ? p2X : p1X), (int)Math.Round(flip ? p2Y : p1Y));
                    var v2 = GetVertex((int)Math.Round(flip ? p1X : p2X), (int)Math.Round(flip ? p1Y : p2Y));
                    if (v1 == v2)
                    {
                        continue;
                    }

                    map.Sidedefs.Add(new MapSidedef { Middle = texture, Sector = 0 });
                    map.Linedefs.Add(new MapLinedef
                    {
                        V1 = v1,
                        V2 = v2,
                        Flags = DoomConst.LineFlagBlocking,
                        Special = 0,
                        Tag = 0,
                        FrontSide = map.Sidedefs.Count - 1,
                        BackSide = -1
                    });
                }
            }
        }

        /// <summary>
        /// Stands a window in a wall opening: a sector whose floor is the sill and
        /// whose ceiling is the head, boxed by two-sided lines that all carry the
        /// BLOCKING flag - the engine's flag semantics do the rest: players and
        /// monsters can't pass a blocking two-sided line, but missiles are exempt
        /// and hitscan/sight only care about geometry, so you can see and shoot
        /// through the opening while the "glass" keeps everyone out. A window whose
        /// opening degenerates at map scale becomes a solid filler block instead
        /// (the wall was already cut, so SOMETHING must fill the hole).
        /// </summary>
        private bool AddWindowBox(WindowOpening window, double centerX, double centerY, double scale, int windowIndex)
        {
            var length = Math.Sqrt(window.DirX * window.DirX + window.DirY * window.DirY);
            if (length < 1e-9)
            {
                return false;
            }

            var dx = window.DirX / length;
            var dy = window.DirY / length;
            var nx = -dy;
            var ny = dx;

            var cx = (window.CX - centerX) * scale;
            var cy = (window.CY - centerY) * scale;
            var halfW = Math.Max(window.ThicknessFt * scale / 2.0, 3.0);
            var texture = WallTextures[windowIndex % WallTextures.Length];

            var ceiling = map.Sectors[0].CeilingHeight;
            var sill = Clamp((int)Math.Round(window.SillFt * scale), 0, ceiling - 16);
            var head = Clamp((int)Math.Round(window.HeadFt * scale), sill + 16, ceiling);

            // A window in a low (see-over) wall must not grow a bay taller than
            // its wall: the bay stays open above the sill like the rest of the
            // wall is open above its top - one sector can't express
            // open/solid/open, and never-phantom-occlude is the faithful choice.
            var wallTop = (int)Math.Round(window.HostHeightFt * scale);
            var lowWall = window.HostHeightFt > 0.0
                && wallTop < Math.Min(ceilingSourceUnits, ceiling) - FullHeightSlack;

            var fullHalf = window.WidthFt * scale / 2.0;
            var negHalf = Math.Min(Math.Max(4.0, fullHalf - DoorInset), fullHalf);
            var posHalf = negHalf;
            TrimEndsClearOfPillars(cx, cy, dx, dy, nx, ny, halfW, ref negHalf, ref posHalf);

            var seeThrough = negHalf + posHalf >= 24.0 && head - sill >= 16 && sill < ceiling - 16;

            var corners = new[]
            {
                new[] { cx - dx * negHalf + nx * halfW, cy - dy * negHalf + ny * halfW },
                new[] { cx + dx * posHalf + nx * halfW, cy + dy * posHalf + ny * halfW },
                new[] { cx + dx * posHalf - nx * halfW, cy + dy * posHalf - ny * halfW },
                new[] { cx - dx * negHalf - nx * halfW, cy - dy * negHalf - ny * halfW },
            };
            if (SignedArea(corners) < 0)
            {
                Array.Reverse(corners);
            }

            if (!seeThrough)
            {
                // No meaningful opening at this scale: fill the gap so the wall
                // reads continuous - matching the host's height, so a parapet's
                // degenerate window doesn't become a full-height tower.
                if (lowWall)
                {
                    AddRaisedBox(corners, Clamp(wallTop, 8, ceiling - FullHeightSlack), texture);
                }
                else
                {
                    AddLoop(corners, texture);
                }
                AddSlotSeals(cx, cy, dx, dy, nx, ny, halfW, negHalf, posHalf, fullHalf, texture);
                pillarPolygons.Add(corners);
                return false;
            }

            map.Sectors.Add(new MapSector
            {
                FloorHeight = sill,
                CeilingHeight = lowWall ? ceiling : head,
                FloorFlat = FloorFlat,
                CeilingFlat = CeilingFlat,
                LightLevel = 192,
                Special = 0,
                Tag = 0
            });
            var sector = map.Sectors.Count - 1;

            var indices = new int[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var x = (int)Math.Round(corners[i][0]);
                var y = (int)Math.Round(corners[i][1]);
                indices[i] = GetVertex(x, y);

                // Same bbox rule as the door boxes: a window can outlive its wall.
                pillarMinX = Math.Min(pillarMinX, x);
                pillarMinY = Math.Min(pillarMinY, y);
                pillarMaxX = Math.Max(pillarMaxX, x);
                pillarMaxY = Math.Max(pillarMaxY, y);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                var next = (i + 1) % corners.Length;
                if (indices[i] == indices[next])
                {
                    continue;
                }

                // Below-sill and above-head strips wear the wall texture; the slit
                // between them stays open. Unpegged both ways so the strips align
                // with the surrounding wall instead of the moving... nothing moves
                // here, but floor-up/ceiling-down alignment matches the walls.
                map.Sidedefs.Add(new MapSidedef { Upper = texture, Lower = texture, Sector = 0 });
                var front = map.Sidedefs.Count - 1;
                map.Sidedefs.Add(new MapSidedef { Upper = texture, Lower = texture, Sector = sector });
                var back = map.Sidedefs.Count - 1;

                map.Linedefs.Add(new MapLinedef
                {
                    V1 = indices[i],
                    V2 = indices[next],
                    Flags = DoomConst.LineFlagTwoSided | DoomConst.LineFlagBlocking
                        | DoomConst.LineFlagUpperUnpegged | DoomConst.LineFlagLowerUnpegged,
                    Special = 0,
                    Tag = 0,
                    FrontSide = front,
                    BackSide = back
                });
            }

            AddSlotSeals(cx, cy, dx, dy, nx, ny, halfW, negHalf, posHalf, fullHalf, texture);
            pillarPolygons.Add(corners);
            return true;
        }

        /// <summary>
        /// Stands a working Doom door in a wall opening: a new sector whose ceiling
        /// starts at floor height (closed) and raises to the surrounding ceiling
        /// minus 4 when used - the classic manual door. The box is a free-standing
        /// rectangle of four two-sided linedefs, fronts facing the surrounding
        /// sector: the two faces along the wall carry the door texture and the DR
        /// special (usable from both sides, and monsters can open it), the two jamb
        /// edges get the door track with the upper-unpegged flag so the track
        /// doesn't scroll while the door moves.
        /// </summary>
        private bool AddDoorBox(DoorOpening door, double centerX, double centerY, double scale)
        {
            var length = Math.Sqrt(door.DirX * door.DirX + door.DirY * door.DirY);
            if (length < 1e-9)
            {
                return false;
            }

            var dx = door.DirX / length;
            var dy = door.DirY / length;
            var nx = -dy;
            var ny = dx;

            var cx = (door.CX - centerX) * scale;
            var cy = (door.CY - centerY) * scale;

            var halfW = Math.Max(door.ThicknessFt * scale / 2.0, 3.0);
            var fullHalf = door.WidthFt * scale / 2.0;
            var negHalf = fullHalf - DoorInset;
            var posHalf = negHalf;
            if (negHalf < 12.0)
            {
                // Too narrow to work as a door at this scale - leave the opening open.
                return false;
            }

            TrimEndsClearOfPillars(cx, cy, dx, dy, nx, ny, halfW, ref negHalf, ref posHalf);
            if (negHalf + posHalf < 24.0)
            {
                return false; // a wall junction ate the doorway
            }

            var corners = new[]
            {
                new[] { cx - dx * negHalf + nx * halfW, cy - dy * negHalf + ny * halfW },
                new[] { cx + dx * posHalf + nx * halfW, cy + dy * posHalf + ny * halfW },
                new[] { cx + dx * posHalf - nx * halfW, cy + dy * posHalf - ny * halfW },
                new[] { cx - dx * negHalf - nx * halfW, cy - dy * negHalf - ny * halfW },
            };

            // Same winding rule as the pillars: CCW puts each edge's front side on
            // the OUTSIDE, toward the surrounding sector.
            if (SignedArea(corners) < 0)
            {
                Array.Reverse(corners);
            }

            map.Sectors.Add(new MapSector
            {
                FloorHeight = 0,
                CeilingHeight = 0, // closed - the engine raises it on use
                FloorFlat = FloorFlat,
                CeilingFlat = CeilingFlat,
                LightLevel = 192,
                Special = 0,
                Tag = 0
            });
            var doorSector = map.Sectors.Count - 1;

            var indices = new int[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var x = (int)Math.Round(corners[i][0]);
                var y = (int)Math.Round(corners[i][1]);
                indices[i] = GetVertex(x, y);

                // Grow the play-area bbox too: a door can outlive its wall (a short
                // wall gets dropped, its opening doesn't), and the boundary computed
                // from pillar corners alone could otherwise leave it in the void.
                pillarMinX = Math.Min(pillarMinX, x);
                pillarMinY = Math.Min(pillarMinY, y);
                pillarMaxX = Math.Max(pillarMaxX, x);
                pillarMaxY = Math.Max(pillarMaxY, y);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                var next = (i + 1) % corners.Length;
                if (indices[i] == indices[next])
                {
                    continue;
                }

                // Edges running along the wall are the door faces; the two across
                // it are the jambs/tracks.
                var edgeDx = corners[next][0] - corners[i][0];
                var edgeDy = corners[next][1] - corners[i][1];
                var isFace = Math.Abs(edgeDx * dx + edgeDy * dy) > Math.Abs(edgeDx * nx + edgeDy * ny);

                var texture = isFace ? DoorTexture : DoorTrackTexture;

                map.Sidedefs.Add(new MapSidedef { Upper = texture, Sector = 0 });
                var front = map.Sidedefs.Count - 1;
                map.Sidedefs.Add(new MapSidedef { Upper = texture, Sector = doorSector });
                var back = map.Sidedefs.Count - 1;

                map.Linedefs.Add(new MapLinedef
                {
                    V1 = indices[i],
                    V2 = indices[next],
                    Flags = isFace
                        ? DoomConst.LineFlagTwoSided
                        : DoomConst.LineFlagTwoSided | DoomConst.LineFlagUpperUnpegged,
                    Special = isFace ? DoomConst.SpecialDRDoor : 0,
                    Tag = 0,
                    FrontSide = front,
                    BackSide = back
                });
            }

            // Seal the jamb slots so a CLOSED door can't be seen or shot past
            // (same leak the windows had), then keep things (and the player start)
            // out of the doorway - anything spawned inside a closed door sector is
            // crushed into the zero-height gap and stuck.
            AddSlotSeals(cx, cy, dx, dy, nx, ny, halfW, negHalf, posHalf, fullHalf, DoorTrackTexture);
            pillarPolygons.Add(corners);
            return true;
        }

        private void AddBoundary()
        {
            var left = boundaryLeft = pillarMinX - BoundaryMargin;
            var bottom = boundaryBottom = pillarMinY - BoundaryMargin;
            var right = boundaryRight = pillarMaxX + BoundaryMargin;
            var top = boundaryTop = pillarMaxY + BoundaryMargin;

            // A guaranteed-free spot in the pillar-free ring between the building
            // and the boundary - the player-start fallback of last resort.
            ringAnchorX = pillarMinX - 96;
            ringAnchorY = pillarMinY - 96;

            // Clockwise winding: the fronts of the boundary walls face INTO the map.
            var corners = new[]
            {
                new[] { (double)left, (double)bottom },
                new[] { (double)left, (double)top },
                new[] { (double)right, (double)top },
                new[] { (double)right, (double)bottom },
            };

            AddLoop(corners, BoundaryTexture);
        }

        /// <summary>Adds a closed loop of one-sided linedefs (given corner order is
        /// trusted - CCW for solids, CW for the boundary).</summary>
        private void AddLoop(double[][] corners, string texture)
        {
            var indices = new int[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var x = (int)Math.Round(corners[i][0]);
                var y = (int)Math.Round(corners[i][1]);
                indices[i] = GetVertex(x, y);

                pillarMinX = Math.Min(pillarMinX, x);
                pillarMinY = Math.Min(pillarMinY, y);
                pillarMaxX = Math.Max(pillarMaxX, x);
                pillarMaxY = Math.Max(pillarMaxY, y);
            }

            for (var i = 0; i < corners.Length; i++)
            {
                var next = (i + 1) % corners.Length;
                if (indices[i] == indices[next])
                {
                    continue;
                }

                map.Sidedefs.Add(new MapSidedef { Middle = texture, Sector = 0 });
                map.Linedefs.Add(new MapLinedef
                {
                    V1 = indices[i],
                    V2 = indices[next],
                    Flags = DoomConst.LineFlagBlocking,
                    Special = 0,
                    Tag = 0,
                    FrontSide = map.Sidedefs.Count - 1,
                    BackSide = -1
                });
            }
        }

        private sealed class PlacedThing
        {
            public double X;
            public double Y;
            public bool Blocking;
            public double Radius;
        }

        private string PlacePlayerStart(BuildingModel model, double centerX, double centerY, double scale, Random rng, List<PlacedThing> placed)
        {
            // Prefer spawning inside an actual room (Revit room location points are
            // reliable interior points); fall back to random sampling; and if the
            // building is wall-to-wall solid, the boundary ring is pillar-free by
            // construction, so the final fallback always works.
            var candidates = new List<double[]>();
            foreach (var room in model.Rooms)
            {
                candidates.Add(new[] { (room.X - centerX) * scale, (room.Y - centerY) * scale });
            }
            Shuffle(candidates, rng);

            double[] spot = null;
            var note = "";
            foreach (var candidate in candidates)
            {
                if (IsFree(candidate[0], candidate[1], 40, true, PlayerRadius, placed))
                {
                    spot = candidate;
                    note = "Player start in a room.";
                    break;
                }
            }

            if (spot == null)
            {
                for (var attempt = 0; attempt < 3000 && spot == null; attempt++)
                {
                    var p = RandomPoint(rng, 40);
                    if (IsFree(p[0], p[1], 40, true, PlayerRadius, placed))
                    {
                        spot = p;
                        note = "Player start at a random open spot.";
                    }
                }
            }

            if (spot == null)
            {
                spot = new[] { (double)ringAnchorX, (double)ringAnchorY };
                note = "Player start on the boundary ring (no open interior spot found).";
            }

            var angle = SnapAngle(Math.Atan2(-spot[1], -spot[0]));
            map.Things.Add(new MapThingRec
            {
                X = (int)Math.Round(spot[0]),
                Y = (int)Math.Round(spot[1]),
                AngleDegrees = angle,
                Type = 1,
                Flags = DoomConst.ThingAllSkills
            });
            placed.Add(new PlacedThing { X = spot[0], Y = spot[1], Blocking = true, Radius = PlayerRadius });
            return note;
        }

        private string PlaceItems(int itemCount, bool includeMonsters, Random rng, List<PlacedThing> placed)
        {
            itemCount = Clamp(itemCount, 0, 500);

            var pool = new List<ThingDef>(ItemPool);
            if (includeMonsters)
            {
                pool.AddRange(MonsterPool);
            }

            var totalWeight = 0;
            foreach (var def in pool)
            {
                totalWeight += def.Weight;
            }

            var placedCount = 0;
            var monsters = 0;
            var attempts = 0;
            var maxAttempts = itemCount * 60;

            while (placedCount < itemCount && attempts < maxAttempts)
            {
                attempts++;

                var pick = rng.Next(totalWeight);
                ThingDef def = null;
                foreach (var candidate in pool)
                {
                    pick -= candidate.Weight;
                    if (pick < 0)
                    {
                        def = candidate;
                        break;
                    }
                }

                var p = RandomPoint(rng, def.Clearance);
                if (!IsFree(p[0], p[1], def.Clearance, def.Blocking, def.Radius, placed))
                {
                    continue;
                }

                map.Things.Add(new MapThingRec
                {
                    X = (int)Math.Round(p[0]),
                    Y = (int)Math.Round(p[1]),
                    AngleDegrees = 45 * rng.Next(8),
                    Type = def.Type,
                    Flags = DoomConst.ThingAllSkills
                });
                placed.Add(new PlacedThing { X = p[0], Y = p[1], Blocking = def.Blocking, Radius = def.Radius });
                placedCount++;

                if (Array.IndexOf(MonsterPool, def) >= 0)
                {
                    monsters++;
                }
            }

            return "Placed " + placedCount + " things (" + monsters + " monsters) with seed-driven randomness.";
        }

        private double[] RandomPoint(Random rng, double clearance)
        {
            var margin = clearance + 8;
            var left = boundaryLeft + margin;
            var right = boundaryRight - margin;
            var bottom = boundaryBottom + margin;
            var top = boundaryTop - margin;

            return new[]
            {
                left + rng.NextDouble() * (right - left),
                bottom + rng.NextDouble() * (top - bottom)
            };
        }

        // The engine's player radius (GameConst / DoomInfo): what the player-start
        // spawn must keep clear of other blocking things.
        private const double PlayerRadius = 16.0;

        private bool IsFree(double x, double y, double clearance, bool blocking, double radius, List<PlacedThing> placed)
        {
            if (x < boundaryLeft + clearance + 8 ||
                x > boundaryRight - clearance - 8 ||
                y < boundaryBottom + clearance + 8 ||
                y > boundaryTop - clearance - 8)
            {
                return false;
            }

            foreach (var polygon in pillarPolygons)
            {
                if (PointInPolygon(polygon, x, y))
                {
                    return false;
                }
                if (DistanceToPolygon(polygon, x, y) < clearance)
                {
                    return false;
                }
            }

            foreach (var other in placed)
            {
                var dx = other.X - x;
                var dy = other.Y - y;
                if (blocking && other.Blocking)
                {
                    // Doom collision is per-axis (ThingMovement.CheckThing blocks
                    // when BOTH |dx| and |dy| are under the radii sum), so spacing
                    // must be Chebyshev, not Euclidean - a demon pair 51 units
                    // apart diagonally would otherwise spawn interlocked and stand
                    // frozen for the whole game. +4 keeps boxes from being flush.
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < radius + other.Radius + 4)
                    {
                        return false;
                    }
                }
                else if (dx * dx + dy * dy < 24.0 * 24.0)
                {
                    return false;
                }
            }

            return true;
        }

        private int GetVertex(int x, int y)
        {
            var key = ((long)x << 32) ^ (uint)y;
            int index;
            if (vertexLookup.TryGetValue(key, out index))
            {
                return index;
            }

            map.Vertices.Add(new MapVertex(x, y));
            index = map.Vertices.Count - 1;
            vertexLookup[key] = index;
            return index;
        }

        private static double SignedArea(double[][] polygon)
        {
            var sum = 0.0;
            for (var i = 0; i < polygon.Length; i++)
            {
                var j = (i + 1) % polygon.Length;
                sum += polygon[i][0] * polygon[j][1] - polygon[j][0] * polygon[i][1];
            }
            return sum / 2;
        }

        private static bool PointInPolygon(double[][] polygon, double x, double y)
        {
            var inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i][1] > y) != (polygon[j][1] > y) &&
                    x < (polygon[j][0] - polygon[i][0]) * (y - polygon[i][1]) /
                        (polygon[j][1] - polygon[i][1]) + polygon[i][0])
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToPolygon(double[][] polygon, double x, double y)
        {
            var best = double.MaxValue;
            for (var i = 0; i < polygon.Length; i++)
            {
                var j = (i + 1) % polygon.Length;
                best = Math.Min(best, DistanceToSegment(
                    x, y, polygon[i][0], polygon[i][1], polygon[j][0], polygon[j][1]));
            }
            return best;
        }

        private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared < 1e-12
                ? 0
                : Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSquared));
            var cx = x1 + t * dx;
            var cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        private static int SnapAngle(double radians)
        {
            var degrees = radians * 180.0 / Math.PI;
            var snapped = (int)Math.Round(degrees / 45.0) * 45;
            return ((snapped % 360) + 360) % 360;
        }

        private static void Shuffle<T>(List<T> list, Random rng)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
