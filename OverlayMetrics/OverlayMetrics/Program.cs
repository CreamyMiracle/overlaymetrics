using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ToolTip;

namespace OverlayMetrics
{
    internal class Program
    {
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        [STAThread]
        static void Main(string[] args)
        {
            var builder = new ConfigurationBuilder();
            builder.SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration config = builder.Build();

            if (bool.Parse(config["HideConsole"]))
            {
                var handle = Win32API.GetConsoleWindow();
                Win32API.ShowWindow(handle, SW_HIDE);
            }

            GameOverlay.TimerService.EnableHighPrecisionTimers();
            using (var overlay = new Overlay(config))
            {
                overlay.Run();
            }
        }
    }
}