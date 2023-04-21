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
using System.Timers;
using System.Windows.Forms;
using static System.Net.Mime.MediaTypeNames;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Microsoft.Extensions.Configuration;
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

        private TimeSpan counterRefreshSpan = TimeSpan.FromMilliseconds(5000);

        private ConcurrentDictionary<string, PerformanceCounter> _cpuCounters = new ConcurrentDictionary<string, PerformanceCounter>();
        private ConcurrentDictionary<string, float> _cpuAvgs = new ConcurrentDictionary<string, float>();
        private DateTime _cpuCountersUpdated = DateTime.MinValue;

        private ConcurrentDictionary<string, PerformanceCounter> _ramCounters = new ConcurrentDictionary<string, PerformanceCounter>();
        private ConcurrentDictionary<string, float> _ramAvgs = new ConcurrentDictionary<string, float>();
        private DateTime _ramCountersUpdated = DateTime.MinValue;

        private ConcurrentDictionary<string, PerformanceCounter> _diskCounters = new ConcurrentDictionary<string, PerformanceCounter>();
        private ConcurrentDictionary<string, float> _diskAvgs = new ConcurrentDictionary<string, float>();
        private DateTime _diskCountersUpdated = DateTime.MinValue;

        private ConcurrentDictionary<string, PerformanceCounter> _gpuCounters = new ConcurrentDictionary<string, PerformanceCounter>();
        private ConcurrentDictionary<string, float> _gpuAvgs = new ConcurrentDictionary<string, float>();
        private DateTime _gpuCountersUpdated = DateTime.MinValue;

        private readonly int _averagingCount;
        private readonly int _fontColorThreshold;

        private ConcurrentDictionary<string, ConcurrentQueue<float>> _cpuMetrics = new ConcurrentDictionary<string, ConcurrentQueue<float>>();
        private ConcurrentDictionary<string, ConcurrentQueue<float>> _ramMetrics = new ConcurrentDictionary<string, ConcurrentQueue<float>>();
        private ConcurrentDictionary<string, ConcurrentQueue<float>> _diskMetrics = new ConcurrentDictionary<string, ConcurrentQueue<float>>();

        private ConcurrentQueue<float> _gpuMetrics = new ConcurrentQueue<float>();

        private readonly int _fps;
        private readonly double _availableMemory;
        private readonly float _opacity;
        private readonly int _left;
        private readonly int _top;
        private readonly int _width;
        private readonly int _height;
        private readonly int _elementSpacing;
        private readonly string _fontName;
        private readonly int _fontSize;
        private readonly bool _followMouse;

        private readonly System.Timers.Timer updateCountersTimer;
        private readonly System.Timers.Timer updateMetricsTimer;

        private readonly IConfiguration _config;

        public Overlay(IConfiguration config)
        {
            _config = config;

            _averagingCount = int.Parse(_config["AverageOf"]);
            _fontColorThreshold = int.Parse(_config["FontColorThreshold"]);
            _opacity = float.Parse(_config["Opacity"]);

            _left = int.Parse(_config["OverlayLeft"]);
            _top = int.Parse(_config["OverlayTop"]);
            _width = int.Parse(_config["OverlayWidth"]);
            _height = int.Parse(_config["OverlayHeight"]);
            _elementSpacing = int.Parse(_config["OverlayElementSpacing"]);
            _fontName = _config["OverlayFontName"];
            _fontSize = int.Parse(_config["OverlayFontSize"]);
            _followMouse = bool.Parse(config["RelativeToMouse"]);
            _fps = int.Parse(_config["OverlayFPS"]);

            updateCountersTimer = new System.Timers.Timer(int.Parse(_config["UpdateCounterIntervalMillis"]));
            updateCountersTimer.Elapsed += OnCounterTimedEvent;
            updateCountersTimer.AutoReset = true;
            updateCountersTimer.Enabled = true;

            updateMetricsTimer = new System.Timers.Timer(int.Parse(_config["UpdateMetricsIntervalMillis"]));
            updateMetricsTimer.Elapsed += OnMetricsTimedEvent;
            updateMetricsTimer.AutoReset = true;
            updateMetricsTimer.Enabled = true;

            GetCPUCounters();
            GetRAMCounters();
            GetGPUCounters();
            GetDiskCounters();

            var gcMemoryInfo = GC.GetGCMemoryInfo();
            var installedMemory = gcMemoryInfo.TotalAvailableMemoryBytes;
            // it will give the size of memory in MB
            _availableMemory = (double)installedMemory / 1048576.0;
            var gfx = new GameOverlay.Drawing.Graphics()
            {
                MeasureFPS = false,
                PerPrimitiveAntiAliasing = false,
                VSync = false,
                TextAntiAliasing = true
            };

            int totalWidth = Screen.AllScreens.ToList().Sum(screen => screen.Bounds.Width);
            int maxHeight = Screen.AllScreens.ToList().MaxBy(screen => screen.Bounds.Height).Bounds.Height;
            _window = new GraphicsWindow(0, 0, totalWidth, maxHeight, gfx)
            {
                FPS = _fps,
                IsTopmost = true,
                IsVisible = true
            };
            _window.DestroyGraphics += _window_DestroyGraphics;
            _window.DrawGraphics += _window_DrawGraphics;
            _window.SetupGraphics += _window_SetupGraphics;
        }

        private void OnCounterTimedEvent(Object source, ElapsedEventArgs e)
        {
            SetOverlaySize();
            GetCPUCounters();
            GetRAMCounters();
            GetGPUCounters();
            GetDiskCounters();
        }

        private void OnMetricsTimedEvent(Object source, ElapsedEventArgs e)
        {
            //_cpuAvgs.Clear();
            foreach (var counter in _cpuCounters)
            {
                float cpu = counter.Value.NextValue();
                ConcurrentQueue<float> currQueue = _cpuMetrics[counter.Key];
                currQueue.Enqueue(cpu);
                if (currQueue.Count >= _averagingCount) { currQueue.TryDequeue(out float _); }
                float cpuAvg = currQueue.DefaultIfEmpty(0).Average();
                _cpuAvgs[counter.Key] = cpuAvg;
            }

            //_ramAvgs.Clear();
            foreach (var counter in _ramCounters)
            {
                float ram = counter.Value.NextValue();
                ConcurrentQueue<float> currQueue = _ramMetrics[counter.Key];
                currQueue.Enqueue(ram);
                if (currQueue.Count >= _averagingCount) { currQueue.TryDequeue(out float _); }
                float ramAvg = currQueue.DefaultIfEmpty(0).Average();
                double ramRatio = (_availableMemory - ramAvg) / _availableMemory;
                _ramAvgs[counter.Key] = (float)ramRatio;
            }

            //_diskAvgs.Clear();
            foreach (var counter in _diskCounters)
            {
                float disk = 100 - (int)counter.Value.NextValue();
                ConcurrentQueue<float> currQueue = _diskMetrics[counter.Key];
                currQueue.Enqueue(disk);
                if (currQueue.Count >= _averagingCount) { currQueue.TryDequeue(out float _); }
                float diskAvg = currQueue.DefaultIfEmpty(0).Average();
                _diskAvgs[counter.Key] = diskAvg;
            }

            //_gpuAvgs.Clear();
            float gpu = 0;
            foreach (var counter in _gpuCounters)
            {
                try
                {
                    gpu += counter.Value.NextValue();
                }
                catch (System.InvalidOperationException ex)
                {
                    GetGPUCounters();
                }
            }

            _gpuMetrics.Enqueue(gpu);
            if (_gpuMetrics.Count >= _averagingCount) { _gpuMetrics.TryDequeue(out float _); }
            float gpuAvg = _gpuMetrics.DefaultIfEmpty(0).Average();
            _gpuAvgs["GPU"] = gpuAvg;
        }

        private void SetOverlaySize()
        {
            if (_window == null) { return; }
            int totalWidth = Screen.AllScreens.ToList().Sum(screen => screen.Bounds.Width);
            int maxHeight = Screen.AllScreens.ToList().MaxBy(screen => screen.Bounds.Height).Bounds.Height;

            if (_window.Width == totalWidth && _window.Height == maxHeight)
            {
                return;
            }
            _window.Width = totalWidth;
            _window.Height = maxHeight;
        }

        private void GetCPUCounters()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _cpuCountersUpdated < counterRefreshSpan)
            {
                return;
            }
            _cpuCountersUpdated = now;

            _cpuCounters.Clear();

            _cpuCounters.TryAdd("CPU", new PerformanceCounter("Processor", "% Processor Time", "_Total"));
            _cpuCounters.ToList().ForEach(kv => _cpuMetrics.TryAdd(kv.Key, new ConcurrentQueue<float>()));
        }
        private void GetRAMCounters()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _ramCountersUpdated < counterRefreshSpan)
            {
                return;
            }
            _ramCountersUpdated = now;

            _ramCounters.Clear();

            _ramCounters.TryAdd("RAM", new PerformanceCounter("Memory", "Available MBytes"));
            _ramCounters.ToList().ForEach(kv => _ramMetrics.TryAdd(kv.Key, new ConcurrentQueue<float>()));
        }
        private void GetGPUCounters()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _gpuCountersUpdated < counterRefreshSpan)
            {
                return;
            }
            _gpuCountersUpdated = now;

            _gpuCounters.Clear();

            var gpuCategory = new PerformanceCounterCategory("GPU Engine");
            var gpuCounterNames = gpuCategory.GetInstanceNames();
            _gpuCounters = gpuCounterNames.Where(counterName => counterName.EndsWith("engtype_3D"))
                .SelectMany(gpuCategory.GetCounters)
                .Where(counter => counter.CounterName.Equals("Utilization Percentage"))
                .ToConcurrentDictionary(x => x.InstanceName, x => x);
        }
        private void GetDiskCounters()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _diskCountersUpdated < counterRefreshSpan)
            {
                return;
            }
            _diskCountersUpdated = now;

            _diskCounters.Clear();

            var trimmedChars = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ' };
            var diskCategory = new PerformanceCounterCategory("PhysicalDisk");
            var diskCounterNames = diskCategory.GetInstanceNames();
            _diskCounters = diskCounterNames.Where(counterName => counterName != "_Total")
                .SelectMany(diskCategory.GetCounters)
                .Where(counter => counter.CounterName.Equals("% Idle Time"))
                .ToConcurrentDictionary(x => x.InstanceName.Trim(trimmedChars), x => x);
            _diskCounters.ToList().ForEach(kv => _diskMetrics.TryAdd(kv.Key, new ConcurrentQueue<float>()));
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
            _fontSmall = gfx.CreateFont(_fontName, _fontSize);
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
            if (_followMouse)
            {
                bool success = Win32API.GetCursorPos(out p);
            }
            int currX = p.X + _left;
            int currY = p.Y + _top;
            var gfx = e.Graphics;

            GameOverlay.Drawing.SolidBrush textBrush = FontColorBasedOnBackground(GetPixelColor(p.X, p.Y));

            gfx.ClearScene(_backgroundBrush);

            foreach (var cpu in _cpuAvgs)
            {
                float cpuAvg = cpu.Value;
                GameOverlay.Drawing.SolidBrush cpuBrush = cpuAvg <= 50 ? _greenBrush : cpuAvg >= 85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + _width, currY + _height), cpuBrush, textBrush, cpuAvg, cpu.Key);
                currX = currX + _elementSpacing;
            }

            foreach (var ram in _ramAvgs)
            {
                double ramRatio = ram.Value;
                GameOverlay.Drawing.SolidBrush ramBrush = ramRatio <= 0.5 ? _greenBrush : ramRatio >= 0.85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + _width, currY + _height), ramBrush, textBrush, (float)ramRatio * 100, ram.Key);
                currX = currX + _elementSpacing;
            }

            foreach (var disk in _diskAvgs)
            {
                float diskAvg = disk.Value;
                GameOverlay.Drawing.SolidBrush diskBrush = diskAvg <= 50 ? _greenBrush : diskAvg >= 85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + _width, currY + _height), diskBrush, textBrush, diskAvg, disk.Key);
                currX = currX + _elementSpacing;
            }

            foreach (var gpu in _gpuAvgs)
            {
                float gpuAvg = gpu.Value;
                GameOverlay.Drawing.SolidBrush gpuBrush = gpuAvg <= 50 ? _greenBrush : gpuAvg >= 85 ? _redBrush : _yellowBrush;
                DrawMetric(gfx, new GameOverlay.Drawing.Rectangle(currX, currY, currX + _width, currY + _height), gpuBrush, textBrush, gpuAvg, gpu.Key);
            }
        }

        private GameOverlay.Drawing.SolidBrush FontColorBasedOnBackground(System.Drawing.Color bg)
        {
            int threshold = bg.R * 2 + bg.G * 7 + bg.B;
            if (threshold < _fontColorThreshold)
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