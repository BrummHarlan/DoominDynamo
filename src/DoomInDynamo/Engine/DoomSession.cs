using System;
using System.IO;
using System.Runtime.InteropServices;
using DrippyAL;
using ManagedDoom;
using ManagedDoom.Audio;
using ManagedDoom.Silk;

namespace DoomInDynamo.Engine
{
    /// <summary>
    /// Owns one running instance of the ManagedDoom engine: WAD/config loading,
    /// the 35-tics-per-second simulation step, and the software-rendered frame
    /// buffer. DoomPlayerView drives this from a CompositionTarget.Rendering pump
    /// and blits <see cref="RenderFrame"/>'s buffer into a WriteableBitmap; it never
    /// touches ManagedDoom types directly.
    /// </summary>
    public sealed class DoomSession : IDisposable
    {
        public const int TicsPerSecond = 35;

        private readonly GameContent content;
        private readonly WpfVideo video;
        private readonly WpfUserInput userInput;
        private readonly Doom doom;
        private readonly AudioDevice audioDevice;
        private readonly SilkSound sound;
        private readonly SilkMusic music;
        private bool disposed;

        public string AudioStatus { get; }

        public DoomSession(string wadPath)
            : this(wadPath, null)
        {
        }

        public DoomSession(string wadPath, string pwadPath)
        {
            if (string.IsNullOrWhiteSpace(wadPath))
            {
                throw new ArgumentException("A WAD file path is required.", nameof(wadPath));
            }

            // ManagedDoom's Wad.GetLumpNumber searches lumps last-to-first, so a PWAD
            // passed via -file (loaded after the -iwad) wins any name collision - its
            // map lumps override the IWAD's, which is exactly how vanilla PWADs work.
            var args = string.IsNullOrWhiteSpace(pwadPath)
                ? new CommandLineArgs(new[] { "-iwad", wadPath })
                : new CommandLineArgs(new[] { "-iwad", wadPath, "-file", pwadPath });

            var config = new Config();

            // Shoot with Alt instead of vanilla Doom's default Ctrl.
            config.key_fire = new KeyBinding(new[] { DoomKey.LAlt, DoomKey.RAlt });

            content = new GameContent(args);
            video = new WpfVideo(config, content);
            userInput = new WpfUserInput(config);

            ISound soundBackend = null;
            IMusic musicBackend = null;
            try
            {
                PreloadNativeOpenAl();
                audioDevice = new AudioDevice();

                sound = new SilkSound(config, content, audioDevice);
                soundBackend = sound;

                var soundFontPath = ResolveSoundFontPath(config);
                if (soundFontPath != null)
                {
                    music = new SilkMusic(config, content, audioDevice, soundFontPath);
                    musicBackend = music;
                }

                AudioStatus = soundFontPath != null ? "sfx+music" : "sfx only (no soundfont)";
            }
            catch (Exception ex)
            {
                // Sound effects/music are a nice-to-have, not core to "does it run
                // Doom" - if OpenAL can't initialize here (no audio device, native
                // lib not found, etc.) fall back to silent rather than failing the
                // whole session over it.
                music?.Dispose();
                sound?.Dispose();
                audioDevice?.Dispose();
                music = null;
                sound = null;
                audioDevice = null;
                soundBackend = null;
                musicBackend = null;
                AudioStatus = "off (" + ex.Message + ")";
            }

            doom = new Doom(args, config, content, video, soundBackend, musicBackend, userInput);
        }

        /// <summary>
        /// DrippyAL/Silk.NET.OpenAL's native library (soft_oal.dll, from the
        /// Silk.NET.OpenAL.Soft.Native package) ships under bin/runtimes/win-x64/native/
        /// next to this assembly. That layout is what .NET's native-library probing
        /// expects for a normally-launched app, but this DLL is instead loaded by
        /// Dynamo's package loader inside Revit.exe - so probing may resolve relative
        /// to Revit's own directory instead of ours. Loading it explicitly by full path
        /// once, up front, sidesteps that: once a module with this name is loaded
        /// anywhere in the process, later implicit lookups by the same name resolve to it.
        /// </summary>
        private static void PreloadNativeOpenAl()
        {
            var dir = Path.GetDirectoryName(typeof(DoomSession).Assembly.Location);
            if (dir == null)
            {
                return;
            }

            var candidate = Path.Combine(dir, "runtimes", "win-x64", "native", "soft_oal.dll");
            if (!File.Exists(candidate))
            {
                candidate = Path.Combine(dir, "soft_oal.dll");
            }

            if (File.Exists(candidate))
            {
                NativeLibrary.Load(candidate);
            }
        }

