using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.DesignScript.Runtime;
using DoomInDynamo.WadGen;

namespace DoomInDynamo
{
    /// <summary>
    /// The zero-touch node: Dynamo imports this assembly (it is listed in
    /// pkg.json's node_libraries) and turns the public static method below into the
    /// "RevitToWad.Export" node. Everything else in the assembly is internal on
    /// purpose, so this is the entire node surface.
    /// </summary>
    public static class RevitToWad
    {
        /// <summary>
        /// Converts the current Revit document's walls into a playable Doom map
        /// (PWAD) with randomly placed items and monsters. Wire the resulting
        /// wadPath into the Doom Player node's pwad input, browse a real IWAD
        /// (doom1.wad, DOOM2.WAD, Freedoom, ...), press Start, and walk your
        /// building. Doors become real working Doom doors (press Space to open);
        /// Tab shows your floor plan as the automap; each level exports separately.
        /// </summary>
        /// <param name="wadPath">Where to write the .wad. Empty = a file named after the document under %TEMP%\DoomInDynamo.</param>
        /// <param name="seed">Random seed for item placement - change it to reshuffle the pickups and monsters.</param>
        /// <param name="itemCount">How many items/monsters to scatter (0-500).</param>
        /// <param name="includeMonsters">False = peaceful architecture walkthrough, true = it's Doom.</param>
        /// <param name="levelName">Revit level to export. Empty = the level with the most walls; "*" = the WHOLE model, every level laid out side by side with walkable gaps between the storeys.</param>
        /// <returns>wadPath: the written file (empty on failure); report: what happened.</returns>
        [MultiReturn(new[] { "wadPath", "report" })]
        public static Dictionary<string, object> Export(
            string wadPath = "",
            int seed = 1,
            int itemCount = 75,
            bool includeMonsters = true,
            string levelName = "")
        {
            try
            {
                BuildingModel model;
                try
                {
                    model = Revit.RevitExtractor.ExtractCurrentDocument(levelName ?? "");
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException ||
                    ex is TypeLoadException ||
                    ex is TypeInitializationException ||
                    ex is MissingMethodException ||
                    ex is BadImageFormatException)
                {
                    // The Revit assemblies are reference-only (Private=false) and are
                    // simply absent in Dynamo Sandbox - the first call into
                    // RevitExtractor is where the runtime notices.
                    return Failure("This node reads the current Revit model, so it only works in Dynamo for Revit (Revit's API assemblies are not available here).");
                }

                var path = ResolveOutputPath(wadPath, model.DocumentTitle);
                var report = ExportModel(model, path, seed, itemCount, includeMonsters);

                return new Dictionary<string, object>
                {
                    { "wadPath", path },
                    { "report", report }
                };
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        /// <summary>The Revit-free half of the pipeline, shared with the smoke test
        /// harness (which feeds it synthetic buildings and then boots the real
        /// engine against the result).</summary>
        internal static string ExportModel(BuildingModel model, string path, int seed, int itemCount, bool includeMonsters)
        {
            string report;
            var map = MapBuilder.Build(model, seed, itemCount, includeMonsters, out report);

            // ManagedDoom archives savegames into a fixed 360KB buffer with no
            // bounds checks (SaveAndLoad): ~16 bytes per one-sided linedef (+10
            // more for a second sidedef), 14 per sector, ~158 per mobj. Reject a
            // map that could overflow it when the player presses F2/F6, instead of
            // letting quicksave crash the session mid-game with no warning.
            var twoSided = 0;
            foreach (var line in map.Linedefs)
            {
                if (line.BackSide >= 0)
                {
                    twoSided++;
                }
            }
            var estimatedSaveBytes = 400
                + 14 * map.Sectors.Count
                + 16 * map.Linedefs.Count
                + 10 * twoSided
                + 158 * (map.Things.Count + 64);
            if (estimatedSaveBytes > 360 * 1024)
            {
                throw new InvalidOperationException(
                    "The level is too detailed for the engine's fixed savegame buffer (" +
                    map.Linedefs.Count + " linedefs, " + map.Things.Count +
                    " things). Export a smaller level or reduce itemCount.");
            }

            BspBuilder.Build(map);
            WadWriter.Write(map, path);

            var size = new FileInfo(path).Length;
            return report +
                " Map: " + map.Linedefs.Count + " linedefs, " + map.Subsectors.Count + " subsectors, " +
                map.Nodes.Count + " BSP nodes, " + map.Things.Count + " things; " +
                (size / 1024) + " KB written to " + path + ".";
        }

        private static string ResolveOutputPath(string wadPath, string documentTitle)
        {
            var path = (wadPath ?? "").Trim();
            if (path.Length == 0)
            {
                var name = documentTitle ?? "RevitMap";
                foreach (var bad in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(bad, '_');
                }
                if (name.Trim().Length == 0)
                {
                    name = "RevitMap";
                }
                path = Path.Combine(Path.GetTempPath(), "DoomInDynamo", name + ".wad");
            }

            if (!string.Equals(Path.GetExtension(path), ".wad", StringComparison.OrdinalIgnoreCase))
            {
                path += ".wad";
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return path;
        }

        private static Dictionary<string, object> Failure(string message)
        {
            return new Dictionary<string, object>
            {
                { "wadPath", "" },
                { "report", message }
            };
        }
    }
}
