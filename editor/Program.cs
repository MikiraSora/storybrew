﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrewLib.Audio;
using BrewLib.Graphics;
using BrewLib.Util;
using Microsoft.Win32;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using StorybrewEditor.Util;
using Image = OpenTK.Windowing.GraphicsLibraryFramework.Image;

namespace StorybrewEditor
{
    class Program
    {
        public const string Name = "storybrew editor";
        public const string Repository = "Damnae/storybrew";
        public static Version Version => Assembly.GetExecutingAssembly().GetName().Version;
        public static string FullName => $"{Name} {Version} ({Repository})";
        public static string DiscordUrl = $"https://discord.gg/0qfFOucX93QDNVN7";

        public static AudioManager AudioManager { get; private set; }
        public static Settings Settings { get; private set; }

        private static int mainThreadId;
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;
        public static void CheckMainThread([CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = -1, [CallerMemberName] string callerName = "")
        {
            if (IsMainThread) return;
            throw new InvalidOperationException($"{callerPath}:L{callerLine} {callerName} called from the thread '{Thread.CurrentThread.Name}', must be called from the main thread");
        }

        [STAThread]
        public static void Main(string[] args)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            //Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

            if (args.Length != 0 && handleArguments(args))
                return;

            setupLogging(checkFrozen: false);
            startEditor();
        }

        private static bool handleArguments(string[] args)
        {
            switch (args[0])
            {
                case "update":
                    if (args.Length < 3) return false;
                    setupLogging(Path.Combine(args[1], DefaultLogPath), "update.log");
                    Updater.Update(args[1], new Version(args[2]));
                    return true;
                case "build":
                    setupLogging(null, "build.log");
                    Builder.Build();
                    return true;
            }
            return false;
        }

        #region Editor

        public static string Stats { get; private set; }

