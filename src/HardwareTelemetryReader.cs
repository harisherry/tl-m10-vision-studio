using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSCore;
using CSCore.DSP;
using CSCore.SoundIn;
using CSCore.Streams;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UsbHid;
using UsbHid.USB.Classes.Messaging;

namespace VisionStudio
{
    public class HardwareTelemetryReader
    {
        private const string SYSINFO_MMF = "shareMemory_SysInfo";
        private PerformanceCounter cpuCounter;
        private long lastBytesRecv = 0;
        private long lastBytesSent = 0;
        private DateTime lastNetSample = DateTime.UtcNow;
        private readonly TelemetryData current = new TelemetryData();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public HardwareTelemetryReader()
        {
            try
            {
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue();
            }
            catch { }
        }

        public void StartBackgroundHelperIfNeeded()
        {
            try
            {
                if (Process.GetProcessesByName("HWINFO").Length == 0)
                {
                    string hwInfoExe = @"C:\Program Files\TRCCCAP\HWINFO.exe";
                    if (File.Exists(hwInfoExe))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = hwInfoExe,
                            WorkingDirectory = Path.GetDirectoryName(hwInfoExe),
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                    }
                }
            }
            catch { }
        }

        public TelemetryData ReadLatest()
        {
            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem))
                {
                    current.ramUsage = mem.dwMemoryLoad;
                    current.ramUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
                }
            }
            catch { }

            try
            {
                if (cpuCounter != null)
                {
                    float val = cpuCounter.NextValue();
                    if (val > 0.1f) current.cpuUsage = Math.Round(val, 1);
                }
            }
            catch { }

            try
            {
                double dt = (DateTime.UtcNow - lastNetSample).TotalSeconds;
                if (dt >= 0.5)
                {
                    long recv = 0, sent = 0;
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up &&
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        {
                            var stats = ni.GetIPv4Statistics();
                            recv += stats.BytesReceived;
                            sent += stats.BytesSent;
                        }
                    }
                    if (lastBytesRecv > 0)
                    {
                        current.netDownKbps = Math.Max(0, (recv - lastBytesRecv) / 1024.0 / dt);
                        current.netUpKbps = Math.Max(0, (sent - lastBytesSent) / 1024.0 / dt);
                    }
                    lastBytesRecv = recv;
                    lastBytesSent = sent;
                    lastNetSample = DateTime.UtcNow;
                }
            }
            catch { }

            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(SYSINFO_MMF, MemoryMappedFileRights.Read))
                using (var stream = mmf.CreateViewStream(0, 65536, MemoryMappedFileAccess.Read))
                {
                    byte[] buf = new byte[65536];
                    int read = stream.Read(buf, 0, buf.Length);
                    int nullIdx = Array.IndexOf(buf, (byte)0);
                    int strLen = nullIdx > 0 ? nullIdx : read;
                    if (strLen > 16)
                    {
                        string rawJson = Encoding.UTF8.GetString(buf, 0, strLen);
                        ParseSysInfoSensors(rawJson);
                    }
                }
            }
            catch { }

            return current;
        }

        private void ParseSysInfoSensors(string json)
        {
            ExtractMetric(json, @"(?:CPU Package|CPU \(Tctl/Tdie\)|Core Max)""[^}]*""val""\s*:\s*([\d.]+)", v => current.cpuTemp = v);
            ExtractMetric(json, @"Total CPU Usage""[^}]*""val""\s*:\s*([\d.]+)", v => current.cpuUsage = v);
            ExtractMetric(json, @"CPU Package Power""[^}]*""val""\s*:\s*([\d.]+)", v => current.cpuPower = v);
            ExtractMetric(json, @"GPU Temperature""[^}]*""val""\s*:\s*([\d.]+)", v => current.gpuTemp = v);
            ExtractMetric(json, @"GPU Core Load|GPU D3D Usage""[^}]*""val""\s*:\s*([\d.]+)", v => current.gpuUsage = v);
            ExtractMetric(json, @"GPU Power""[^}]*""val""\s*:\s*([\d.]+)", v => current.gpuPower = v);
            ExtractMetric(json, @"GPU Clock""[^}]*""val""\s*:\s*([\d.]+)", v => current.gpuClock = v);
        }

        private static void ExtractMetric(string input, string pattern, Action<double> setter)
        {
            var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
            {
                setter(v);
            }
        }
    }
}
