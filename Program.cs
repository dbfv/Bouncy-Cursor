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
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

            try
            {
                RunApp();
            }
            catch (Exception ex)
            {
                LogCrash(ex, "Main try/catch");
                throw;
            }
        }

        private static void RunApp()
        {
            var app = new Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                LogCrash(e.Exception, "DispatcherUnhandledException");
                e.Handled = true; // giữ app sống tiếp thay vì crash
            };

            var overlay = new OverlayWindow();
            overlay.Show();

            var hook = new MouseHook();
            hook.LeftButtonDown += (x, y) => overlay.Dispatcher.Invoke(() => overlay.OnPress(x, y));
            hook.LeftButtonUp += (x, y) => overlay.Dispatcher.Invoke(() => overlay.OnRelease());
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
                hook.Dispose();
                trayIcon.Visible = false;
                app.Shutdown();
            });
            trayIcon.ContextMenuStrip = menu;

            app.Exit += (_, _) => hook.Dispose();
            app.Run();
        }

        private static void LogCrash(Exception? ex, string source)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now}] {source}\n{ex}\n\n");
            }
            catch { /* nothing we can do */ }
        }
    }
}