        private static void startEditor()
        {
            enableScheduling();

            Settings = new Settings();
            Updater.NotifyEditorRun();

            using (var window = createWindow())
            using (AudioManager = createAudioManager(window))
            using (var editor = new Editor(window))
            {
                Trace.WriteLine($"{getOSVersion()}");
                Trace.WriteLine($"graphics mode: {window.Context}");

                using var stream = typeof(Program).Module.Assembly.GetManifestResourceStream(typeof(Program), "icon.ico");
                var img = System.Drawing.Image.FromStream(stream) as Bitmap;
                var data = img.LockBits(new Rectangle(0, 0, img.Width, img.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var size = img.Width * img.Height * 4;
                byte[] managedArray = new byte[size];
                Marshal.Copy(data.Scan0, managedArray, 0, size);
                window.Icon = new WindowIcon(new OpenTK.Windowing.Common.Input.Image(img.Width, img.Height, managedArray));
                img.UnlockBits(data);

                window.Resize += (e) =>
                {
                    editor.Draw(1);
                    window.SwapBuffers();
                };

                editor.Initialize();
                runMainLoop(window, editor, 1.0 / Settings.UpdateRate, 1.0 / (Settings.FrameRate > 0 ? Settings.FrameRate : 0));

                Settings.Save();
            }
        }

        private static string getOSVersion()
        {
            try
            {
                using (var registryKey = Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion"))
                    return (string)registryKey.GetValue("ProductName");
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        private static GameWindow createWindow()
        {
#if DEBUG
            var contextFlags = ContextFlags.Debug | ContextFlags.Default;
#else
            var contextFlags = ContextFlags.Defaulte;
#endif
            var primaryScreenArea = Screen.PrimaryScreen.WorkingArea;

            int windowWidth = 1366, windowHeight = 768;
            if (windowHeight >= primaryScreenArea.Height)
            {
                windowWidth = 1024;
                windowHeight = 600;
                if (windowWidth >= primaryScreenArea.Width) windowWidth = 800;
            }
            var window = new GameWindow(new GameWindowSettings()
            {


            }, new NativeWindowSettings()
            {
                Size = new(windowWidth, windowHeight),
                Title = Name,
                Profile = ContextProfile.Compatability,
                Flags = contextFlags,
                WindowState = WindowState.Normal,
            });
            Trace.WriteLine($"Window dpi scale: {window.Size.Y / (float)windowHeight}");

            window.Location = new Vector2i(
                (int)(primaryScreenArea.Left + (primaryScreenArea.Width - window.Size.X) * 0.5f),
                (int)(primaryScreenArea.Top + (primaryScreenArea.Height - window.Size.Y) * 0.5f)
            );
            if (window.Location.X < 0 || window.Location.Y < 0)
            {
                window.Location = new(primaryScreenArea.Location.X, primaryScreenArea.Location.Y);
                window.Size = new(primaryScreenArea.Size.Width, primaryScreenArea.Size.Height);
                window.WindowState = WindowState.Maximized;
            }

            return window;
        }

        private static AudioManager createAudioManager(GameWindow window)
        {
            var audioManager = new AudioManager(window.GetWindowHandle())
            {
                Volume = Settings.Volume,
            };
            Settings.Volume.OnValueChanged += (sender, e) => audioManager.Volume = Settings.Volume;

            return audioManager;
        }

        private static void runMainLoop(GameWindow window, Editor editor, double fixedRateUpdateDuration, double targetFrameDuration)
        {
            var previousTime = 0.0;
            var fixedRateTime = 0.0;
            var averageFrameTime = 0.0;
            var averageActiveTime = 0.0;
            var longestFrameTime = 0.0;
            var lastStatTime = 0.0;
            var windowDisplayed = false;
            var watch = new Stopwatch();

            watch.Start();
            while (window.Exists && !window.IsExiting)
            {
                var focused = window.IsFocused;
                var currentTime = watch.Elapsed.TotalSeconds;
                var fixedUpdates = 0;

                AudioManager.Update();
                window.ProcessEvents();

                while (currentTime - fixedRateTime >= fixedRateUpdateDuration && fixedUpdates < 2)
                {
                    fixedRateTime += fixedRateUpdateDuration;
                    fixedUpdates++;

                    editor.Update(fixedRateTime, true);
                }
                if (focused && fixedUpdates == 0 && fixedRateTime < currentTime && currentTime < fixedRateTime + fixedRateUpdateDuration)
                    editor.Update(currentTime, false);

                if (!window.Exists || window.IsExiting) return;

                window.VSync =/* focused ? VSyncMode.Off :*/ VSyncMode.On;
                if (window.WindowState != WindowState.Minimized)
                {
                    var tween = Math.Min((currentTime - fixedRateTime) / fixedRateUpdateDuration, 1);
                    editor.Draw(tween);
                    window.SwapBuffers();
                }

                if (!windowDisplayed)
                {
                    window.IsVisible = true;
                    windowDisplayed = true;
                }

                RunScheduledTasks();

                var activeDuration = watch.Elapsed.TotalSeconds - currentTime;
                var sleepMs = Math.Max(0, (int)(((focused ? targetFrameDuration : fixedRateUpdateDuration) - activeDuration) * 1000));
                Thread.Sleep(sleepMs);

                var frameTime = currentTime - previousTime;
                previousTime = currentTime;

                // Stats

                averageFrameTime = (frameTime + averageFrameTime) / 2;
                averageActiveTime = (activeDuration + averageActiveTime) / 2;
                longestFrameTime = Math.Max(frameTime, longestFrameTime);

                if (lastStatTime + 1 < currentTime)
                {
                    Stats = $"fps:{1 / averageFrameTime:0}/{1 / averageActiveTime:0} (act:{averageActiveTime * 1000:0} avg:{averageFrameTime * 1000:0} hi:{longestFrameTime * 1000:0})";
                    if (false) Debug.Print($"TexBinds - {DrawState.TextureBinds}, {editor.GetStats()}");

                    longestFrameTime = 0;
                    lastStatTime = currentTime;
                }
            }
        }

        #endregion

        #region Scheduling

        public static bool SchedulingEnabled { get; private set; }

        private static readonly Queue<Action> scheduledActions = new Queue<Action>();

        public static void enableScheduling()
        {
            SchedulingEnabled = true;
        }

        /// <summary>
        /// Schedule the action to run in the main thread.
        /// Exceptions will be logged.
        /// </summary>
        public static void Schedule(Action action)
        {
            if (SchedulingEnabled)
                lock (scheduledActions)
                    scheduledActions.Enqueue(action);
            else throw new InvalidOperationException("Scheduling isn't enabled");
        }

        /// <summary>
        /// Schedule the action to run in the main thread after a delay (in milliseconds).
        /// Exceptions will be logged.
        /// </summary>
        public static void Schedule(Action action, int delay)
        {
            Task.Run(async () =>
            {
                await Task.Delay(delay);
                Schedule(action);
            });
        }

        /// <summary>
        /// Run the action synchronously in the main thread.
        /// Exceptions will be thrown to the calling thread.
        /// </summary>
        public static void RunMainThread(Action action)
        {
            if (IsMainThread)
            {
                action();
                return;
            }

            using (var completed = new ManualResetEvent(false))
            {
                Exception exception = null;
                Schedule(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                    completed.Set();
                });
                completed.WaitOne();

                if (exception != null)
                    throw exception;
            }
        }

        public static void RunScheduledTasks()
        {
            CheckMainThread();

            Action[] actionsToRun;
            lock (scheduledActions)
            {
                actionsToRun = new Action[scheduledActions.Count];
                scheduledActions.CopyTo(actionsToRun, 0);
                scheduledActions.Clear();
            }

            foreach (var action in actionsToRun)
            {
#if !DEBUG
                try
                {
#endif
                action.Invoke();
#if !DEBUG
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Scheduled task {action.Method} failed:\n{e}");
                }
#endif
            }
        }

        #endregion

        #region Error Handling

        public const string DefaultLogPath = "logs";

        private static TraceLogger logger;
        private static readonly object errorHandlerLock = new object();
        private static volatile bool insideErrorHandler;

        private static void setupLogging(string logsPath = null, string commonLogFilename = null, bool checkFrozen = false)
        {
            logsPath = logsPath ?? DefaultLogPath;
            var tracePath = Path.Combine(logsPath, commonLogFilename ?? "trace.log");
            var exceptionPath = Path.Combine(logsPath, commonLogFilename ?? "exception.log");
            var crashPath = Path.Combine(logsPath, commonLogFilename ?? "crash.log");
            var freezePath = Path.Combine(logsPath, commonLogFilename ?? "freeze.log");

            if (!Directory.Exists(logsPath))
                Directory.CreateDirectory(logsPath);
            else
            {
                if (File.Exists(tracePath)) File.Delete(tracePath);
                if (File.Exists(exceptionPath)) File.Delete(exceptionPath);
            }

            logger = new TraceLogger(tracePath);
            Trace.WriteLine($"{FullName}\n");

            AppDomain.CurrentDomain.FirstChanceException += (sender, e) => logError(e.Exception, exceptionPath, null, false);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => logError((Exception)e.ExceptionObject, crashPath, "crash", true);

            if (checkFrozen)
                setupFreezeCheck(e => logError(e, freezePath, null, false));
        }

        private static void logError(Exception e, string filename, string reportType, bool show)
        {
            lock (errorHandlerLock)
            {
                if (insideErrorHandler) return;
                insideErrorHandler = true;

                try
                {
                    var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
                    using (StreamWriter w = new StreamWriter(logPath, true))
                    {
                        w.Write(DateTime.Now + " - ");
                        w.WriteLine(e);
                        w.WriteLine();
                    }

                    if (reportType != null)
                        Report(reportType, e);

                    if (show)
                    {
                        var result = MessageBox.Show($"An error occured:\n\n{e.Message} ({e.GetType().Name})\n\nClick Ok if you want to receive and invitation to a Discord server where you can get help with this problem.", FullName, MessageBoxButtons.OKCancel);
                        if (result == DialogResult.OK) Process.Start(new ProcessStartInfo(DiscordUrl) { UseShellExecute = true });
                    }
                }
                catch (Exception e2)
                {
                    Trace.WriteLine(e2.Message);
                }
                finally
                {
                    insideErrorHandler = false;
                }
            }
        }

        public static void Report(string type, Exception e)
        {
#if DEBUG
            return;
#endif

            return; // rip, server =(
            NetHelper.BlockingPost("http://a-damnae.rhcloud.com/storybrew/report.php",
                new NameValueCollection()
                {
                    ["reporttype"] = type,
                    ["source"] = Settings?.Id ?? "-",
                    ["version"] = Version.ToString(),
                    ["content"] = e.ToString(),
                },
                (response, exception) =>
                {
                });
        }

        private static void setupFreezeCheck(Action<Exception> action)
        {
            var mainThread = Thread.CurrentThread;

            var thread = new Thread(() =>
            {
                var answered = false;
                var frozen = 0;

                while (!SchedulingEnabled)
                    Thread.Sleep(1000);

                while (true)
                {
                    answered = false;
                    Schedule(() => answered = true);

                    Thread.Sleep(1000);

                    if (!answered)
                        frozen++;

                    if (frozen >= 3)
                    {
                        frozen = 0;

                        mainThread.Suspend();
                        StackTrace trace = null;
                        try
                        {
                            //trace = new StackTrace(mainThread, true);
                            action(new Exception(trace?.ToString()));
                        }
                        catch (ThreadStateException e)
                        {
                            action(e);
                        }

                        try
                        {
                            mainThread.Resume();
                        }
                        catch (ThreadStateException e)
                        {
                            action(e);
                        }
                    }
                }
            })
            { Name = "Freeze Checker", IsBackground = true, };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        #endregion
    }
}
