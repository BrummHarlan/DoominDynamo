using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitServices.Persistence;

namespace DoomInDynamo.Revit
{
    /// <summary>
    /// The only file in this assembly that touches Autodesk.Revit.DB / RevitServices.
    /// Its one entry point deliberately takes and returns Revit-free types
    /// (see BuildingModel) so the assembly can be imported by Dynamo Sandbox, where
    /// the Revit DLLs don't exist - the CLR only tries to load them when this method
    /// actually runs, which only happens inside Dynamo for Revit.
    /// </summary>
    internal static class RevitExtractor
    {
        /// <summary>Sub-segments shorter than this (feet) are noise from tessellation
        /// or door cuts landing near a vertex - too small to matter at Doom scale.</summary>
        private const double MinSegmentFt = 0.25;

        /// <summary>Extra clearance added to a door's width when cutting its opening,
        /// so the gap survives the later snap to integer Doom map units.</summary>
        private const double DoorGapExtraFt = 0.5;

        private const double FallbackThicknessFt = 0.5;
        private const double FallbackHeightFt = 10.0;
        private const double FallbackDoorWidthFt = 3.0;
        private const double FallbackWindowWidthFt = 3.0;
        private const double FallbackSillFt = 3.0;
        private const double FallbackWindowHeightFt = 4.0;

