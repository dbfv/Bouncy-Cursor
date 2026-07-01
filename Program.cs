using System;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace BounceCursor
{
    public class Program
    {
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

        [STAThread]
        public static void Main()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CursorAnimator.RestoreAll();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                CursorAnimator.RestoreAll();
                LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
            };

            try { RunApp(); }
            catch (Exception ex)
            {
                CursorAnimator.RestoreAll();
                LogCrash(ex, "Main try/catch");
                throw;
            }
        }

        private static void RunApp()
        {
            var app = new Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                CursorAnimator.RestoreAll();
                LogCrash(e.Exception, "DispatcherUnhandledException");
                e.Handled = true;
            };

            var bounce = new CursorBounceEffect();
            var hook = new MouseHook();
            hook.LeftButtonDown += (x, y) => app.Dispatcher.Invoke(() => bounce.OnPress());
            hook.LeftButtonUp += (x, y) => app.Dispatcher.Invoke(() => bounce.OnRelease());
            hook.Start();

            var trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "BounceCursor"
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Thoát", null, (_, _) =>
            {
                CursorAnimator.RestoreAll();
                hook.Dispose();
                trayIcon.Visible = false;
                app.Shutdown();
            });
            trayIcon.ContextMenuStrip = menu;

            app.Exit += (_, _) => { hook.Dispose(); CursorAnimator.RestoreAll(); };
            app.Run();
        }

        private static void LogCrash(Exception? ex, string source)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now}] {source}\n{ex}\n\n"); }
            catch { }
        }
    }
}