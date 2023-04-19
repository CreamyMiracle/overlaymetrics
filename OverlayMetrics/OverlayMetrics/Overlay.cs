using GameOverlay.Drawing;
using GameOverlay.Windows;
using Microsoft.VisualBasic.Devices;
using SharpDX.Direct2D1;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace OverlayMetrics
{
    public class Overlay : IDisposable
    {
        private GameOverlay.Drawing.SolidBrush _backgroundBrush;
        private GameOverlay.Drawing.SolidBrush _blackBrush;
        private GameOverlay.Drawing.SolidBrush _whiteBrush;
        private GameOverlay.Drawing.SolidBrush _greenBrush;
        private GameOverlay.Drawing.SolidBrush _yellowBrush;
        private GameOverlay.Drawing.SolidBrush _redBrush;
        private GameOverlay.Drawing.Font _fontSmall;
        private readonly GraphicsWindow _window;

        private readonly Dictionary<string, PerformanceCounter> _cpuCounters = new Dictionary<string, PerformanceCounter>();
        private readonly Dictionary<string, PerformanceCounter> _ramCounters = new Dictionary<string, PerformanceCounter>();
        private readonly Dictionary<string, PerformanceCounter> _diskCounters = new Dictionary<string, PerformanceCounter>();
        private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>();
        private readonly int averagingCount = 30;

        private readonly Queue<float> cpuMetrics = new Queue<float>();
        private readonly Queue<float> ramMetrics = new Queue<float>();
        private readonly Queue<float> diskMetrics = new Queue<float>();
        private readonly Queue<float> threadMetrics = new Queue<float>();
        private readonly Queue<float> gpuMetrics = new Queue<float>();
        private readonly double _availableMemory;
        private readonly float _opacity = 0.35f;

        public Overlay()
        {
            _cpuCounters.Add("CPU", new PerformanceCounter("Processor", "% Processor Time", "_Total"));
            _ramCounters.Add("RAM", new PerformanceCounter("Memory", "Available MBytes"));

            var gpuCategory = new PerformanceCounterCategory("GPU Engine");
            var gpuInstances = gpuCategory.GetInstanceNames();
            foreach (string gpuInstance in gpuInstances)
            {
                if (gpuInstance.EndsWith("engtype_3D"))
                {
                    foreach (PerformanceCounter counter in gpuCategory.GetCounters(gpuInstance))
                    {
                        if (counter.CounterName == "Utilization Percentage")
                        {
                            _gpuCounters.Add(gpuInstance, counter);
                        }
                    }
                }
            }

            var trimmedChars = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ' };
            var diskCategory = new PerformanceCounterCategory("PhysicalDisk");
            var diskInstances = diskCategory.GetInstanceNames();
            foreach (string diskIstance in diskInstances)
            {
                foreach (PerformanceCounter counter in diskCategory.GetCounters(diskIstance))
                {
                    if (counter.CounterName == "Current Disk Queue Length" && diskIstance != "_Total")
                    {
                        _diskCounters.Add(diskIstance.Trim(trimmedChars), counter);
                    }
                }
            }

            var gcMemoryInfo = GC.GetGCMemoryInfo();
            var installedMemory = gcMemoryInfo.TotalAvailableMemoryBytes;
            // it will give the size of memory in MB
            _availableMemory = (double)installedMemory / 1048576.0;

            var gfx = new GameOverlay.Drawing.Graphics()
            {
                MeasureFPS = true,
                PerPrimitiveAntiAliasing = true,
                TextAntiAliasing = true
            };

            int totalWidth = Screen.AllScreens.ToList().Sum(screen => screen.Bounds.Width);
            int maxHeight = Screen.AllScreens.ToList().MaxBy(screen => screen.Bounds.Height).Bounds.Height;

            _window = new GraphicsWindow(0, 0, totalWidth, maxHeight, gfx)
            {
                FPS = 40,
                IsTopmost = true,
                IsVisible = true
            };

            _window.DestroyGraphics += _window_DestroyGraphics;
            _window.DrawGraphics += _window_DrawGraphics;
            _window.SetupGraphics += _window_SetupGraphics;
        }

        private void _window_SetupGraphics(object sender, SetupGraphicsEventArgs e)
        {
            var gfx = e.Graphics;

            _backgroundBrush = gfx.CreateSolidBrush(0, 0, 0, 0);
            _blackBrush = gfx.CreateSolidBrush(0, 0, 0, _opacity);
            _whiteBrush = gfx.CreateSolidBrush(255, 255, 255, _opacity);

            _greenBrush = gfx.CreateSolidBrush(0, 255, 0, _opacity);
            _yellowBrush = gfx.CreateSolidBrush(255, 255, 0, _opacity);
            _redBrush = gfx.CreateSolidBrush(255, 0, 0, _opacity);

            _fontSmall = gfx.CreateFont("Segoe UI", 10);
        }

        private void _window_DestroyGraphics(object sender, DestroyGraphicsEventArgs e)
        {
        }

        private void DrawMetric(GameOverlay.Drawing.Graphics? gfx, GameOverlay.Drawing.Rectangle rectangle, GameOverlay.Drawing.SolidBrush brush, GameOverlay.Drawing.SolidBrush textBrush, float value, string title)
        {
            gfx.DrawHorizontalProgressBar(_backgroundBrush, brush, rectangle, 1, value);
            gfx.DrawText(_fontSmall, textBrush, rectangle.Left, rectangle.Top + rectangle.Height, title);
            gfx.DrawText(_fontSmall, textBrush, rectangle.Left, rectangle.Top + rectangle.Height + 10, value.ToString("0") + "%");
        }

        private void _window_DrawGraphics(object sender, DrawGraphicsEventArgs e)
        {
            Win32API.POINT p = new Win32API.POINT();
            bool success = Win32API.GetCursorPos(out p);

            GameOverlay.Drawing.SolidBrush textBrush = FontColorBasedOnBackground(GetPixelColor(p.X, p.Y));

            int width = 20;
            int height = 40;
            int space = 30;
            int currX = p.X;
            int currY = p.Y - 80;

            var gfx = e.Graphics;
            gfx.ClearScene(_backgroundBrush);

            foreach (var counter in _cpuCounters)
            {
                float cpu = counter.Value.NextValue();
                cpuMetrics.Enqueue(cpu);
                if (cpuMetrics.Count >= averagingCount) { cpuMetrics.Dequeue(); }
                float cpuAvg = cpuMetrics.DefaultIfEmpty(0).Average();
                GameOverlay.Drawing.SolidBrush cpuBrush = cpuAvg <= 50 ? _greenBrush : cpuAvg >= 85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + width, currY + height), cpuBrush, textBrush, cpuAvg, counter.Key);
                currX = currX + space;
            }

            foreach (var counter in _ramCounters)
            {
                float ram = counter.Value.NextValue();
                ramMetrics.Enqueue(ram);
                if (ramMetrics.Count >= averagingCount) { ramMetrics.Dequeue(); }
                float ramAvg = ramMetrics.DefaultIfEmpty(0).Average();
                double ramRatio = (_availableMemory - ramAvg) / _availableMemory;
                GameOverlay.Drawing.SolidBrush ramBrush = ramRatio <= 0.5 ? _greenBrush : ramRatio >= 0.85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + width, currY + height), ramBrush, textBrush, (float)ramRatio * 100, counter.Key);
                currX = currX + space;
            }

            foreach (var counter in _diskCounters)
            {
                float disk = counter.Value.NextValue();
                GameOverlay.Drawing.SolidBrush diskBrush = disk <= 1 ? _greenBrush : disk >= 3 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + width, currY + height), diskBrush, textBrush, (disk / 3) * 100, counter.Key);
                currX = currX + space;
            }

            float gpu = 0;
            foreach (var counter in _gpuCounters)
            {
                try
                {
                    gpu += counter.Value.NextValue();
                }
                catch (System.InvalidOperationException ex)
                {
                    _gpuCounters.Remove(counter.Key);
                }
            }
            gpuMetrics.Enqueue(gpu);
            if (gpuMetrics.Count >= averagingCount) { gpuMetrics.Dequeue(); }
            float gpuAvg = gpuMetrics.DefaultIfEmpty(0).Average();
            GameOverlay.Drawing.SolidBrush gpuBrush = gpuAvg <= 50 ? _greenBrush : gpuAvg >= 85 ? _redBrush : _yellowBrush;
            DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + width, currY + height), gpuBrush, textBrush, gpuAvg, "GPU");
            currX = currX + space;

        }

        private GameOverlay.Drawing.SolidBrush FontColorBasedOnBackground(System.Drawing.Color bg)
        {
            if (bg.R * 2 + bg.G * 7 + bg.B < 500)
                return _whiteBrush;
            else
                return _blackBrush;
        }

        static private System.Drawing.Color GetPixelColor(int x, int y)
        {
            IntPtr hdc = Win32API.GetDC(IntPtr.Zero);
            uint pixel = Win32API.GetPixel(hdc, x, y);
            Win32API.ReleaseDC(IntPtr.Zero, hdc);
            System.Drawing.Color color = System.Drawing.Color.FromArgb((int)(pixel & 0x000000FF),
                         (int)(pixel & 0x0000FF00) >> 8,
                         (int)(pixel & 0x00FF0000) >> 16);
            return color;
        }



        public void Run()
        {
            _window.Create();
            _window.Join();
        }

        ~Overlay()
        {
            Dispose(false);
        }

        #region IDisposable Support
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                _window.Dispose();

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
