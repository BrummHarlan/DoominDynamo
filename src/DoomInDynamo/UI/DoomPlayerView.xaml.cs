using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoomInDynamo.Engine;
using DoomInDynamo.Nodes;
using ManagedDoom;

namespace DoomInDynamo.UI
{
    /// <summary>
    /// Hosts one running DoomSession on the node's face: a WriteableBitmap pumped by
    /// CompositionTarget.Rendering (runs on the WPF UI thread - Doom's simulation is
    /// cheap enough that there's no need for a background thread, and this sidesteps
    /// any cross-thread WriteableBitmap/Dispatcher hazards inside Revit's process),
    /// plus keyboard capture scoped to this control so Dynamo's canvas shortcuts
    /// don't fire while the player is driving Doom.
    /// </summary>
    public partial class DoomPlayerView : UserControl, IDisposable
    {
        private readonly DoomPlayerNodeModel model;
        private DoomSession session;
        private WriteableBitmap bitmap;
        private byte[] rowMajorBuffer;
        private bool running;
        private readonly Stopwatch clock = new Stopwatch();
        private double ticAccumulatorSeconds;
        private string lastKeyInfo = "(none yet)";

        public DoomPlayerView(DoomPlayerNodeModel model)
        {
            this.model = model;
            InitializeComponent();

            if (!string.IsNullOrEmpty(model.WadPath))
            {
                WadPathText.Text = model.WadPath;
                StartStopButton.IsEnabled = true;
            }

            BrowseButton.Click += OnBrowseClick;
            StartStopButton.Click += OnStartStopClick;

            // Dynamo's own canvas has PreviewKeyDown handling higher up the visual
            // tree (arrow-key nudge, Escape, Space-bar shortcuts, etc.), which tunnels
            // - and therefore runs - before this control sees the event. A plain "+="
            // subscription is skipped once an ancestor sets e.Handled = true, so
            // keys would silently never reach the game; handledEventsToo: true makes
            // sure they always do.
            ScreenImage.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
            ScreenImage.AddHandler(PreviewKeyUpEvent, new KeyEventHandler(OnPreviewKeyUp), true);

            // Plain Focus() + a bubbling MouseLeftButtonDown handler wasn't enough -
            // Dynamo's own canvas almost certainly has node-selection/drag handling
            // on mouse-down that runs right after this in the bubble route and steals
            // focus/capture back. Use PreviewMouseLeftButtonDown (tunnels in - runs
            // before any of that), force keyboard focus explicitly, and mark it
            // Handled so the click doesn't reach the canvas's own handling at all.
            ScreenImage.AddHandler(PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnScreenMouseLeftButtonDown), true);
            ScreenImage.LostKeyboardFocus += (s, e) =>
            {
                session?.ReleaseAllKeys();
                ReleaseMouseGrabVisuals();
            };
            ScreenImage.PreviewMouseMove += OnPreviewMouseMove;
        }