        /// <summary>
        /// Background MIDI music needs a General MIDI soundfont file (config.audio_soundfont,
        /// "TimGM6mb.sf2" by default) that this repo doesn't bundle - same reasoning as not
        /// bundling a WAD (see README). Drop one next to DoomInDynamo.dll yourself to enable
        /// music; sound effects (from the WAD itself) work either way.
        /// </summary>
        private static string ResolveSoundFontPath(Config config)
        {
            var dir = Path.GetDirectoryName(typeof(DoomSession).Assembly.Location);
            if (dir == null || string.IsNullOrEmpty(config.audio_soundfont))
            {
                return null;
            }

            var path = Path.Combine(dir, config.audio_soundfont);
            return File.Exists(path) ? path : null;
        }

        public int ScreenWidth => video.ScreenWidth;
        public int ScreenHeight => video.ScreenHeight;
        public string QuitMessage => doom.QuitMessage;

        /// <summary>Live diagnostic - which internal state the engine is in and
        /// whether the menu is open, so the view can show it and we can tell
        /// input-not-reaching-the-control apart from input-reaching-Doom-but-
        /// Doom-not-reacting.</summary>
        public string DebugStatus => doom.State + (doom.Menu.Active ? " (menu open)" : string.Empty);

        /// <summary>Advances the simulation by exactly one 35 Hz tic.</summary>
        /// <returns>false once the engine has requested to quit (e.g. user chose "Quit" in the menu).</returns>
        public bool Tick()
        {
            return doom.Update() != UpdateResult.Completed;
        }

        /// <summary>
        /// Renders the current state and returns the RGBA frame buffer (column-major
        /// - see WpfVideo.FrameBuffer) along with its screen dimensions.
        /// </summary>
        public byte[] RenderFrame()
        {
            video.Render(doom, Fixed.One);
            return video.FrameBuffer;
        }

        public void KeyDown(DoomKey key)
        {
            if (key == DoomKey.Unknown)
            {
                return;
            }

            userInput.KeyDown(key);
            doom.PostEvent(new DoomEvent(EventType.KeyDown, key));
        }

        public void KeyUp(DoomKey key)
        {
            if (key == DoomKey.Unknown)
            {
                return;
            }

            userInput.KeyUp(key);
            doom.PostEvent(new DoomEvent(EventType.KeyUp, key));
        }

        /// <summary>Call when the hosting control loses keyboard focus, so a key that
        /// never got its KeyUp (e.g. focus lost mid-press) doesn't stay stuck held.</summary>
        public void ReleaseAllKeys()
        {
            userInput.ReleaseAll();
        }

        /// <summary>Whether ManagedDoom currently wants the mouse grabbed (in a level,
        /// menu closed) - see Doom.CheckMouseState(), called once per Tick(). The view
        /// hides/warps the cursor while this is true and shows it normally otherwise.</summary>
        public bool IsMouseGrabbed => userInput.IsMouseGrabbed;

        /// <summary>Raised whenever <see cref="IsMouseGrabbed"/> changes.</summary>
        public event Action MouseGrabChanged
        {
            add => userInput.MouseGrabChanged += value;
            remove => userInput.MouseGrabChanged -= value;
        }

        /// <summary>Feeds a control-relative mouse-move delta (pixels) in for the next
        /// tic's turning - only meaningful while <see cref="IsMouseGrabbed"/>.</summary>
        public void AddMouseDelta(double dx, double dy)
        {
            userInput.AddMouseDelta(dx, dy);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            music?.Dispose();
            sound?.Dispose();
            audioDevice?.Dispose();
            content.Dispose();
        }
    }
}
