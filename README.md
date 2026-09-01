# DoomInDynamo

A Dynamo package that adds two nodes:

- **Doom Player** - runs a real, playable copy of Doom on the node's face inside
  Dynamo (Sandbox or Dynamo for Revit).
- **RevitToWad.Export** - converts the current Revit document's walls into a
  playable Doom map (a PWAD) with randomly scattered items and monsters. Wire its
  `wadPath` output into Doom Player's `pwad` input and walk your own building.

It works by embedding [ManagedDoom](https://github.com/sinshu/managed-doom) (a
C# port of the original Doom source release) directly in a `NodeModel` + WPF
custom node view: the engine's software rasterizer writes into a `WriteableBitmap`
each frame, and keyboard input is captured by the node's own WPF control (not a
global hook) so it doesn't fight Dynamo's canvas shortcuts.

## Status / what actually works

- Full single-player Doom: movement, shooting, the automap, the menu, level
  transitions - anything the original engine supports.
- Sound effects (gunfire, doors, monsters, pickups - straight from the WAD).
  Background MIDI music is opt-in - see [Sound](#sound) below.
- Mouse look, horizontal only - see [Mouse](#mouse) below for why there's no
  vertical look, and it's not just left out of laziness.
- Built and compiled against a real local Revit 2027 / Dynamo-for-Revit install
  (.NET 10, `net10.0-windows`) - see [Building](#building).

## Getting a WAD file

Doom's levels, textures, and sounds live in a separate `.wad` file that is **not**
included in this repo and never will be - it's licensed game data, not source
code. You need to supply your own, e.g.:

- The free shareware `doom1.wad` (episode 1 only), distributed by id Software
  themselves since 1993 - search for "doom1.wad shareware download" and get it
  from a source you trust.
- `DOOM.WAD` / `DOOM2.WAD` from a copy of Doom or Doom II you own (Steam, GOG,
  or the original CD).
- Any other IWAD/PWAD you're legally entitled to use (Freedoom, Heretic, etc. -
  ManagedDoom targets vanilla Doom's data format, so mileage on other games
  will vary).

The node has a **Browse WAD...** button - point it at the file each time you
add the node, no config file editing needed.

## Using the node

1. Add a **Doom Player** node (category `DoomInDynamo`) to the canvas.
2. Click **Browse WAD...** and pick your `.wad` file.
3. Click **Start**.
4. Click into the black screen area to give it keyboard focus, then:
   - `W A S D` / arrow keys - move
   - Mouse - turn left/right (once you're actually in a level - see [Mouse](#mouse))
   - `Left Alt` - fire
   - `Space` - use / open doors
   - `Tab` - automap (Doom's built-in minimap: your floor plan, live; `+`/`-`
     zoom, `F` follow mode, `G` grid)
   - `1`-`7` - switch weapon
   - `Esc` - menu (also lets you quit back to idle, and lets go of the mouse)
5. Click **Stop** to pause the engine (e.g. before saving a large graph) - it
   fully tears down the running session rather than just hiding the view.

The node's one output port emits `"running"` or `"idle"` if you want to wire
something (a Watch node, a conditional, whatever) off its state. Its optional
`pwad` input takes a path to a map WAD (e.g. from **RevitToWad.Export**) that is
loaded on top of the browsed IWAD via the engine's `-file` mechanism - the PWAD's
map lumps override the IWAD's, so starting a new game drops you into the custom map.

## Revit -> WAD: play your building

Add a **RevitToWad.Export** node (same `DoomInDynamo` category, only functional
in Dynamo *for Revit* - in Sandbox it politely reports that it needs Revit). On
run it reads the active Revit document and writes a PWAD:

- **Walls become solid Doom walls.** Each wall's centerline (curved walls are
  tessellated) is extruded by its real thickness into a solid "pillar" standing
  in one big sector, at 16 map units per foot - a 3 ft door reads as a proper
  48-unit Doom doorway. Oversized sites are auto-scaled down to fit Doom's
  16-bit coordinate space.
- **Doors become doors.** Any door hosted on a wall cuts a door-width gap in
  it, and a real working Doom door stands in the gap: a closed slab (classic
  "DR" type 1 - BIGDOOR2 face, DOORTRAK jambs) that opens when you press
  Space on either side, waits, and closes again. Monsters can open them too,
  exactly like the originals. Rooms connect where the architect said they
  should - but you still have to open the door.
- **Windows become windows.** Hosted windows cut an opening between their
  real sill and head heights: you (and monsters) see through it, hitscan and
  projectiles fly through it, but nobody walks through it - the "glass" is a
  blocking flag on the opening's two-sided lines, which vanilla Doom's
  movement code respects while its bullets ignore. A window that overlaps a
  doorway defers to the door, and ribbon glazing merges into one opening.
- **Real heights.** The room ceiling comes from the length-weighted *median*
  wall height (one tall atrium wall doesn't raise the whole roof), and walls
  meaningfully shorter than it become **low walls** at their true geometric
  height - you see and shoot over them, and step over anything curb-height.
- **Stairs and furnishings.** Stair flights become sequences of climbable
  steps (risers clamped under Doom's 24-unit step limit); furniture and
  casework become blocks you can see over; columns become solid pillars.
  All approximated from bounding boxes - a rotated sofa gets a slightly
  generous footprint, and spiral stairs are skipped (no run direction to
  infer from a square box).
- **A findable exit.** A switch-faced pedestal stands just outside the
  building's north-east corner - press Space on any side of it to finish the
  level (the automap, `Tab`, makes it easy to spot).
- **Which level - or all of them?** By default the Revit level with the most
  walls; pass `levelName` to pick another, or pass `"*"` to export the WHOLE
  model: Doom's engine is 2.5D with no room-over-room, so the storeys can't
  stack - instead every level becomes its own cluster, laid out west-to-east
  (lowest level first) with walkable 30 ft gaps between them, all inside one
  boundary. One map, the entire building, exploded-axon style. (A multi-storey
  stair still exports just its first climbable rise.)
- **Curtain walls are glass.** Glazed panels export as see-through window
  bands (adjacent panels weld into ribbons), curtain wall *doors* work like
  any other door - panels have no location point or level of their own, so
  position, width and storey come from the panel's bounding box and
  grid-driven Width. Spandrel panels can't be told apart from glazing, so
  they read as glass too.
- **Random items.** `seed`-driven placement of health, armor, ammo, weapons,
  barrels - and monsters unless `includeMonsters` is false (all of it is
  rejection-sampled to stay out of walls; every thing type used exists even in
  shareware Doom 1). Change `seed` to reshuffle, `itemCount` (default 75) to
  crowd or thin the map.
- **Both map slots.** The file contains the same map as `E1M1` *and* `MAP01`,
  so it works whether the IWAD you browse in Doom Player is Doom 1-family or
  Doom 2-family (including Freedoom).
- **Outputs**: `wadPath` (feed this to Doom Player's `pwad` input; empty input
  writes to `%TEMP%\DoomInDynamo\<document>.wad`) and `report` (what was
  exported, how many things were placed, and one hint: an exit switch hides
  somewhere on the outer boundary - hug the edge and press Space).
- **Common mix-up**: don't point Doom Player's **Browse WAD...** button at the
  generated file - it's a map-only PWAD with no textures/sprites/palette in it
  (the engine would fail with "The lump 'PLAYPAL' was not found"; the node now
  catches this and says so). Browse a real IWAD there, wire `wadPath` into the
  `pwad` *input port*, run the graph, then Start.

Because ManagedDoom is a faithful vanilla port it builds nothing at load time -
the exporter therefore writes *complete* maps: real BSP nodes (SEGS, SSECTORS,
NODES built by its own recursive seg partitioner), a real collision BLOCKMAP,
and a REJECT table, not just the editing lumps. Windows stay solid, curtain
walls export at a fallback thickness, and overlapping walls simply overlap -
Doom doesn't mind.

## Sound

Sound effects work out of the box - they're read straight from the WAD's own
`DSxxxx` lumps, no extra assets needed. If OpenAL can't initialize for some
reason (no audio device on the machine, native library not found, etc.),
`DoomSession` catches that and falls back to silent rather than failing the
whole session - check the status line after clicking Start, it reports which
happened (`Running (audio: sfx+music)` / `sfx only (no soundfont)` / `off (...)`).

Background MIDI music needs a General MIDI soundfont file, which - like the
WAD - isn't bundled here (see `Config.audio_soundfont`, defaults to expecting
a file literally named `TimGM6mb.sf2`; see `licenses/LICENSE_TimGM6mb.txt` in
the upstream ManagedDoom repo for what that specific one's license requires).
To enable it: drop a `.sf2` file with that name next to `DoomInDynamo.dll` in
the installed package's `bin\` folder. Nothing else to configure - `DoomSession`
picks it up automatically next time you click Start.

## Mouse

The mouse turns you left/right. That's it - there's no vertical look, and that's
not a corner we cut: vanilla Doom's engine has no true camera pitch at all. Its
renderer is a fixed-height software rasterizer with no concept of looking up or
down (that's why the original game could get away with rendering so fast on
1993 hardware) - source ports that add real mouselook (e.g. Doom Retro, GZDoom)
do it by extending the renderer itself, which ManagedDoom (and so this node)
doesn't do. Vanilla Doom did use the mouse's vertical movement for something -
walking forward/backward - but that's a confusing legacy behavior most players
don't expect from a "look around" mouse, so `WpfUserInput` doesn't wire it to
anything; forward/back stays on `W`/`S`.

Mouse capture follows the actual game state, exactly like the original: as soon
as you're in a level with the menu closed, the cursor hides and locks to the
node (a "recenter every move" trick - see `DoomPlayerView.OnPreviewMouseMove` -
since there's no OS-level infinite-cursor mode inside a small windowed control).
Opening the menu (`Esc`) releases it back to a normal, visible, clickable
cursor immediately, and clicking **Stop** or navigating away always releases it
too, even if something goes wrong mid-session.

## Controls note: fire is Alt, not Ctrl

Vanilla Doom's default fire key is Ctrl; this node rebinds it to Alt instead
(`DoomSession` overrides `config.key_fire` after construction). Using Alt as a
game key needed one extra fix: WPF reports Alt as `Key.System` with the actual
key stashed in `e.SystemKey` instead - a well-known gotcha.
`DoomPlayerView.ResolveKey` unwraps it, and the same handler marks the event
`Handled` so Alt doesn't also trigger Windows/Revit's usual "show menu access
keys" behavior while you're playing.

## Architecture

```
DoomPlayerNodeModel        <- Dynamo.Graph.Nodes.NodeModel; just the WadPath
  (Nodes/)                    property + one output port. No ManagedDoom
                               dependency at all.

DoomPlayerNodeViewCustomization  <- Dynamo.Wpf.INodeViewCustomization<T>;
  (UI/)                            Dynamo auto-discovers this and attaches
                                    DoomPlayerView to the node's ContentGrid.

DoomPlayerView (WPF UserControl)  <- Owns a DoomSession, pumps it from
  (UI/)                              CompositionTarget.Rendering (runs on the
                                      WPF UI thread - no background thread,
                                      no cross-thread bitmap hazards), captures
                                      keyboard input scoped to itself.

DoomSession                 <- Thin facade over ManagedDoom's Doom/Config/
  (Engine/)                    GameContent/Renderer types. This is the only
                                place that touches ManagedDoom.Doom directly.

WpfVideo : IVideo            <- Drives the same Renderer class ManagedDoom's own
  (Engine/)                     SilkVideo uses, but into a plain byte[] instead
                                 of an OpenGL texture.

WpfUserInput : IUserInput     <- Reads a HashSet<DoomKey> that DoomPlayerView's
  (Engine/)                     PreviewKeyDown/Up handlers maintain, instead of
                                 SilkUserInput's live Silk.NET keyboard polling.
```

The exporter lives in a second, deliberately WPF-free assembly:

```
DoomInDynamo.Functions      <- zero-touch node library (also in pkg.json's
  (src/DoomInDynamo.Functions)  node_libraries). One public class, RevitToWad -
                                Dynamo imports exactly one node from it.

  Revit\RevitExtractor       <- the ONLY file touching Autodesk.Revit.DB /
                                RevitServices: walls -> centerline segments
                                (door gaps pre-cut, plus a DoorOpening record
                                per gap), rooms -> placement hints.
                                No Revit type appears in any signature, so the
                                assembly still imports cleanly in Sandbox where
                                RevitAPI.dll doesn't exist; the node just
                                reports "needs Dynamo for Revit" when run there.

  WadGen\MapBuilder          <- feet -> map units, wall pillars + sealed outer
                                boundary + sector, seeded random thing placement.
  WadGen\BspBuilder          <- recursive seg partitioner emitting vanilla
                                SEGS/SSECTORS/NODES (front = right side of the
                                partition, exactly matching Geometry.PointOnSide).
  WadGen\BlockmapBuilder     <- collision grid, offsets aimed at each list's
                                first real entry (ManagedDoom reads from the
                                offset target itself).
  WadGen\WadWriter           <- PWAD serialization, E1M1 + MAP01 slots.
```

`tools/WadSmokeTest/` is a console harness (never packaged) that runs the whole
pipeline on a synthetic building and then boots the *real* engine on the result -
`GameContent.CreateDummy` plus a stub resource WAD stands in for an IWAD - and
simulates ~500 tics of a player charging around while monsters wake up, plus
structural validation of every lump. `dotnet run --project tools\WadSmokeTest`
should end with `ALL CHECKS PASSED`.

`vendor/ManagedDoom/` holds a trimmed copy of the upstream engine (platform
code stripped out - see `vendor/ManagedDoom/VENDOR_NOTICE.md` for exactly what
was kept/dropped and why); `vendor/ManagedDoom.Engine/` is the class library
project that compiles it.

### Why WPF input capture instead of a global keyboard hook

An earlier sketch of this idea (see the design notes this repo was built from)
suggested hooking `user32.dll`'s `GetAsyncKeyState` to read input regardless of
focus, to work around Dynamo's canvas swallowing keyboard shortcuts. This repo
does it differently: `DoomPlayerView`'s `Image` control captures
`PreviewKeyDown`/`PreviewKeyUp` (and marks them `Handled`) only while it
actually has WPF keyboard focus - click into the screen first. That's enough to
stop Dynamo's shortcuts from firing without reading keystrokes typed anywhere
else in the process.

### Frame buffer transpose + channel order

ManagedDoom's `Renderer` writes pixels **column-major** (`index = ScreenHeight *
x + y` - see `DrawScreen.Data`), and its `Palette` packs colors as `r | g<<8 |
b<<16 | 255<<24`, which on this little-endian machine lands in memory as
**R,G,B,A** byte order. WPF's `WriteableBitmap` wants row-major **B,G,R,A**
(`PixelFormats.Bgra32`). `DoomPlayerView.CopyTransposedToBgra` does both fixes
in one pass. Get either one wrong and it still "works" - you just get a
diagonally-sheared or red/blue-swapped image - so this was verified by reading
`Renderer.WriteData`/`Palette.ResetColors` directly rather than assumed.

## Not included (yet)

- **Frame interpolation.** Runs at Doom's native 35 tics/sec 1:1 with render
  frames (`Fixed.One` passed to `Renderer.Render`) rather than ManagedDoom's
  optional sub-tic interpolation (`video_fpsscale`) - simpler, and 35fps is how
  the original game actually ran.
- **Verified `.dyn` save/reload of the WAD path.** `WadPath` persists through
  undo/redo and copy/paste for certain (`SerializeCore`/`DeserializeCore`,
  which this Dynamo build's own doc comments confirm are still exercised for
  exactly that). Whether it round-trips through a full **File > Save** of the
  graph wasn't verified against a running Dynamo Sandbox session - if it
  doesn't, re-Browse the WAD after reopening the graph.

## Building

Requires the .NET 10 SDK and a local Revit 2027 (or newer, matching Dynamo)
install - this repo references `DynamoCore.dll`, `DynamoCoreWpf.dll`,
`ProtoCore.dll`, `DynamoServices.dll`, and `Newtonsoft.Json.dll` directly from
`<Revit install>\AddIns\DynamoForRevit\`, since there's no public NuGet package
for this SDK yet. If your install lives somewhere other than
`C:\Program Files\Autodesk\Revit 2027`, override it:

```powershell
$env:RevitInstallDir = "D:\Autodesk\Revit 2027"
dotnet build DoomInDynamo.slnx
```

To build and stage an installable package folder under `dist\DoomInDynamo\`:

```powershell
.\build\Pack-Package.ps1
```

That script only writes under `.\dist` - see the comment at its top for the
one-line `Copy-Item` to actually install it into your Dynamo packages folder
(on this machine, Revit 2027's Dynamo build reads user packages from
`%AppData%\Dynamo\Dynamo Revit\27.0\packages`).

## License

This package links directly against ManagedDoom, which is GPLv2 (itself
derived from id Software's 1997 GPL release of the Doom source). That makes
**this whole repository GPLv2 as a combined work** if you distribute it -
see `LICENSE` and `vendor/ManagedDoom/VENDOR_NOTICE.md`. Running it yourself,
modified or not, is unrestricted; if you publish the package or redistribute
binaries, keep the license/notice files intact and make source available.