        private void OnScreenMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.Focus(ScreenImage);
            ScreenImage.Focus();
            e.Handled = true;
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Doom WAD files (*.wad)|*.wad|All files (*.*)|*.*",
                Title = "Choose a WAD file you own the rights to use"
            };

            // Microsoft.Win32.OpenFileDialog.ShowDialog() only accepts a
            // System.Windows.Window as owner, and this control isn't hosted inside
            // one when docked in Revit - without an owner HWND the picker can pop up
            // with no owner at all and land behind Revit's main window, looking like
            // the whole thing hung. System.Windows.Forms.OpenFileDialog takes a raw
            // IWin32Window instead, so we can hand it this control's actual HWND.
            var owner = Win32Window.FromVisual(this);
            var result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();

            if (result == System.Windows.Forms.DialogResult.OK)
            {
                model.WadPath = dialog.FileName;
                WadPathText.Text = dialog.FileName;
                StartStopButton.IsEnabled = true;
                StatusText.Text = "Idle";
            }
        }

        private sealed class Win32Window : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; }

            private Win32Window(IntPtr handle)
            {
                Handle = handle;
            }

            public static Win32Window FromVisual(Visual visual)
            {
                var source = PresentationSource.FromVisual(visual) as HwndSource;
                return source == null ? null : new Win32Window(source.Handle);
            }
        }

        private void OnStartStopClick(object sender, RoutedEventArgs e)
        {
            if (running)
            {
                StopGame("Stopped");
            }
            else
            {
                StartGame();
            }
        }

        /// <summary>Scans a WAD's directory for a lump name (case-insensitive).
        /// Returns true on any parse trouble - this is a pre-flight nicety, and
        /// genuinely broken files should get the engine's own error, not ours.</summary>
        private static bool WadContainsLump(string path, string lumpName)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    var identification = new string(reader.ReadChars(4));
                    if (identification != "IWAD" && identification != "PWAD")
                    {
                        return true;
                    }

                    var lumpCount = reader.ReadInt32();
                    var directoryOffset = reader.ReadInt32();
                    if (lumpCount < 0 || lumpCount > 65536 ||
                        directoryOffset < 0 || directoryOffset + 16L * lumpCount > stream.Length)
                    {
                        return true;
                    }

                    stream.Seek(directoryOffset, SeekOrigin.Begin);
                    for (var i = 0; i < lumpCount; i++)
                    {
                        reader.ReadInt32(); // position
                        reader.ReadInt32(); // size
                        var nameBytes = reader.ReadBytes(8);
                        var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                        if (string.Equals(name, lumpName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
            catch
            {
                return true;
            }
        }

        private void StartGame()
        {
            if (string.IsNullOrWhiteSpace(model.WadPath) || !File.Exists(model.WadPath))
            {
                StatusText.Text = "WAD file not found.";
                return;
            }

            // The optional "pwad" input arrives via the model's DataBridge callback
            // (see DoomPlayerNodeModel.PwadPath). Reading it here, at Start time, is
            // the simple-and-correct order of operations: the user runs the graph
            // (which evaluates the input and bridges it back) and then clicks Start -
            // so no PwadPathChanged subscription is needed, and there's nothing to
            // unsubscribe in StopGame/Dispose either.
            var pwad = model.PwadPath;
            if (string.IsNullOrWhiteSpace(pwad))
            {
                pwad = null;
            }
            else if (!File.Exists(pwad))
            {
                // Fail loudly before touching the engine: GameContent would throw a
                // far less helpful error, and silently dropping the map would look
                // like the export never worked.
                StatusText.Text = "PWAD not found: " + pwad;
                return;
            }

            // The classic mix-up: browsing a map-only PWAD (e.g. the RevitToWad
            // export) as the main WAD. The engine would only say "The lump 'PLAYPAL'
            // was not found" - PLAYPAL is the palette every real IWAD carries and
            // map-only files don't - so catch it here with an actionable message.
            if (!WadContainsLump(model.WadPath, "PLAYPAL"))
            {
                StatusText.Text = "That file is a map-only PWAD (no game data in it). Browse a real IWAD "
                    + "here (doom1.wad, DOOM.WAD, DOOM2.WAD, Freedoom) and wire the generated map "
                    + "into the node's pwad input instead.";
                return;
            }

            try
            {
                session = new DoomSession(model.WadPath, pwad);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to load: " + ex.Message;
                session = null;
                return;
            }

            bitmap = new WriteableBitmap(session.ScreenWidth, session.ScreenHeight, 96, 96, PixelFormats.Bgra32, null);
            rowMajorBuffer = new byte[4 * session.ScreenWidth * session.ScreenHeight];
            ScreenImage.Source = bitmap;

            clock.Restart();
            ticAccumulatorSeconds = 0;

            running = true;
            StartStopButton.Content = "Stop";
            var runningStatus = "Running (audio: " + session.AudioStatus + ")";
            if (pwad != null)
            {
                runningStatus += " | map: " + Path.GetFileName(pwad);
            }

            StatusText.Text = runningStatus;

            session.MouseGrabChanged += OnMouseGrabChanged;

            CompositionTarget.Rendering += OnCompositionRendering;
            ScreenImage.Focus();
        }

        private void StopGame(string status)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            running = false;
            StartStopButton.Content = "Start";
            StatusText.Text = status;

            ReleaseMouseGrabVisuals();

            session?.Dispose();
            session = null;
        }

        /// <summary>
        /// ManagedDoom grabs the mouse automatically while in a level with the menu
        /// closed, and releases it otherwise (see Doom.CheckMouseState(), driven by
        /// DoomSession.IsMouseGrabbed) - exactly like vanilla Doom. This hides the
        /// cursor and captures it to ScreenImage so it can't wander onto the rest of
        /// the Dynamo canvas while playing; OnPreviewMouseMove does the actual
        /// recenter-every-move trick that turns that into an unbounded look control.
        /// </summary>
        private void OnMouseGrabChanged()
        {
            if (session == null)
            {
                return;
            }

            if (session.IsMouseGrabbed)
            {
                ScreenImage.Cursor = Cursors.None;
                Mouse.Capture(ScreenImage, CaptureMode.Element);
                WarpCursorToCenter();
            }
            else
            {
                ReleaseMouseGrabVisuals();
            }
        }

        private void ReleaseMouseGrabVisuals()
        {
            if (Mouse.Captured == ScreenImage)
            {
                Mouse.Capture(null);
            }

            ScreenImage.ClearValue(CursorProperty);
        }

        private void WarpCursorToCenter()
        {
            if (ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0)
            {
                return;
            }

            var center = new Point(ScreenImage.ActualWidth / 2, ScreenImage.ActualHeight / 2);
            var screenCenter = ScreenImage.PointToScreen(center);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)screenCenter.X, (int)screenCenter.Y);
        }

        /// <summary>
        /// While the mouse is grabbed, every move is measured against the control's
        /// center and then the OS cursor is warped straight back to that center -
        /// the standard trick for unbounded "look" input inside a small windowed
        /// area (no raw-input APIs, no OS-level infinite-cursor mode needed). The
        /// warp is what makes the next move's delta-from-center measurement correct.
        /// </summary>
        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (session == null || !session.IsMouseGrabbed)
            {
                return;
            }

            var center = new Point(ScreenImage.ActualWidth / 2, ScreenImage.ActualHeight / 2);
            var pos = e.GetPosition(ScreenImage);
            var dx = pos.X - center.X;
            var dy = pos.Y - center.Y;

            if (dx != 0 || dy != 0)
            {
                session.AddMouseDelta(dx, dy);
                WarpCursorToCenter();
            }

            e.Handled = true;
        }

        private void OnCompositionRendering(object sender, EventArgs e)
        {
            if (session == null)
            {
                return;
            }

            // Never let an exception escape this handler: CompositionTarget.Rendering
            // runs on Revit's own UI-thread message loop, and an unhandled exception
            // there can surface as a Revit-level error/task dialog that ends up parented
            // wrong (seen behind the main window, unclickable) instead of a normal WPF
            // crash - which looks exactly like a silent freeze. Catch it, show it in
            // StatusText, and stop cleanly instead.
            try
            {
                var elapsed = clock.Elapsed.TotalSeconds;
                clock.Restart();
                ticAccumulatorSeconds += elapsed;

                const double secondsPerTic = 1.0 / DoomSession.TicsPerSecond;

                // Cap catch-up so a debugger pause or a Revit UI-thread hiccup doesn't
                // make the game visibly fast-forward once it gets a chance to run again.
                var maxBacklog = 10 * secondsPerTic;
                if (ticAccumulatorSeconds > maxBacklog)
                {
                    ticAccumulatorSeconds = maxBacklog;
                }

                var quit = false;
                while (ticAccumulatorSeconds >= secondsPerTic)
                {
                    ticAccumulatorSeconds -= secondsPerTic;
                    if (!session.Tick())
                    {
                        quit = true;
                        break;
                    }
                }

                if (quit)
                {
                    StopGame("Quit");
                    return;
                }

                var frame = session.RenderFrame();
                CopyTransposedToBgra(frame, rowMajorBuffer, session.ScreenWidth, session.ScreenHeight);

                bitmap.Lock();
                Marshal.Copy(rowMajorBuffer, 0, bitmap.BackBuffer, rowMajorBuffer.Length);
                bitmap.AddDirtyRect(new Int32Rect(0, 0, session.ScreenWidth, session.ScreenHeight));
                bitmap.Unlock();

                DebugText.Text = session.DebugStatus + " | focused: " + ScreenImage.IsKeyboardFocused + " | last key: " + lastKeyInfo;
            }
            catch (Exception ex)
            {
                StopGame("Error: " + ex.Message);
            }
        }

        /// <summary>
        /// ManagedDoom's Renderer writes pixels column-major - index = ScreenHeight * x + y,
        /// see DrawScreen.Data / Renderer.WriteData - i.e. transposed relative to the
        /// row-major layout WriteableBitmap expects (index = width * y + x). Its palette
        /// (Palette.ResetColors) also packs colors as (r | g&lt;&lt;8 | b&lt;&lt;16 | 255&lt;&lt;24), which
        /// in memory on this little-endian machine is R,G,B,A byte order - WPF's Bgra32
        /// wants B,G,R,A, so red and blue are swapped here too.
        /// </summary>
        private static void CopyTransposedToBgra(byte[] source, byte[] destination, int width, int height)
        {
            for (var x = 0; x < width; x++)
            {
                var srcBase = height * x;
                for (var y = 0; y < height; y++)
                {
                    var srcPixel = (srcBase + y) * 4;
                    var dstPixel = (width * y + x) * 4;
                    var r = source[srcPixel];
                    var g = source[srcPixel + 1];
                    var b = source[srcPixel + 2];
                    var a = source[srcPixel + 3];
                    destination[dstPixel] = b;
                    destination[dstPixel + 1] = g;
                    destination[dstPixel + 2] = r;
                    destination[dstPixel + 3] = a;
                }
            }
        }

        // WPF reports Alt (and a few other "system" keys) as e.Key == Key.System with
        // the real key stashed in e.SystemKey instead - a well-known WPF gotcha. Now
        // that Alt is the fire key, this matters: without this, pressing Alt would
        // look like an unmapped key and never reach the game (and WPF would try to
        // do its usual Alt-activates-menu-access-keys thing on top of that).
        private static Key ResolveKey(KeyEventArgs e)
        {
            return e.Key == Key.System ? e.SystemKey : e.Key;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = ResolveKey(e);
            var doomKey = WpfKeyMap.ToDoomKey(key);
            lastKeyInfo = key + " -> " + doomKey;

            if (doomKey == DoomKey.Unknown)
            {
                return;
            }

            session?.KeyDown(doomKey);
            e.Handled = true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            var doomKey = WpfKeyMap.ToDoomKey(ResolveKey(e));
            if (doomKey == DoomKey.Unknown)
            {
                return;
            }

            session?.KeyUp(doomKey);
            e.Handled = true;
        }

        public void Dispose()
        {
            StopGame("Idle");
        }
    }
}