        /// <summary>
        /// Reads walls, hosted doors, rooms, furniture, columns and stairs from the
        /// active Revit document and
        /// flattens them into the pure-data <see cref="WadGen.BuildingModel"/>.
        /// Door openings are cut out of the wall centerlines here, so WadGen never
        /// has to know that doors exist. Read-only: no transactions.
        /// </summary>
        /// <param name="levelName">Base level to export (case-insensitive), or empty
        /// to auto-pick the level that hosts the most walls.</param>
        internal static WadGen.BuildingModel ExtractCurrentDocument(string levelName)
        {
            Document doc = DocumentManager.Instance != null
                ? DocumentManager.Instance.CurrentDBDocument
                : null;
            if (doc == null)
            {
                throw new InvalidOperationException(
                    "No active Revit document - this node needs to run inside Dynamo for Revit with a project open.");
            }

            // Only walls driven by a location curve can become map geometry; walls
            // without one (some in-place/stacked oddities) have nothing to trace.
            // Design options: a bare collector returns elements from EVERY option,
            // primary and secondary alike - keep only what the user actually sees
            // (main model + primary options), or mutually-exclusive layouts would
            // export on top of each other.
            List<Wall> allWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .OfType<Wall>()
                .Where(w => w.Location is LocationCurve && IsMainModelOrPrimary(w))
                .ToList();

            Dictionary<string, LevelBucket> buckets = BucketByBaseLevel(doc, allWalls);

            string requested = (levelName ?? "").Trim();
            var model = new WadGen.BuildingModel();
            model.DocumentTitle = string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title;

            // "*" (or "all") = the WHOLE model: Doom has no room-over-room, so the
            // storeys can't stack - instead every level with walls is exported as
            // its own cluster, laid out west-to-east with walkable gaps between
            // them. One map, the entire building, exploded-axon style.
            if (requested == "*" || string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
            {
                List<KeyValuePair<string, LevelBucket>> levels = buckets
                    .Where(kvp => kvp.Key.Length > 0 && kvp.Value.Walls.Count > 0)
                    .OrderBy(kvp => kvp.Value.Elevation)
                    .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (levels.Count > 0)
                {
                    double offsetX = 0.0;
                    var exported = new List<string>();
                    foreach (KeyValuePair<string, LevelBucket> kvp in levels)
                    {
                        var pieces = new WadGen.BuildingModel();
                        ExtractLevel(doc, kvp.Key, kvp.Value, pieces);
                        if (pieces.Walls.Count == 0)
                        {
                            continue;
                        }

                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue;
                        foreach (WadGen.WallSegment wallSeg in pieces.Walls)
                        {
                            minX = Math.Min(minX, Math.Min(wallSeg.X1, wallSeg.X2));
                            minY = Math.Min(minY, Math.Min(wallSeg.Y1, wallSeg.Y2));
                            maxX = Math.Max(maxX, Math.Max(wallSeg.X1, wallSeg.X2));
                        }

                        OffsetModel(pieces, offsetX - minX, -minY);
                        MergeInto(model, pieces);
                        exported.Add(kvp.Key);
                        offsetX += (maxX - minX) + ClusterGapFt;
                    }

                    if (model.Walls.Count == 0)
                    {
                        throw new InvalidOperationException("No walls found on any level - nothing to convert.");
                    }

                    model.LevelName = "whole model: " + string.Join(" | ", exported);
                    return model;
                }

                // No named levels at all - degrade to the single-level auto-pick.
                requested = "";
            }

            string chosenName;
            List<Wall> keptWalls;
            SelectLevel(buckets, requested, out chosenName, out keptWalls);

            LevelBucket chosenBucket;
            if (!buckets.TryGetValue(chosenName, out chosenBucket))
            {
                chosenBucket = new LevelBucket();
            }

            ExtractLevel(doc, chosenName, chosenBucket, model);

            if (model.Walls.Count == 0)
            {
                throw new InvalidOperationException(
                    "No walls found on level '" + chosenName + "' - nothing to convert.");
            }

            model.LevelName = chosenName;
            return model;
        }

        /// <summary>How far apart whole-model clusters sit - generous enough to
        /// walk between storeys but not a hike (30 ft = 480 map units).</summary>
        private const double ClusterGapFt = 30.0;

        /// <summary>Extracts one level's walls (with door/window cuts), doors,
        /// windows, rooms, furnishings and stairs into the given model, in world
        /// coordinates - the caller offsets and merges for whole-model exports.</summary>
        private static void ExtractLevel(Document doc, string levelName, LevelBucket bucket, WadGen.BuildingModel model)
        {
            // The level's elevation (when any wall resolved one) anchors two things:
            // telling floor-standing furniture from wall-hung pieces, and placing
            // level-less curtain-wall door panels on the right storey.
            double levelElevationFt = bucket.Elevation;

            Dictionary<long, List<FamilyInstance>> doorsByHost = CollectDoorsByHostWall(doc, levelName, levelElevationFt);
            Dictionary<long, List<FamilyInstance>> windowsByHost = CollectWindowsByHostWall(doc, levelName, levelElevationFt);

            foreach (Wall wall in bucket.Walls)
            {
                ExtractWall(wall, doorsByHost, windowsByHost, levelElevationFt, model.Walls, model.Doors, model.Windows);
            }

            CollectRoomPoints(doc, levelName, model.Rooms);
            CollectPrisms(doc, levelName, levelElevationFt, model.Prisms);
            CollectStairs(doc, levelName, model.Stairs);
        }

        private static void OffsetModel(WadGen.BuildingModel model, double dx, double dy)
        {
            foreach (WadGen.WallSegment wall in model.Walls)
            {
                wall.X1 += dx; wall.Y1 += dy;
                wall.X2 += dx; wall.Y2 += dy;
            }
            foreach (WadGen.DoorOpening door in model.Doors)
            {
                door.CX += dx; door.CY += dy;
            }
            foreach (WadGen.WindowOpening window in model.Windows)
            {
                window.CX += dx; window.CY += dy;
            }
            foreach (WadGen.Prism prism in model.Prisms)
            {
                prism.CX += dx; prism.CY += dy;
            }
            foreach (WadGen.StairFlight stair in model.Stairs)
            {
                stair.X1 += dx; stair.Y1 += dy;
                stair.X2 += dx; stair.Y2 += dy;
            }
            foreach (WadGen.RoomPoint room in model.Rooms)
            {
                room.X += dx; room.Y += dy;
            }
        }

        private static void MergeInto(WadGen.BuildingModel target, WadGen.BuildingModel pieces)
        {
            target.Walls.AddRange(pieces.Walls);
            target.Doors.AddRange(pieces.Doors);
            target.Windows.AddRange(pieces.Windows);
            target.Prisms.AddRange(pieces.Prisms);
            target.Stairs.AddRange(pieces.Stairs);
            target.Rooms.AddRange(pieces.Rooms);
        }

        /// <summary>Walls grouped under one base-level name. Elevation is the lowest
        /// elevation seen for that name (levels can share names across phases/links)
        /// and is only used as the tie-breaker when auto-picking a level.</summary>
        private sealed class LevelBucket
        {
            public readonly List<Wall> Walls = new List<Wall>();
            public double Elevation = double.PositiveInfinity;
        }

        private static Dictionary<string, LevelBucket> BucketByBaseLevel(Document doc, List<Wall> walls)
        {
            // Case-insensitive so a user-typed level name doesn't have to match
            // Revit's capitalization exactly.
            var buckets = new Dictionary<string, LevelBucket>(StringComparer.OrdinalIgnoreCase);
            foreach (Wall wall in walls)
            {
                // WALL_BASE_CONSTRAINT is the "Base Constraint" parameter; walls
                // whose level can't be resolved (deleted level, exotic wall kinds)
                // land in the "" bucket rather than being dropped outright.
                string name = "";
                double elevation = double.PositiveInfinity;
                Parameter baseParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                if (baseParam != null)
                {
                    ElementId levelId = baseParam.AsElementId();
                    if (levelId != null && levelId != ElementId.InvalidElementId)
                    {
                        Level level = doc.GetElement(levelId) as Level;
                        if (level != null)
                        {
                            name = level.Name ?? "";
                            elevation = level.Elevation;
                        }
                    }
                }

                LevelBucket bucket;
                if (!buckets.TryGetValue(name, out bucket))
                {
                    bucket = new LevelBucket();
                    buckets[name] = bucket;
                }
                bucket.Walls.Add(wall);
                if (elevation < bucket.Elevation)
                {
                    bucket.Elevation = elevation;
                }
            }
            return buckets;
        }

        private static void SelectLevel(Dictionary<string, LevelBucket> buckets, string requested,
            out string chosenName, out List<Wall> keptWalls)
        {
            if (requested.Length > 0)
            {
                // Explicit level: report the level's own spelling back, not the
                // user's, so downstream messages match what Revit shows.
                foreach (KeyValuePair<string, LevelBucket> kvp in buckets)
                {
                    if (kvp.Key.Length > 0 && string.Equals(kvp.Key, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenName = kvp.Key;
                        keptWalls = kvp.Value.Walls;
                        return;
                    }
                }

                List<string> available = buckets.Keys
                    .Where(k => k.Length > 0)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                throw new InvalidOperationException(available.Count > 0
                    ? "No walls found on level '" + requested + "'. Levels that do have walls: "
                        + string.Join(", ", available) + "."
                    : "No walls found on level '" + requested
                        + "' - no wall in the document has a resolvable base level.");
            }

            // Auto-pick: most walls wins; ties go to the lowest level so a model
            // with equal floors exports its ground floor. The "" (unresolved-level)
            // bucket is a last resort only - it's usually junk walls.
            chosenName = null;
            LevelBucket chosen = null;
            foreach (KeyValuePair<string, LevelBucket> kvp in buckets)
            {
                if (kvp.Key.Length == 0)
                {
                    continue;
                }
                if (chosen == null
                    || kvp.Value.Walls.Count > chosen.Walls.Count
                    || (kvp.Value.Walls.Count == chosen.Walls.Count
                        && (kvp.Value.Elevation < chosen.Elevation
                            || (kvp.Value.Elevation == chosen.Elevation
                                && string.CompareOrdinal(kvp.Key, chosenName) < 0))))
                {
                    chosenName = kvp.Key;
                    chosen = kvp.Value;
                }
            }

            LevelBucket unleveled;
            if (chosen == null && buckets.TryGetValue("", out unleveled))
            {
                chosenName = "";
                chosen = unleveled;
            }

            keptWalls = chosen != null ? chosen.Walls : new List<Wall>();
            if (chosenName == null)
            {
                chosenName = "";
            }
        }

        private static Dictionary<long, List<FamilyInstance>> CollectDoorsByHostWall(
            Document doc, string chosenLevelName, double levelElevationFt)
        {
            // ElementId.Value (long) as the key - IntegerValue is deprecated in
            // current Revit versions.
            var doorsByHost = new Dictionary<long, List<FamilyInstance>>();
            IList<Element> doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .ToElements();
            foreach (Element element in doors)
            {
                FamilyInstance door = element as FamilyInstance;
                if (door == null || door.Host == null || !IsMainModelOrPrimary(door))
                {
                    continue;
                }

                // A multi-storey wall based on the exported level can host doors on
                // OTHER storeys too; cutting those would punch walkable holes where
                // this storey is solid - so only doors on the exported level cut
                // gaps. Wrinkle: CURTAIN WALL doors are panels, and panels usually
                // resolve no level at all (no LevelId, no level parameters), so a
                // level-less door is judged by elevation instead: its base has to
                // sit at this storey's floor.
                if (!OpeningBelongsToLevel(doc, door, chosenLevelName, levelElevationFt))
                {
                    continue;
                }

                long hostId = door.Host.Id.Value;
                List<FamilyInstance> list;
                if (!doorsByHost.TryGetValue(hostId, out list))
                {
                    list = new List<FamilyInstance>();
                    doorsByHost[hostId] = list;
                }
                list.Add(door);
            }
            return doorsByHost;
        }

        /// <summary>Level test shared by hosted doors and windows: a resolvable
        /// level must match the exported one by name; a level-less opening (curtain
        /// wall door/window panels are the usual case) is accepted when its base
        /// elevation sits at this storey's floor - or unconditionally when either
        /// the "" bucket is being exported or no elevation is known to test against.</summary>
        private static bool OpeningBelongsToLevel(Document doc, FamilyInstance opening,
            string chosenLevelName, double levelElevationFt)
        {
            string openingLevelName = ResolveInstanceLevelName(doc, opening);
            if (openingLevelName.Length > 0)
            {
                return string.Equals(openingLevelName, chosenLevelName, StringComparison.OrdinalIgnoreCase);
            }

            if (chosenLevelName.Length == 0 || double.IsPositiveInfinity(levelElevationFt))
            {
                return true;
            }

            BoundingBoxXYZ box = opening.get_BoundingBox(null);
            return box != null
                && box.Min.Z > levelElevationFt - 3.0
                && box.Min.Z < levelElevationFt + 6.0;
        }

        /// <summary>Same bucketing as the doors - hosted windows on the exported
        /// level, keyed by host wall id - so ExtractWall can cut their openings.</summary>
        private static Dictionary<long, List<FamilyInstance>> CollectWindowsByHostWall(
            Document doc, string chosenLevelName, double levelElevationFt)
        {
            var windowsByHost = new Dictionary<long, List<FamilyInstance>>();
            IList<Element> windows = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .ToElements();
            foreach (Element element in windows)
            {
                FamilyInstance window = element as FamilyInstance;
                if (window == null || window.Host == null || !IsMainModelOrPrimary(window))
                {
                    continue;
                }

                if (!OpeningBelongsToLevel(doc, window, chosenLevelName, levelElevationFt))
                {
                    continue;
                }

                long hostId = window.Host.Id.Value;
                List<FamilyInstance> list;
                if (!windowsByHost.TryGetValue(hostId, out list))
                {
                    list = new List<FamilyInstance>();
                    windowsByHost[hostId] = list;
                }
                list.Add(window);
            }
            return windowsByHost;
        }

        private static void ExtractWall(Wall wall, Dictionary<long, List<FamilyInstance>> doorsByHost,
            Dictionary<long, List<FamilyInstance>> windowsByHost, double levelElevationFt,
            List<WadGen.WallSegment> output, List<WadGen.DoorOpening> doorOutput,
            List<WadGen.WindowOpening> windowOutput)
        {
            Curve curve = ((LocationCurve)wall.Location).Curve;
            if (curve == null)
            {
                return;
            }

            // Tessellate covers straight and curved walls alike: a Line yields just
            // its two endpoints, an Arc a fan of short chords. Everything downstream
            // works on the flat X/Y polyline (plan projection).
            IList<XYZ> raw;
            try
            {
                raw = curve.Tessellate();
            }
            catch
            {
                return; // degenerate/unbound curve - nothing worth exporting
            }
            if (raw == null || raw.Count < 2)
            {
                return;
            }

            var pts = new List<(double X, double Y)>(raw.Count);
            foreach (XYZ p in raw)
            {
                pts.Add((p.X, p.Y));
            }
            double[] stations = CumulativeStations(pts);
            double totalLength = stations[stations.Length - 1];

            // Wall.Width throws for wall kinds with no compound structure (curtain
            // walls being the classic case), so it gets the defensive treatment.
            double thicknessFt;
            try
            {
                thicknessFt = wall.Width;
            }
            catch
            {
                thicknessFt = FallbackThicknessFt;
            }
            if (thicknessFt <= 0.0)
            {
                thicknessFt = FallbackThicknessFt;
            }

            // WALL_USER_HEIGHT_PARAM is "Unconnected Height", which goes stale the
            // moment a wall's top is constrained or attached to an upper level - it
            // keeps whatever value the wall had before. The bounding box measures
            // the geometry actually built, so it wins whenever it exists and looks
            // sane; the parameter, then a plain 10 ft, are the fallbacks.
            double heightFt = 0.0;
            BoundingBoxXYZ wallBox = wall.get_BoundingBox(null);
            if (wallBox != null)
            {
                double h = wallBox.Max.Z - wallBox.Min.Z;
                if (h > 1.0)
                {
                    heightFt = h;
                }
            }
            if (heightFt <= 0.0)
            {
                Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null)
                {
                    double h = heightParam.AsDouble();
                    if (h > 1.0)
                    {
                        heightFt = h;
                    }
                }
            }
            if (heightFt <= 0.0)
            {
                heightFt = FallbackHeightFt;
            }

            // Every hosted door becomes a cut interval along the centerline's arc
            // length; the complement of the merged intervals is what remains solid.
            var cuts = new List<(double Start, double End)>();
            List<FamilyInstance> hostedDoors;
            if (doorsByHost.TryGetValue(wall.Id.Value, out hostedDoors))
            {
                foreach (FamilyInstance door in hostedDoors)
                {
                    XYZ doorPoint = OpeningPoint(door);
                    if (doorPoint == null)
                    {
                        continue;
                    }

                    double station = StationOfClosestPoint(pts, stations, doorPoint.X, doorPoint.Y);
                    double halfGap = (GetDoorWidthFt(door) + DoorGapExtraFt) / 2.0;
                    cuts.Add((station - halfGap, station + halfGap));
                }
            }

            List<(double Start, double End)> merged = MergeCuts(cuts, totalLength);

            // Windows cut the wall the same way, but a doorway takes precedence
            // when a window overlaps one (a curtain-wall door often carries glazing
            // right up against it - the walkable opening wins). Curtain walls also
            // contribute their glazed panels as synthesized window cuts, so
            // storefronts read as glass instead of solid wall.
            List<(double Start, double End, double Sill, double Head)> panelCuts =
                CollectCurtainPanelCuts(wall, pts, stations, levelElevationFt, wallBox);
            List<(double Start, double End, double Sill, double Head)> windowCuts =
                CollectWindowCuts(wall, windowsByHost, pts, stations, totalLength, merged, panelCuts);

            var allCuts = new List<(double Start, double End)>(merged);
            foreach ((double Start, double End, double Sill, double Head) cut in windowCuts)
            {
                allCuts.Add((cut.Start, cut.End));
            }
            allCuts.Sort((a, b) => a.Start.CompareTo(b.Start));

            foreach ((double Start, double End) keep in KeepIntervals(allCuts, totalLength))
            {
                EmitRun(pts, stations, keep.Start, keep.End, thicknessFt, heightFt, output);
            }

            // Each merged opening also becomes a working in-game door standing in
            // the gap: report where it sits, which way the wall runs there, and how
            // wide the hole is - WadGen builds the door sector from that.
            foreach ((double Start, double End) cut in merged)
            {
                double centerStation = (cut.Start + cut.End) / 2.0;
                (double X, double Y) center = PointAtStation(pts, stations, centerStation);
                (double X, double Y) dir = DirectionAtStation(pts, stations, centerStation);
                if (dir.X == 0.0 && dir.Y == 0.0)
                {
                    continue;
                }

                doorOutput.Add(new WadGen.DoorOpening
                {
                    CX = center.X,
                    CY = center.Y,
                    DirX = dir.X,
                    DirY = dir.Y,
                    WidthFt = cut.End - cut.Start,
                    ThicknessFt = thicknessFt,
                });
            }

            foreach ((double Start, double End, double Sill, double Head) cut in windowCuts)
            {
                double centerStation = (cut.Start + cut.End) / 2.0;
                (double X, double Y) center = PointAtStation(pts, stations, centerStation);
                (double X, double Y) dir = DirectionAtStation(pts, stations, centerStation);
                if (dir.X == 0.0 && dir.Y == 0.0)
                {
                    continue;
                }

                windowOutput.Add(new WadGen.WindowOpening
                {
                    CX = center.X,
                    CY = center.Y,
                    DirX = dir.X,
                    DirY = dir.Y,
                    WidthFt = cut.End - cut.Start,
                    ThicknessFt = thicknessFt,
                    SillFt = cut.Sill,
                    HeadFt = cut.Head,
                    HostHeightFt = heightFt,
                });
            }
        }

        /// <summary>
        /// Hosted windows as merged cut intervals with their sill/head heights
        /// (heights relative to the wall's level, which is what the map's floor 0
        /// means). Windows overlapping a doorway are dropped; overlapping windows
        /// (ribbon glazing modeled as separate units) merge into one opening
        /// spanning the lowest sill to the highest head.
        /// </summary>
        private static List<(double Start, double End, double Sill, double Head)> CollectWindowCuts(
            Wall wall, Dictionary<long, List<FamilyInstance>> windowsByHost,
            List<(double X, double Y)> pts, double[] stations, double totalLength,
            List<(double Start, double End)> doorCuts,
            List<(double Start, double End, double Sill, double Head)> extraCuts)
        {
            var result = new List<(double Start, double End, double Sill, double Head)>();

            var raw = new List<(double Start, double End, double Sill, double Head)>();
            List<FamilyInstance> hostedWindows;
            if (windowsByHost.TryGetValue(wall.Id.Value, out hostedWindows))
            {
                foreach (FamilyInstance window in hostedWindows)
                {
                    XYZ point = OpeningPoint(window);
                    if (point == null)
                    {
                        continue;
                    }

                    double station = StationOfClosestPoint(pts, stations, point.X, point.Y);
                    double halfGap = (GetWindowWidthFt(window) + DoorGapExtraFt) / 2.0;
                    double sill = GetWindowSillFt(window);
                    raw.Add((station - halfGap, station + halfGap, sill, GetWindowHeadFt(window, sill)));
                }
            }
            raw.AddRange(extraCuts);

            // Clamp to the wall, drop anything under a doorway (curtain glazing
            // included - the walkable opening wins), sort, then merge.
            var filtered = new List<(double Start, double End, double Sill, double Head)>();
            foreach ((double Start, double End, double Sill, double Head) cut in raw)
            {
                double start = Math.Max(0.0, cut.Start);
                double end = Math.Min(totalLength, cut.End);
                if (end <= start)
                {
                    continue;
                }

                var overlapsDoor = false;
                foreach ((double Start, double End) door in doorCuts)
                {
                    if (start < door.End && end > door.Start)
                    {
                        overlapsDoor = true;
                        break;
                    }
                }
                if (!overlapsDoor)
                {
                    filtered.Add((start, end, cut.Sill, cut.Head));
                }
            }

            filtered.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach ((double Start, double End, double Sill, double Head) cut in filtered)
            {
                if (result.Count > 0 && cut.Start <= result[result.Count - 1].End)
                {
                    (double Start, double End, double Sill, double Head) last = result[result.Count - 1];
                    if (cut.Sill <= last.Head && cut.Head >= last.Sill)
                    {
                        // Vertically overlapping (ribbon glazing modeled as units):
                        // union into one band.
                        result[result.Count - 1] = (
                            last.Start,
                            Math.Max(last.End, cut.End),
                            Math.Min(last.Sill, cut.Sill),
                            Math.Max(last.Head, cut.Head));
                    }
                    else
                    {
                        // Vertically DISJOINT (a clerestory above a normal window):
                        // unioning would make the solid spandrel between them
                        // transparent. One sector can't show both bands, so keep
                        // the taller one - the lesser opening is the sacrifice.
                        var cutTaller = cut.Head - cut.Sill > last.Head - last.Sill;
                        result[result.Count - 1] = (
                            last.Start,
                            Math.Max(last.End, cut.End),
                            cutTaller ? cut.Sill : last.Sill,
                            cutTaller ? cut.Head : last.Head);
                    }
                }
                else
                {
                    result.Add(cut);
                }
            }

            return result;
        }

        /// <summary>
        /// A curtain wall's glazed panels, synthesized into window cuts so
        /// storefronts export as see-through glass rather than solid wall. Panels
        /// that are really doors or windows are skipped (the hosted collectors
        /// already handle them); spandrel/solid panels are NOT distinguishable
        /// reliably, so all remaining panels count as glass - an approximation.
        /// Sill/head are measured from the level (or the wall's own base when no
        /// level elevation is known); panels starting more than a storey up (a
        /// multi-storey curtain wall's upper bands) are skipped.
        /// </summary>
        private static List<(double Start, double End, double Sill, double Head)> CollectCurtainPanelCuts(
            Wall wall, List<(double X, double Y)> pts, double[] stations,
            double levelElevationFt, BoundingBoxXYZ wallBox)
        {
            var cuts = new List<(double Start, double End, double Sill, double Head)>();
            try
            {
                CurtainGrid grid = wall.CurtainGrid;
                if (grid == null)
                {
                    return cuts;
                }

                double datum = double.IsPositiveInfinity(levelElevationFt)
                    ? (wallBox != null ? wallBox.Min.Z : 0.0)
                    : levelElevationFt;

                foreach (ElementId panelId in grid.GetPanelIds())
                {
                    Element panel = wall.Document.GetElement(panelId);
                    if (panel == null || !IsMainModelOrPrimary(panel))
                    {
                        continue;
                    }

                    Category category = panel.Category;
                    if (category != null)
                    {
                        long categoryId = category.Id.Value;
                        if (categoryId == (long)BuiltInCategory.OST_Doors ||
                            categoryId == (long)BuiltInCategory.OST_Windows)
                        {
                            continue; // handled by the hosted door/window paths
                        }
                    }

                    BoundingBoxXYZ box = panel.get_BoundingBox(null);
                    if (box == null)
                    {
                        continue;
                    }

                    double sill = box.Min.Z - datum;
                    double head = box.Max.Z - datum;
                    if (head - sill < 0.5 || sill > 12.0)
                    {
                        continue;
                    }
                    if (sill < 0.0)
                    {
                        sill = 0.0;
                    }

                    double station = StationOfClosestPoint(pts, stations,
                        (box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0);
                    double width = PositiveDoubleOrZero(panel.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH));
                    if (width <= 0.0)
                    {
                        width = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
                    }
                    if (width < 0.5)
                    {
                        continue;
                    }

                    // No extra margin: panels tile edge to edge, and the merge
                    // welds adjacent glass into one ribbon opening.
                    cuts.Add((station - width / 2.0, station + width / 2.0, sill, head));
                }
            }
            catch
            {
                cuts.Clear();
            }
            return cuts;
        }

        private static double GetWindowWidthFt(FamilyInstance window)
        {
            FamilySymbol symbol = window.Symbol;
            double width = PositiveDoubleOrZero(symbol != null
                ? symbol.get_Parameter(BuiltInParameter.WINDOW_WIDTH) : null);
            if (width <= 0.0)
            {
                width = PositiveDoubleOrZero(window.get_Parameter(BuiltInParameter.WINDOW_WIDTH));
            }
            if (width <= 0.0)
            {
                width = PositiveDoubleOrZero(symbol != null
                    ? symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) : null);
            }
            if (width <= 0.0)
            {
                width = PositiveDoubleOrZero(window.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH));
            }
            if (width <= 0.0)
            {
                width = BoxPlanWidthFt(window, 1.0, 10.0);
            }
            return width > 0.0 ? width : FallbackWindowWidthFt;
        }

