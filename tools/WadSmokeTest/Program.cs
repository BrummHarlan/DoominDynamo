using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DoomInDynamo.WadGen;
using ManagedDoom;
using ManagedDoom.Video;

namespace WadSmokeTest
{
    /// <summary>
    /// End-to-end verification of the Revit->WAD pipeline without needing Revit or
    /// a real IWAD: build a synthetic building, export it through the exact code
    /// the Dynamo node runs, then load the result in the REAL ManagedDoom engine
    /// (GameContent.CreateDummy + a stub resource WAD supplies palette/flats/
    /// textures) and simulate a player running around for a while. On top of that,
    /// structurally validate every lump the way the engine's readers would.
    /// Exit code 0 = all checks passed.
    /// </summary>
    internal static class Program
    {
        private static int failures;

        private static int Main()
        {
            var workDir = Path.Combine(Path.GetTempPath(), "DoomInDynamo", "smoketest");
            Directory.CreateDirectory(workDir);

            try
            {
                var model = BuildSyntheticBuilding();

                var wadPath = Path.Combine(workDir, "generated.wad");
                var report = DoomInDynamo.RevitToWad.ExportModel(model, wadPath, seed: 1234, itemCount: 60, includeMonsters: true);
                Console.WriteLine("Export report: " + report);
                Console.WriteLine();

                Check(File.Exists(wadPath), "WAD file written");

                DeterminismChecks(model, workDir);
                var wad = StructuralChecks(wadPath);

                // Engine runs: doom2-named stub exercises the MAP01 slot,
                // doom1-named stub the E1M1 slot.
                EngineRun(workDir, wadPath, "doom2.wad", wad);
                EngineRun(workDir, wadPath, "doom1.wad", wad);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string what)
        {
            Console.WriteLine((condition ? "  ok   " : "  FAIL ") + what);
            if (!condition)
            {
                failures++;
            }
        }

        // ------------------------------------------------------------------
        // Synthetic building: a 40x30 ft rectangle with a doorway in the south
        // wall, an interior partition with its own doorway, and one angled wall -
        // exercises door gaps, axis-aligned and diagonal geometry.
        // ------------------------------------------------------------------
        private static BuildingModel BuildSyntheticBuilding()
        {
            var model = new BuildingModel
            {
                LevelName = "Level 1",
                DocumentTitle = "SmokeTest"
            };

            const double t = 0.66;
            const double h = 9.0;

            // South wall with a 3.5 ft door gap centered at x=10.
            AddWall(model, 0, 0, 8.25, 0, t, h);
            AddWall(model, 11.75, 0, 40, 0, t, h);
            // North wall with a 4 ft window gap centered at x=30 (gap = width+0.5,
            // like the extractor cuts), then west and east walls.
            AddWall(model, 0, 30, 27.75, 30, t, h);
            AddWall(model, 32.25, 30, 40, 30, t, h);
            AddWall(model, 0, 0, 0, 30, t, h);
            AddWall(model, 40, 0, 40, 30, t, h);
            // Interior partition at x=22 with a doorway from y=12..16.
            AddWall(model, 22, 0, 22, 12, t, h);
            AddWall(model, 22, 16, 22, 30, t, h);
            // Angled wall.
            AddWall(model, 26, 5, 36, 11, t, h);

            // A 3 ft parapet in the west room - low enough to become a see-over
            // low wall instead of a solid one.
            AddWall(model, 5, 20, 13, 20, t, 3.0);

            // Working doors standing in both gaps (what RevitExtractor emits per
            // merged door cut).
            model.Doors.Add(new DoorOpening { CX = 10, CY = 0, DirX = 1, DirY = 0, WidthFt = 3.5, ThicknessFt = t });
            model.Doors.Add(new DoorOpening { CX = 22, CY = 14, DirX = 0, DirY = 1, WidthFt = 4.0, ThicknessFt = t });

            // A window in the north wall gap: sill 3 ft, head 7 ft, in a
            // full-height (9 ft) wall.
            model.Windows.Add(new WindowOpening
            {
                CX = 30, CY = 30, DirX = 1, DirY = 0,
                WidthFt = 4.5, ThicknessFt = t, SillFt = 3.0, HeadFt = 7.0, HostHeightFt = h
            });

            // Furnishings: a desk (raised block) and a full-height column (solid).
            model.Prisms.Add(new Prism { CX = 30, CY = 19, DirX = 1, DirY = 0, HalfLenFt = 2.5, HalfWidthFt = 1.25, HeightFt = 2.5 });
            model.Prisms.Add(new Prism { CX = 25, CY = 3, DirX = 1, DirY = 0, HalfLenFt = 0.5, HalfWidthFt = 0.5, HeightFt = 9.0 });

            // A 12 ft stair flight rising 6 ft along the east room's north side.
            model.Stairs.Add(new StairFlight { X1 = 25, Y1 = 25, X2 = 37, Y2 = 25, WidthFt = 4.0, RiseFt = 6.0 });

            model.Rooms.Add(new RoomPoint { X = 10, Y = 15 });
            model.Rooms.Add(new RoomPoint { X = 30, Y = 22 });

            return model;
        }

        private static void AddWall(BuildingModel model, double x1, double y1, double x2, double y2, double t, double h)
        {
            model.Walls.Add(new WallSegment { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, ThicknessFt = t, HeightFt = h });
        }

        private static void DeterminismChecks(BuildingModel model, string workDir)
        {
            Console.WriteLine("Determinism:");
            var a = Path.Combine(workDir, "det-a.wad");
            var b = Path.Combine(workDir, "det-b.wad");
            var c = Path.Combine(workDir, "det-c.wad");
            DoomInDynamo.RevitToWad.ExportModel(model, a, 42, 40, true);
            DoomInDynamo.RevitToWad.ExportModel(model, b, 42, 40, true);
            DoomInDynamo.RevitToWad.ExportModel(model, c, 43, 40, true);

            Check(BytesEqual(File.ReadAllBytes(a), File.ReadAllBytes(b)), "same seed => identical WAD");
            Check(!BytesEqual(File.ReadAllBytes(a), File.ReadAllBytes(c)), "different seed => different WAD");
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Structural validation: parse the PWAD with an independent little reader
        // and apply every constraint the engine relies on.
        // ------------------------------------------------------------------

        private sealed class ParsedMap
        {
            public short[][] Things;      // x, y, angle, type, flags
            public short[][] Linedefs;    // v1, v2, flags, special, tag, front, back
            public short[][] Sidedefs;    // xoff, yoff, sector (textures separate)
            public short[][] Vertices;    // x, y
            public short[][] Segs;        // v1, v2, angle, line, side, offset
            public short[][] Subsectors;  // count, first
            public short[][] Nodes;       // x, y, dx, dy, fbox*4, bbox*4, fchild, bchild
            public short[] Blockmap;
            public int SectorCount;
        }

        private static ParsedMap StructuralChecks(string wadPath)
        {
            Console.WriteLine("Structure:");
            var bytes = File.ReadAllBytes(wadPath);

            Check(Encoding.ASCII.GetString(bytes, 0, 4) == "PWAD", "PWAD header");
            var lumpCount = BitConverter.ToInt32(bytes, 4);
            var dirOffset = BitConverter.ToInt32(bytes, 8);
            Check(lumpCount == 22, "22 lumps (two map slots)");

            var lumps = new List<Tuple<string, byte[]>>();
            for (var i = 0; i < lumpCount; i++)
            {
                var entry = dirOffset + 16 * i;
                var pos = BitConverter.ToInt32(bytes, entry);
                var size = BitConverter.ToInt32(bytes, entry + 4);
                var name = Encoding.ASCII.GetString(bytes, entry + 8, 8).TrimEnd('\0');
                var data = new byte[size];
                Array.Copy(bytes, pos, data, 0, size);
                lumps.Add(Tuple.Create(name, data));
            }

            Check(lumps[0].Item1 == "MAP01" && lumps[11].Item1 == "E1M1", "both map slots present");

            ParsedMap parsed = null;
            foreach (var slotStart in new[] { 0, 11 })
            {
                var slot = lumps[slotStart].Item1;
                var map = ParseSlot(lumps, slotStart);
                ValidateSlot(map, slot, lumps, slotStart);
                parsed = map;
            }

            return parsed;
        }

        private static ParsedMap ParseSlot(List<Tuple<string, byte[]>> lumps, int start)
        {
            var map = new ParsedMap();
            map.Things = Records(lumps[start + 1].Item2, 5);
            map.Linedefs = Records(lumps[start + 2].Item2, 7);
            map.Vertices = Records(lumps[start + 4].Item2, 2);
            map.Segs = Records(lumps[start + 5].Item2, 6);
            map.Subsectors = Records(lumps[start + 6].Item2, 2);
            map.Nodes = Records(lumps[start + 7].Item2, 14);
            map.SectorCount = lumps[start + 8].Item2.Length / 26;

            // Sidedefs are 30 bytes with strings in the middle; pull the numeric fields.
            var sd = lumps[start + 3].Item2;
            var count = sd.Length / 30;
            map.Sidedefs = new short[count][];
            for (var i = 0; i < count; i++)
            {
                map.Sidedefs[i] = new[]
                {
                    BitConverter.ToInt16(sd, 30 * i),
                    BitConverter.ToInt16(sd, 30 * i + 2),
                    BitConverter.ToInt16(sd, 30 * i + 28)
                };
            }

            var bm = lumps[start + 10].Item2;
            map.Blockmap = new short[bm.Length / 2];
            for (var i = 0; i < map.Blockmap.Length; i++)
            {
                map.Blockmap[i] = BitConverter.ToInt16(bm, 2 * i);
            }

            return map;
        }

        private static short[][] Records(byte[] data, int fields)
        {
            var size = fields * 2;
            var count = data.Length / size;
            var result = new short[count][];
            for (var i = 0; i < count; i++)
            {
                result[i] = new short[fields];
                for (var f = 0; f < fields; f++)
                {
                    result[i][f] = BitConverter.ToInt16(data, size * i + 2 * f);
                }
            }
            return result;
        }

        private static void ValidateSlot(ParsedMap map, string slot, List<Tuple<string, byte[]>> lumps, int slotStart)
        {
            // One big sector + 2 doors + 1 window + 1 low wall + 1 desk + 11 stair
            // steps (the 12 ft flight loses 8 units to the end insets -> 11 treads;
            // the column is full-height solid, so no sector of its own).
            Check(map.SectorCount == 17, slot + ": expected sector inventory (got " + map.SectorCount + ")");
            Check(map.Vertices.Length > 0 && map.Linedefs.Length >= 8, slot + ": has geometry");
            Check(map.Segs.Length > 0 && map.Subsectors.Length > 0 && map.Nodes.Length > 0, slot + ": has BSP");

            // Reference validity.
            var ok = true;
            foreach (var line in map.Linedefs)
            {
                ok &= InRange(line[0], map.Vertices.Length) && InRange(line[1], map.Vertices.Length);
                ok &= InRange(line[5], map.Sidedefs.Length);
                ok &= line[6] == -1 || InRange(line[6], map.Sidedefs.Length);
            }
            Check(ok, slot + ": linedef references valid");

            ok = true;
            foreach (var side in map.Sidedefs)
            {
                ok &= InRange(side[2], map.SectorCount);
            }
            Check(ok, slot + ": all sidedefs reference a real sector");

            // Door mechanics as data: each door contributes two usable DR faces
            // (special 1) whose back sector is closed (ceiling == floor), plus the
            // one S1 exit on the boundary.
            var drFaces = 0;
            var exits = 0;
            ok = true;
            foreach (var line in map.Linedefs)
            {
                if (line[3] == 1)
                {
                    drFaces++;
                    ok &= line[6] != -1;
                    ok &= (line[2] & 0x0004) != 0; // two-sided flag
                }
                if (line[3] == 11)
                {
                    exits++;
                }
            }
            Check(drFaces == 4, slot + ": four DR door faces (two per door)");
            Check(exits == 4, slot + ": exit pedestal has four usable faces");
            Check(ok, slot + ": door faces are two-sided with a back sector");

            // The window box: two-sided AND explicitly blocking (players/monsters
            // can't pass, but sight, hitscan and missiles go through the opening).
            var windowLines = 0;
            foreach (var line in map.Linedefs)
            {
                if ((line[2] & 0x0005) == 0x0005 && line[6] != -1)
                {
                    windowLines++;
                }
            }
            Check(windowLines == 4, slot + ": four blocking see-through window lines");

            // Jamb slot seals: each door/window box insets 4 units from its cut
            // ends, and one-sided blocking lines must seal the resulting slots on
            // both wall faces or hitscan/sight leak past the sill (2 doors + 1
            // window = 12 seals on top of the previous 102 lines).
            Check(map.Linedefs.Length == 114, slot + ": jamb seals emitted (" + map.Linedefs.Length + " linedefs)");

            ok = true;
            foreach (var seg in map.Segs)
            {
                ok &= InRange(seg[0], map.Vertices.Length) && InRange(seg[1], map.Vertices.Length);
                ok &= InRange(seg[3], map.Linedefs.Length);
                ok &= seg[4] == 0 || seg[4] == 1;
                // A back-side seg is only legal on a linedef that has a back sidedef.
                if (seg[4] == 1)
                {
                    ok &= map.Linedefs[seg[3]][6] != -1;
                }
            }
            Check(ok, slot + ": seg references valid");

            ok = true;
            var covered = new bool[map.Segs.Length];
            foreach (var ss in map.Subsectors)
            {
                ok &= ss[0] > 0;
                ok &= InRange(ss[1], map.Segs.Length) && ss[1] + ss[0] <= map.Segs.Length;
                for (var s = ss[1]; s < ss[1] + ss[0] && s < covered.Length; s++)
                {
                    ok &= !covered[s];
                    covered[s] = true;
                }
            }
            foreach (var c in covered)
            {
                ok &= c;
            }
            Check(ok, slot + ": subsectors partition the segs contiguously");

            ok = true;
            foreach (var node in map.Nodes)
            {
                foreach (var childIndex in new[] { 12, 13 })
                {
                    int child = node[childIndex];
                    if ((child & 0x8000) != 0)
                    {
                        ok &= InRange(child & 0x7FFF, map.Subsectors.Length);
                    }
                    else
                    {
                        ok &= InRange(child, map.Nodes.Length);
                    }
                }
            }
            Check(ok, slot + ": node children valid");

            // Walk the BSP for random interior points exactly the way
            // Geometry.PointOnSide does, and confirm each walk terminates at a
            // subsector within a bounded number of steps.
            short minX = short.MaxValue, minY = short.MaxValue, maxX = short.MinValue, maxY = short.MinValue;
            foreach (var v in map.Vertices)
            {
                minX = Math.Min(minX, v[0]);
                minY = Math.Min(minY, v[1]);
                maxX = Math.Max(maxX, v[0]);
                maxY = Math.Max(maxY, v[1]);
            }

            var rng = new Random(7);
            ok = true;
            for (var i = 0; i < 2000 && ok; i++)
            {
                double px = minX + rng.NextDouble() * (maxX - minX);
                double py = minY + rng.NextDouble() * (maxY - minY);

                var nodeIndex = map.Nodes.Length - 1;
                var steps = 0;
                while (true)
                {
                    if (++steps > map.Nodes.Length + 8)
                    {
                        ok = false;
                        break;
                    }

                    var n = map.Nodes[nodeIndex];
                    var cross = (double)n[2] * (py - n[1]) - (double)n[3] * (px - n[0]);
                    var child = cross < 0 ? n[12] : n[13];
                    if ((child & 0x8000) != 0)
                    {
                        if (!InRange(child & 0x7FFF, map.Subsectors.Length))
                        {
                            ok = false;
                        }
                        break;
                    }
                    nodeIndex = child;
                }
            }
            Check(ok, slot + ": BSP walk reaches a subsector for 2000 random points");

            // Blockmap: header sane, every offset walkable to a -1 terminator,
            // every linedef listed in at least one block.
            ok = map.Blockmap.Length > 4;
            var width = map.Blockmap[2];
            var height = map.Blockmap[3];
            ok &= width > 0 && height > 0 && 4 + width * height <= map.Blockmap.Length;
            var seen = new bool[map.Linedefs.Length];
            for (var b = 0; b < width * height && ok; b++)
            {
                int offset = map.Blockmap[4 + b];
                ok &= offset >= 4 && offset < map.Blockmap.Length;
                while (ok && map.Blockmap[offset] != -1)
                {
                    int lineIndex = map.Blockmap[offset];
                    ok &= InRange(lineIndex, map.Linedefs.Length);
                    if (ok)
                    {
                        seen[lineIndex] = true;
                    }
                    offset++;
                    ok &= offset < map.Blockmap.Length;
                }
            }
            Check(ok, slot + ": blockmap offsets and lists well-formed");
            var allSeen = true;
            foreach (var s in seen)
            {
                allSeen &= s;
            }
            Check(allSeen, slot + ": every linedef appears in the blockmap");

            // Things.
            var playerStarts = 0;
            ok = true;
            foreach (var thing in map.Things)
            {
                if (thing[3] == 1)
                {
                    playerStarts++;
                }
                ok &= thing[0] >= minX && thing[0] <= maxX && thing[1] >= minY && thing[1] <= maxY;
                ok &= (thing[4] & 7) == 7;
            }
            Check(playerStarts == 1, slot + ": exactly one player 1 start");
            Check(map.Things.Length > 10, slot + ": items were placed (" + map.Things.Length + " things)");
            Check(ok, slot + ": things inside bounds with all-skill flags");

            // REJECT sized for sector count.
            var reject = lumps[slotStart + 9].Item2;
            Check(reject.Length == (map.SectorCount * map.SectorCount + 7) / 8, slot + ": reject sized correctly");
        }

        private static bool InRange(int index, int count)
        {
            return index >= 0 && index < count;
        }

        // ------------------------------------------------------------------
        // The real engine: stub resource WAD + generated PWAD -> World -> tics.
        // ------------------------------------------------------------------

        private static void EngineRun(string workDir, string generatedWad, string stubName, ParsedMap parsed)
        {
            Console.WriteLine("Engine (" + stubName + "):");
            var stubPath = Path.Combine(workDir, stubName);
            WriteStubResourceWad(stubPath);

            using (var content = GameContent.CreateDummy(stubPath, generatedWad))
            {
                var options = new GameOptions();
                options.GameMode = content.Wad.GameMode == GameMode.Indetermined
                    ? GameMode.Commercial
                    : content.Wad.GameMode;
                options.Episode = 1;
                options.Map = 1;

                var world = new World(content, options, null);
                var player = options.Players[0];

                Check(player.Mobj != null, "player spawned");
                if (player.Mobj == null)
                {
                    return;
                }

                Check(player.Mobj.Z.Data == 0, "player standing on floor height 0");

                var startX = player.Mobj.X.ToDouble();
                var startY = player.Mobj.Y.ToDouble();

                // Map bounds from the parsed lumps, with a little slack; if the
                // player ever leaves them, collision let them through a wall.
                short minX = short.MaxValue, minY = short.MaxValue, maxX = short.MinValue, maxY = short.MinValue;
                foreach (var v in parsed.Vertices)
                {
                    minX = Math.Min(minX, v[0]);
                    minY = Math.Min(minY, v[1]);
                    maxX = Math.Max(maxX, v[0]);
                    maxY = Math.Max(maxY, v[1]);
                }

                // Door mechanics through the real engine: use a DR face, watch the
                // door sector's ceiling rise off the floor, then watch it come back
                // down after the engine's wait. Done at tic 0, before the monsters
                // are awake enough to wander into the doorway and hold it open.
                LineDef doorLine = null;
                foreach (var line in world.Map.Lines)
                {
                    if ((int)line.Special == 1 && line.BackSector != null)
                    {
                        doorLine = line;
                        break;
                    }
                }
                Check(doorLine != null, "map has a usable DR door line");
                if (doorLine != null)
                {
                    var doorSector = doorLine.BackSector;
                    Check(doorSector.CeilingHeight.Data == doorSector.FloorHeight.Data, "door starts closed");

                    world.MapInteraction.UseSpecialLine(player.Mobj, doorLine, 0);
                    var openedTo = 0;
                    var closedAgain = false;
                    for (var i = 0; i < 400; i++)
                    {
                        world.Update();
                        var height = doorSector.CeilingHeight.Data;
                        openedTo = Math.Max(openedTo, height);
                        if (i > 100 && height == doorSector.FloorHeight.Data)
                        {
                            closedAgain = true;
                            break;
                        }
                    }
                    Check(openedTo > 0, "door opened on use (rose " + (openedTo >> 16) + " units)");
                    Check(closedAgain, "door closed again after its wait");
                }

                var exceptions = 0;
                var escaped = false;
                var tics = 0;
                var framesRendered = 0;

                // The REAL software renderer, aimed at the dummy content - any
                // seg/texture/BSP-traversal defect in the generated map that would
                // crash a real play session should crash here too.
                var screen = new DrawScreen(content.Wad, 320, 200);
                var renderer = new ThreeDRenderer(content, screen, 7);

                try
                {
                    // Phase 1: idle - monsters wake up, look for the player
                    // (exercises REJECT + BSP line-of-sight checks).
                    for (var i = 0; i < 70; i++)
                    {
                        world.Update();
                        tics++;
                    }

                    renderer.Render(player, Fixed.One);
                    framesRendered++;

                    // Phase 2: run forward while slowly turning, several sweeps -
                    // rams the player into pillars and boundary from many angles
                    // (exercises blockmap collision), while monsters act; render a
                    // frame every few tics so walls get drawn from many viewpoints.
                    for (var sweep = 0; sweep < 12; sweep++)
                    {
                        for (var i = 0; i < 35; i++)
                        {
                            player.Cmd.ForwardMove = 50;
                            player.Cmd.AngleTurn = 512;
                            world.Update();
                            tics++;

                            if (tics % 7 == 0)
                            {
                                renderer.Render(player, Fixed.One);
                                framesRendered++;
                            }

                            var x = player.Mobj.X.ToDouble();
                            var y = player.Mobj.Y.ToDouble();
                            if (x < minX - 1 || x > maxX + 1 || y < minY - 1 || y > maxY + 1)
                            {
                                escaped = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions++;
                    Console.WriteLine("         engine threw at tic " + tics + ": " + ex);
                }

                Check(exceptions == 0, "simulated " + tics + " tics without engine exceptions");
                Check(framesRendered >= 60, "software-rendered " + framesRendered + " frames");
                Check(!escaped, "player never escaped the map bounds");

                var movedSq =
                    (player.Mobj.X.ToDouble() - startX) * (player.Mobj.X.ToDouble() - startX) +
                    (player.Mobj.Y.ToDouble() - startY) * (player.Mobj.Y.ToDouble() - startY);
                Check(player.PlayerState == PlayerState.Dead || movedSq > 16 * 16,
                    "player actually moved (or died trying: " + player.PlayerState + ")");

                // Raised-floor geometry through the engine's own point queries: the
                // synthetic model puts a 3 ft low wall at (9, 20)ft -> floor 48, a
                // 2.5 ft desk at (30, 19)ft -> floor 40, and a stair from (25,25)
                // to (37,25)ft that after the 4-unit end insets becomes 11 treads
                // rising 8 units each. Model center is (20, 15)ft, scale 16 -> map
                // coords below.
                var okHeights = true;
                okHeights &= FloorAt(world, -176, 80) == 48;   // low wall top
                okHeights &= FloorAt(world, 160, 64) == 40;    // desk top
                for (var step = 1; step <= 11; step++)
                {
                    var sx = 84 + 184.0 * (step - 0.5) / 11;   // center of step's tread
                    okHeights &= FloorAt(world, sx, 160) == 8 * step;
                }
                Check(okHeights, "low wall, desk and all 11 stair treads have the expected floor heights");

                // The window at (30, 30)ft -> map (160, 240): its sector spans sill
                // 48 to head 112 - the see-through slit in the north wall.
                var windowSubsector = Geometry.PointInSubsector(Fixed.FromDouble(160), Fixed.FromDouble(240), world.Map);
                Check(windowSubsector.Sector.FloorHeight.Data >> 16 == 48 &&
                      windowSubsector.Sector.CeilingHeight.Data >> 16 == 112,
                    "window sector spans sill 48 to head 112");

                // Cross-check PointInSubsector against the same random points the
                // structural walk used - it must resolve without throwing to a real
                // sector every time (points inside a doorway legitimately land in
                // that door's sector rather than the main one).
                var rng = new Random(7);
                var ok = true;
                for (var i = 0; i < 500; i++)
                {
                    var px = minX + rng.NextDouble() * (maxX - minX);
                    var py = minY + rng.NextDouble() * (maxY - minY);
                    var ss = Geometry.PointInSubsector(Fixed.FromDouble(px), Fixed.FromDouble(py), world.Map);
                    ok &= ss != null && ss.Sector != null && Array.IndexOf(world.Map.Sectors, ss.Sector) >= 0;
                }
                Check(ok, "engine PointInSubsector resolves 500 random points to a real sector");
            }
        }

        private static int FloorAt(World world, double x, double y)
        {
            var subsector = Geometry.PointInSubsector(Fixed.FromDouble(x), Fixed.FromDouble(y), world.Map);
            return subsector.Sector.FloorHeight.Data >> 16;
        }

        /// <summary>
        /// The minimal resource WAD GameContent.CreateDummy needs: PLAYPAL and
        /// COLORMAP (raw bytes), a TEXTURE1 with every wall texture name the
        /// generator can emit (DummyTextureLookup reads name at +0 and height at
        /// +14 of each record), and F_START/F_END-bracketed 4096-byte flats
        /// including the F_SKY1 the flat lookup insists on.
        /// </summary>
        private static void WriteStubResourceWad(string path)
        {
            var lumps = new List<Tuple<string, byte[]>>();
            lumps.Add(Tuple.Create("PLAYPAL", new byte[14 * 768]));
            lumps.Add(Tuple.Create("COLORMAP", new byte[34 * 256]));
            lumps.Add(Tuple.Create("TEXTURE1", BuildTexture1(
                "STARTAN3", "STONE2", "BROWN1", "SLADWALL", "BROWNGRN",
                "BIGDOOR2", "DOORTRAK", "SW1COMP", "SW2COMP", "SUPPORT2", "STEP2",
                "SKY1", "SKY2", "SKY3", "SKY4")));
            lumps.Add(Tuple.Create("F_START", new byte[0]));
            lumps.Add(Tuple.Create("FLOOR4_8", new byte[4096]));
            lumps.Add(Tuple.Create("CEIL3_5", new byte[4096]));
            lumps.Add(Tuple.Create("F_SKY1", new byte[4096]));
            // ThreeDRenderer.InitWindowBorder loads these two unconditionally.
            lumps.Add(Tuple.Create("GRNROCK", new byte[4096]));
            lumps.Add(Tuple.Create("FLOOR7_2", new byte[4096]));
            lumps.Add(Tuple.Create("F_END", new byte[0]));

            // ...and these eight, as real (tiny) patches.
            foreach (var name in new[] { "BRDR_TL", "BRDR_TR", "BRDR_BL", "BRDR_BR", "BRDR_T", "BRDR_B", "BRDR_L", "BRDR_R" })
            {
                lumps.Add(Tuple.Create(name, BuildMinimalPatch()));
            }

            // Sprite lumps: DummySpriteLookup only cares about the NAMES between
            // the S_START/S_END markers (every patch becomes DummyData.GetPatch()),
            // so one-byte lumps (EnumerateSprites skips zero-size ones) named
            // <sprite><frame>0 give every sprite 26 frames with all 8 rotations -
            // enough for any state an item, monster or weapon can animate into.
            lumps.Add(Tuple.Create("S_START", new byte[0]));
            for (var i = 0; i < (int)Sprite.Count; i++)
            {
                for (var frame = 'A'; frame <= 'Z'; frame++)
                {
                    lumps.Add(Tuple.Create(DoomInfo.SpriteNames[i] + frame + "0", new byte[1]));
                }
            }
            lumps.Add(Tuple.Create("S_END", new byte[0]));

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Encoding.ASCII.GetBytes("IWAD"));
                w.Write(lumps.Count);
                w.Write(0);

                var positions = new int[lumps.Count];
                for (var i = 0; i < lumps.Count; i++)
                {
                    positions[i] = (int)stream.Position;
                    w.Write(lumps[i].Item2);
                }

                var dir = (int)stream.Position;
                for (var i = 0; i < lumps.Count; i++)
                {
                    w.Write(positions[i]);
                    w.Write(lumps[i].Item2.Length);
                    var name = new byte[8];
                    var ascii = Encoding.ASCII.GetBytes(lumps[i].Item1);
                    Array.Copy(ascii, name, Math.Min(8, ascii.Length));
                    w.Write(name);
                }

                stream.Seek(8, SeekOrigin.Begin);
                w.Write(dir);
            }
        }

        /// <summary>A well-formed 1x1 Doom picture-format patch: header (width,
        /// height, offsets), one column offset, one column (topdelta 0, length 1,
        /// pad, pixel, pad, 0xFF terminator).</summary>
        private static byte[] BuildMinimalPatch()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)1);  // width
                w.Write((short)1);  // height
                w.Write((short)0);  // left offset
                w.Write((short)0);  // top offset
                w.Write(12);        // columnofs[0]
                w.Write((byte)0);   // topdelta
                w.Write((byte)1);   // length
                w.Write((byte)0);   // pad
                w.Write((byte)0);   // pixel
                w.Write((byte)0);   // pad
                w.Write((byte)0xFF); // end of column
                w.Flush();
                return ms.ToArray();
            }
        }

        private static byte[] BuildTexture1(params string[] names)
        {
            // maptexture_t layout: name[8], masked int32, width int16, height
            // int16, columndirectory int32, patchcount int16 (0 - the dummy lookup
            // never composes patches).
            const int recordSize = 22;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(names.Length);
                for (var i = 0; i < names.Length; i++)
                {
                    w.Write(4 + 4 * names.Length + recordSize * i);
                }
                foreach (var name in names)
                {
                    var bytes = new byte[8];
                    var ascii = Encoding.ASCII.GetBytes(name);
                    Array.Copy(ascii, bytes, Math.Min(8, ascii.Length));
                    w.Write(bytes);
                    w.Write(0);          // masked
                    w.Write((short)64);  // width
                    w.Write((short)128); // height
                    w.Write(0);          // columndirectory
                    w.Write((short)0);   // patchcount
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
