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
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }

            try
            {
                int myId = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName("VisionStudio"))
                {
                    if (p.Id != myId)
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }

            bool isAutoStart = args != null && args.Any(a =>
                string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase));

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError("ThreadException", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError("UnhandledException", e.ExceptionObject as Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(isAutoStart));
        }

        public static void LogError(string tag, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vision_studio.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {ex}\r\n");
            }
            catch { }
        }
    }
}