        private static double GetWindowSillFt(FamilyInstance window)
        {
            // "Sill Height" is an instance parameter measured from the level - the
            // same datum the exported map's floor 0 uses. Zero is a legitimate
            // value (floor-to-ceiling glazing), so only an ABSENT parameter falls
            // back - don't confuse "0" with "not set" like the width helpers may.
            Parameter parameter = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (parameter != null && parameter.HasValue)
            {
                return Math.Max(0.0, parameter.AsDouble());
            }
            return FallbackSillFt;
        }

        private static double GetWindowHeadFt(FamilyInstance window, double sillFt)
        {
            // "Head Height" when present; otherwise sill + the family's height.
            double head = PositiveDoubleOrZero(window.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM));
            if (head > sillFt)
            {
                return head;
            }

            FamilySymbol symbol = window.Symbol;
            double height = PositiveDoubleOrZero(symbol != null
                ? symbol.get_Parameter(BuiltInParameter.WINDOW_HEIGHT) : null);
            if (height <= 0.0)
            {
                height = PositiveDoubleOrZero(symbol != null
                    ? symbol.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM) : null);
            }
            return sillFt + (height > 0.0 ? height : FallbackWindowHeightFt);
        }

        /// <summary>Unit direction of the polyline segment containing the station
        /// (zero vector if the polyline is degenerate there).</summary>
        private static (double X, double Y) DirectionAtStation(List<(double X, double Y)> pts,
            double[] stations, double station)
        {
            int last = pts.Count - 1;
            int segment = last - 1;
            for (int i = 0; i < last; i++)
            {
                if (station <= stations[i + 1])
                {
                    segment = i;
                    break;
                }
            }

            double dx = pts[segment + 1].X - pts[segment].X;
            double dy = pts[segment + 1].Y - pts[segment].Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            return length > 1e-9 ? (dx / length, dy / length) : (0.0, 0.0);
        }

        /// <summary>Where a hosted opening sits in plan. Regular doors/windows are
        /// point-hosted; CURTAIN WALL doors/windows are panels with no
        /// LocationPoint at all, so their bounding-box center stands in - it lies
        /// on the panel, which lies on the wall, which is all the centerline
        /// projection needs.</summary>
        private static XYZ OpeningPoint(FamilyInstance opening)
        {
            LocationPoint location = opening.Location as LocationPoint;
            if (location != null && location.Point != null)
            {
                return location.Point;
            }

            BoundingBoxXYZ box = opening.get_BoundingBox(null);
            return box != null ? (box.Min + box.Max) / 2.0 : null;
        }

        /// <summary>Plan footprint's longer side from the bounding box - the last
        /// resort for panel widths when no width parameter answers.</summary>
        private static double BoxPlanWidthFt(FamilyInstance opening, double minFt, double maxFt)
        {
            BoundingBoxXYZ box = opening.get_BoundingBox(null);
            if (box == null)
            {
                return 0.0;
            }
            double extent = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
            return extent < minFt ? 0.0 : Math.Min(extent, maxFt);
        }

        private static double GetDoorWidthFt(FamilyInstance door)
        {
            // Width lives on the type for standard door families, but some families
            // make it an instance parameter, and purely custom ones only have the
            // generic family width - try all three before giving up.
            FamilySymbol symbol = door.Symbol;
            double width = PositiveDoubleOrZero(symbol != null
                ? symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH) : null);
            if (width <= 0.0)
            {
                width = PositiveDoubleOrZero(door.get_Parameter(BuiltInParameter.DOOR_WIDTH));
            }
            if (width <= 0.0)
            {
                width = PositiveDoubleOrZero(symbol != null
                    ? symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) : null);
            }
            if (width <= 0.0)
            {
                // Curtain wall doors are panels whose size the grid drives - their
                // width lives on the panel, not in any door parameter.
                width = PositiveDoubleOrZero(door.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH));
            }
            if (width <= 0.0)
            {
                width = BoxPlanWidthFt(door, 1.5, 10.0);
            }
            return width > 0.0 ? width : FallbackDoorWidthFt;
        }

        private static double PositiveDoubleOrZero(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
            {
                return 0.0;
            }
            double value = parameter.AsDouble();
            return value > 0.0 ? value : 0.0;
        }

        /// <summary>Cut intervals clamped to [0, totalLength], sorted and merged -
        /// overlapping doors (double doors, close pairs) become one opening instead
        /// of producing slivers between them.</summary>
        private static List<(double Start, double End)> MergeCuts(
            List<(double Start, double End)> cuts, double totalLength)
        {
            var merged = new List<(double Start, double End)>();
            if (totalLength <= 0.0)
            {
                return merged;
            }

            cuts.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach ((double Start, double End) cut in cuts)
            {
                double start = Math.Max(0.0, cut.Start);
                double end = Math.Min(totalLength, cut.End);
                if (end <= start)
                {
                    continue; // gap lies entirely off this wall (door projected to an end)
                }
                if (merged.Count > 0 && start <= merged[merged.Count - 1].End)
                {
                    merged[merged.Count - 1] = (merged[merged.Count - 1].Start,
                        Math.Max(merged[merged.Count - 1].End, end));
                }
                else
                {
                    merged.Add((start, end));
                }
            }

            return merged;
        }

        /// <summary>Complement of the merged cut intervals within [0, totalLength] -
        /// i.e. the stretches of centerline that stay solid wall.</summary>
        private static List<(double Start, double End)> KeepIntervals(
            List<(double Start, double End)> merged, double totalLength)
        {
            var keeps = new List<(double Start, double End)>();
            if (totalLength <= 0.0)
            {
                return keeps;
            }

            double cursor = 0.0;
            foreach ((double Start, double End) cut in merged)
            {
                if (cut.Start > cursor)
                {
                    keeps.Add((cursor, cut.Start));
                }
                cursor = Math.Max(cursor, cut.End);
            }
            if (cursor < totalLength)
            {
                keeps.Add((cursor, totalLength));
            }
            return keeps;
        }

        /// <summary>Emits the [start, end] stretch of the polyline as one WallSegment
        /// per straight sub-segment, skipping slivers under <see cref="MinSegmentFt"/>.</summary>
        private static void EmitRun(List<(double X, double Y)> pts, double[] stations,
            double start, double end, double thicknessFt, double heightFt,
            List<WadGen.WallSegment> output)
        {
            const double eps = 1e-6;
            if (end - start < eps)
            {
                return;
            }

            var run = new List<(double X, double Y)>();
            run.Add(PointAtStation(pts, stations, start));
            for (int i = 0; i < pts.Count; i++)
            {
                if (stations[i] > start + eps && stations[i] < end - eps)
                {
                    run.Add(pts[i]);
                }
            }
            run.Add(PointAtStation(pts, stations, end));

            // Accumulate from an anchor rather than testing chords independently:
            // Curve.Tessellate() on a small-radius arc yields uniformly sub-0.25 ft
            // chords, and dropping each one on its own would silently erase the
            // whole wall (or leave micro-gaps mid-run). Coalescing until the span
            // from the anchor reaches the threshold slightly straightens tight arcs,
            // which is invisible at Doom scale (0.25 ft = 4 map units).
            int anchor = 0;
            int lastEmitted = -1;
            for (int i = 1; i < run.Count; i++)
            {
                double dx = run[i].X - run[anchor].X;
                double dy = run[i].Y - run[anchor].Y;
                if (Math.Sqrt(dx * dx + dy * dy) < MinSegmentFt)
                {
                    if (i == run.Count - 1 && lastEmitted >= 0)
                    {
                        // Fold a sub-threshold tail into the previous segment so
                        // the run still ends exactly at the keep-interval boundary.
                        output[lastEmitted].X2 = run[i].X;
                        output[lastEmitted].Y2 = run[i].Y;
                    }
                    continue;
                }
                output.Add(new WadGen.WallSegment
                {
                    X1 = run[anchor].X,
                    Y1 = run[anchor].Y,
                    X2 = run[i].X,
                    Y2 = run[i].Y,
                    ThicknessFt = thicknessFt,
                    HeightFt = heightFt,
                });
                lastEmitted = output.Count - 1;
                anchor = i;
            }
        }

        private static double[] CumulativeStations(List<(double X, double Y)> pts)
        {
            var stations = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++)
            {
                double dx = pts[i].X - pts[i - 1].X;
                double dy = pts[i].Y - pts[i - 1].Y;
                stations[i] = stations[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            return stations;
        }

        /// <summary>Arc-length station of the closest point on the polyline to (px, py) -
        /// how a door's insertion point (which sits on the wall face plane, not exactly
        /// on the centerline) is mapped back onto the centerline.</summary>
        private static double StationOfClosestPoint(List<(double X, double Y)> pts,
            double[] stations, double px, double py)
        {
            double bestDistSq = double.MaxValue;
            double bestStation = 0.0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double ax = pts[i].X;
                double ay = pts[i].Y;
                double dx = pts[i + 1].X - ax;
                double dy = pts[i + 1].Y - ay;
                double lenSq = dx * dx + dy * dy;
                double t = 0.0;
                if (lenSq > 1e-12)
                {
                    t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
                    if (t < 0.0)
                    {
                        t = 0.0;
                    }
                    else if (t > 1.0)
                    {
                        t = 1.0;
                    }
                }
                double cx = ax + t * dx;
                double cy = ay + t * dy;
                double distSq = (px - cx) * (px - cx) + (py - cy) * (py - cy);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestStation = stations[i] + t * Math.Sqrt(lenSq);
                }
            }
            return bestStation;
        }

        private static (double X, double Y) PointAtStation(List<(double X, double Y)> pts,
            double[] stations, double station)
        {
            if (station <= 0.0)
            {
                return pts[0];
            }
            int last = pts.Count - 1;
            if (station >= stations[last])
            {
                return pts[last];
            }
            for (int i = 0; i < last; i++)
            {
                if (station <= stations[i + 1])
                {
                    double segLen = stations[i + 1] - stations[i];
                    double t = segLen > 1e-12 ? (station - stations[i]) / segLen : 0.0;
                    return (pts[i].X + t * (pts[i + 1].X - pts[i].X),
                            pts[i].Y + t * (pts[i + 1].Y - pts[i].Y));
                }
            }
            return pts[last];
        }

        /// <summary>Main-model elements report a null DesignOption; anything else is
        /// visible to the user only if its option is the set's primary one.</summary>
        private static bool IsMainModelOrPrimary(Element element)
        {
            DesignOption option = element.DesignOption;
            return option == null || option.IsPrimary;
        }

        private static void CollectRoomPoints(Document doc, string chosenLevelName, List<WadGen.RoomPoint> output)
        {
            // Rooms are only placement hints, so any failure here (rooms are a
            // frequent source of API surprises in linked/partially-loaded models)
            // just means the WAD gets its things placed without them. Level-matched
            // so whole-model exports drop each level's hints into its own cluster.
            var rooms = new List<WadGen.RoomPoint>();
            try
            {
                // Room can't be used with OfClass directly (ElementClassFilter
                // rejects it) - collect the SpatialElement base class and filter.
                IList<Element> spatial = new FilteredElementCollector(doc)
                    .OfClass(typeof(SpatialElement))
                    .ToElements();
                foreach (Element element in spatial)
                {
                    Room room = element as Room;
                    if (room == null || room.Area <= 0.0 || !IsMainModelOrPrimary(room))
                    {
                        continue; // unplaced/unbounded rooms have no meaningful point
                    }
                    if (!string.Equals(LevelNameFromId(doc, room.LevelId), chosenLevelName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    LocationPoint location = room.Location as LocationPoint;
                    XYZ point = location != null ? location.Point : null;
                    if (point == null)
                    {
                        continue;
                    }
                    rooms.Add(new WadGen.RoomPoint { X = point.X, Y = point.Y });
                }
            }
            catch
            {
                rooms.Clear();
            }
            output.AddRange(rooms);
        }

        /// <summary>
        /// Furniture, casework and columns on the exported level become box-shaped
        /// obstacles. Each category is collected independently so one exotic family
        /// blowing up a collector only costs that category, not the rest.
        /// </summary>
        private static void CollectPrisms(Document doc, string chosenLevelName, double levelElevationFt, List<WadGen.Prism> output)
        {
            // Furnishings and columns share the Prism shape on purpose: WadGen
            // decides solid-pillar vs see-and-shoot-over purely from the height,
            // so a 30 ft column and a 3 ft credenza need no separate tagging here.
            CollectPrismCategory(doc, chosenLevelName, levelElevationFt, BuiltInCategory.OST_Furniture, output);
            CollectPrismCategory(doc, chosenLevelName, levelElevationFt, BuiltInCategory.OST_Casework, output);
            CollectPrismCategory(doc, chosenLevelName, levelElevationFt, BuiltInCategory.OST_Columns, output);
            CollectPrismCategory(doc, chosenLevelName, levelElevationFt, BuiltInCategory.OST_StructuralColumns, output);
        }

        private static void CollectPrismCategory(Document doc, string chosenLevelName,
            double levelElevationFt, BuiltInCategory category, List<WadGen.Prism> output)
        {
            // Obstacles are decoration, not the map - any API surprise (in-place
            // families are inventive) just means this category sits the level out.
            try
            {
                IList<Element> elements = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToElements();
                foreach (Element element in elements)
                {
                    if (!IsMainModelOrPrimary(element))
                    {
                        continue;
                    }
                    if (!string.Equals(ResolveInstanceLevelName(doc, element), chosenLevelName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // same level rule the doors follow
                    }

                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    if (box == null)
                    {
                        continue; // no built geometry (unplaced, symbolic-only, ...)
                    }
                    double ex = box.Max.X - box.Min.X;
                    double ey = box.Max.Y - box.Min.Y;

                    // Wall-hung pieces (upper cabinets, shelving, vanities) start
                    // well above the floor - the player walks UNDER them in reality,
                    // so standing a floor block there would be wrong. Doom has no
                    // floating boxes, so they're simply skipped.
                    if (!double.IsPositiveInfinity(levelElevationFt) && box.Min.Z > levelElevationFt + 2.0)
                    {
                        continue;
                    }

                    // Height above the FLOOR, not the box's own extent, when the
                    // level elevation is known - an item on a low plinth blocks up
                    // to where it actually reaches.
                    double height = double.IsPositiveInfinity(levelElevationFt)
                        ? box.Max.Z - box.Min.Z
                        : box.Max.Z - levelElevationFt;

                    // Slivers vanish at Doom scale; anything over 30 ft a side is
                    // more likely a furniture system or a modelling accident than
                    // an obstacle; flat things (rugs, mats) aren't worth a sector.
                    if (ex < 0.4 || ey < 0.4 || ex > 30.0 || ey > 30.0 || height < 0.5)
                    {
                        continue;
                    }

                    // World-axis-aligned box on purpose: shrink-wrapping rotated
                    // instances via their transform isn't worth the API round-trips.
                    // A 45-degree sofa just gets a slightly fat box, which plays fine.
                    output.Add(new WadGen.Prism
                    {
                        CX = (box.Min.X + box.Max.X) / 2.0,
                        CY = (box.Min.Y + box.Max.Y) / 2.0,
                        DirX = 1.0,
                        DirY = 0.0,
                        HalfLenFt = ex / 2.0,
                        HalfWidthFt = ey / 2.0,
                        HeightFt = height,
                    });
                }
            }
            catch
            {
                // whatever this category managed to add before failing stays -
                // a partial obstacle set is strictly better than none
            }
        }

        /// <summary>
        /// Stairs based on the exported level become climbable step-runs. Only the
        /// bounding box is read - the runs/landings stairs API is a rabbit hole -
        /// so the run is assumed to go along the box's longer plan axis.
        /// </summary>
        private static void CollectStairs(Document doc, string chosenLevelName, List<WadGen.StairFlight> output)
        {
            try
            {
                IList<Element> stairs = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Stairs)
                    .WhereElementIsNotElementType()
                    .ToElements();
                foreach (Element element in stairs)
                {
                    if (!IsMainModelOrPrimary(element))
                    {
                        continue;
                    }
                    if (!string.Equals(ResolveStairBaseLevelName(doc, element), chosenLevelName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    if (box == null)
                    {
                        continue;
                    }
                    double ex = box.Max.X - box.Min.X;
                    double ey = box.Max.Y - box.Min.Y;
                    double longer = Math.Max(ex, ey);
                    double shorter = Math.Min(ex, ey);
                    if (longer < 3.0)
                    {
                        continue; // shorter than a single stride - not a real flight
                    }
                    // A near-square box means a spiral stair, a switchback with a
                    // big landing, or a multi-run assembly: the run direction can't
                    // be inferred from the box, and steps emitted at a wrong angle
                    // would wall off the stair. Skipping them is the lesser evil
                    // (known limitation of the bbox-only approach).
                    if (shorter < 1e-9 || longer / shorter < 1.3)
                    {
                        continue;
                    }

                    // Run along the longer axis, climbing from the min-coordinate
                    // end: which end is really the bottom is unknowable from a
                    // bounding box, so a flight may ascend the "wrong" way vs the
                    // Revit model. Cosmetic only - the steps walk fine either way.
                    double cx = (box.Min.X + box.Max.X) / 2.0;
                    double cy = (box.Min.Y + box.Max.Y) / 2.0;
                    bool alongX = ex >= ey;
                    output.Add(new WadGen.StairFlight
                    {
                        X1 = alongX ? box.Min.X : cx,
                        Y1 = alongX ? cy : box.Min.Y,
                        X2 = alongX ? box.Max.X : cx,
                        Y2 = alongX ? cy : box.Max.Y,
                        WidthFt = Math.Min(8.0, Math.Max(2.0, shorter)),
                        RiseFt = Math.Min(30.0, Math.Max(1.0, box.Max.Z - box.Min.Z)),
                    });
                }
            }
            catch
            {
                // stairs are optional; a failing collector loses only the stairs
            }
        }

        /// <summary>Best-effort level name for a placed instance; "" when nothing
        /// resolves, which then only matches the "" bucket - the same rule walls
        /// and doors follow for unresolvable levels.</summary>
        private static string ResolveInstanceLevelName(Document doc, Element element)
        {
            // Element.LevelId covers level-hosted instances; face- and workplane-
            // hosted families answer InvalidElementId there but usually still carry
            // a level in "Schedule Level" / "Base Level" parameters.
            string name = LevelNameFromId(doc, element.LevelId);
            if (name.Length == 0)
            {
                name = LevelNameFromId(doc, ParameterAsElementId(element, BuiltInParameter.FAMILY_LEVEL_PARAM));
            }
            if (name.Length == 0)
            {
                name = LevelNameFromId(doc, ParameterAsElementId(element, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM));
            }
            if (name.Length == 0)
            {
                // Face-based and in-place families answer InvalidElementId from all
                // of the above but usually still expose "Schedule Level".
                name = LevelNameFromId(doc, ParameterAsElementId(element, BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM));
            }
            if (name.Length == 0)
            {
                name = LevelNameFromId(doc, ParameterAsElementId(element, BuiltInParameter.SCHEDULE_LEVEL_PARAM));
            }
            return name;
        }

        private static string ResolveStairBaseLevelName(Document doc, Element stair)
        {
            // Stairs keep their base level in a stairs-specific parameter; LevelId
            // answers for most, but sketch-based/converted oddities may only reply
            // through the "Base Level" parameter.
            string name = LevelNameFromId(doc, stair.LevelId);
            if (name.Length == 0)
            {
                name = LevelNameFromId(doc, ParameterAsElementId(stair, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM));
            }
            return name;
        }

        private static ElementId ParameterAsElementId(Element element, BuiltInParameter builtInParameter)
        {
            Parameter parameter = element.get_Parameter(builtInParameter);
            return parameter != null ? parameter.AsElementId() : null;
        }

        private static string LevelNameFromId(Document doc, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                return "";
            }
            Level level = doc.GetElement(levelId) as Level;
            return level != null ? (level.Name ?? "") : "";
        }
    }
}
