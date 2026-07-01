using System;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace BounceCursor
{
    public class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

            var overlay = new OverlayWindow();
            overlay.Show();

            var hook = new MouseHook();
            hook.LeftClick += (x, y) => overlay.Dispatcher.Invoke(() => overlay.ShowBounceAt(x, y));
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
    }
}