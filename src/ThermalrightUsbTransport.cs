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
    /// <summary>
    /// Streams 1920x462 frames to the Thermalright TL-M10 Vision LCD.
    /// </summary>
    public class ThermalrightUsbTransport : IDisposable
    {
        private const string MMF_NAME = "shareMemory_ImageRGB";
        private const long MMF_SIZE = 34560000L; // 50 * 691200
        private const int SLOT_SIZE = 691200;

        private static readonly byte[] HeaderTemplate1920x462 = new byte[64]
        {
            0x12, 0x34, 0x56, 0x78,
            0x02, 0x00, 0x00, 0x00,
            0x80, 0x07, 0x00, 0x00,
            0xCE, 0x01, 0x00, 0x00,
            0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
            0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
            0,0,0,0, 0,0,0,0,
            0x02, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        private static readonly byte[] PreWriteFlag = new byte[] { 0x00, 0x00, 0x00, 0x1E };
        private static readonly byte[] PostWriteFlag = new byte[] { 0x00, 0x01, 0x01, 0x1E };

        private MemoryMappedFile mmf;
        private MemoryMappedViewAccessor accessor;
        private UsbHidDevice directHidDevice;
        private bool directHidReady = false;
        private readonly byte[] packetBuffer = new byte[SLOT_SIZE];
        private int watchdogCounter = 0;

        public bool IsActive { get; private set; }
        public string StatusText { get; private set; } = "Initializing USB...";

        public void EnsureConnected(bool stopTrccIfRunning)
        {
            try
            {
                if (stopTrccIfRunning)
                {
                    foreach (var p in Process.GetProcessesByName("TRCC"))
                    {
                        try { p.Kill(); p.WaitForExit(1000); } catch { }
                    }
                }

                if (mmf == null)
                {
                    mmf = MemoryMappedFile.CreateOrOpen(MMF_NAME, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
                    accessor = mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.ReadWrite);
                }

                try
                {
                    accessor.WriteArray(0, PreWriteFlag, 0, 4);
                }
                catch { }

                bool usbLcdServiceRunning = Process.GetProcessesByName("USBLCDNEW").Length > 0;
                if (!usbLcdServiceRunning)
                {
                    string usbLcdExe = @"C:\Program Files\TRCCCAP\USBLCDNEW.exe";
                    if (File.Exists(usbLcdExe))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = usbLcdExe,
                            WorkingDirectory = Path.GetDirectoryName(usbLcdExe),
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                        usbLcdServiceRunning = true;
                    }
                }

                if (usbLcdServiceRunning)
                {
                    IsActive = true;
                    StatusText = "TL-M10 Vision USB Connected (1920Ã—462)";
                    return;
                }

                if (directHidDevice == null)
                {
                    directHidDevice = new UsbHidDevice(0x0416, 0x5302, new ArrayList(), 512);
                    if (directHidDevice.Connect())
                    {
                        for (int i = 0; i < 400; i++)
                        {
                            directHidDevice.SendMessage(new CommandMessage(new byte[512]));
                        }
                        byte[] initCmd = new byte[20];
                        initCmd[0] = 0xDA; initCmd[1] = 0xDB; initCmd[2] = 0xDC; initCmd[3] = 0xDD;
                        initCmd[12] = 0x01;
                        directHidDevice.SendMessage(new CommandMessage(initCmd));
                        directHidReady = true;
                        IsActive = true;
                        StatusText = "TL-M10 Vision Direct HID Active";
                        return;
                    }
                }

                IsActive = true;
                StatusText = "USB Stream Ready (1920Ã—462)";
            }
            catch (Exception ex)
            {
                IsActive = false;
                StatusText = "USB Standby (" + ex.GetType().Name + ")";
            }
        }

        public void Send1920x462Frame(byte[] jpegBytes)
        {
            if (jpegBytes == null || jpegBytes.Length == 0) return;
            int totalLen = 64 + jpegBytes.Length;
            if (totalLen > SLOT_SIZE) return;

            watchdogCounter++;
            if (watchdogCounter % 75 == 0)
            {
                try
                {
                    if (Process.GetProcessesByName("USBLCDNEW").Length == 0)
                    {
                        EnsureConnected(false);
                    }
                }
                catch { }
            }

            Buffer.BlockCopy(HeaderTemplate1920x462, 0, packetBuffer, 0, 64);
            byte[] lenBytes = BitConverter.GetBytes(jpegBytes.Length);
            Buffer.BlockCopy(lenBytes, 0, packetBuffer, 60, 4);
            Buffer.BlockCopy(jpegBytes, 0, packetBuffer, 64, jpegBytes.Length);

            if (accessor != null)
            {
                try
                {
                    accessor.WriteArray(0, PreWriteFlag, 0, 4);
                    accessor.WriteArray(SLOT_SIZE, packetBuffer, 0, totalLen);
                    accessor.WriteArray(0, PostWriteFlag, 0, 4);
                }
                catch { }
            }

            if (directHidReady && directHidDevice != null && directHidDevice.IsDeviceConnected)
            {
                try
                {
                    int offset = 0;
                    while (offset < totalLen)
                    {
                        int chunkLen = Math.Min(512, totalLen - offset);
                        byte[] chunk = new byte[chunkLen];
                        Buffer.BlockCopy(packetBuffer, offset, chunk, 0, chunkLen);
                        directHidDevice.SendMessage(new CommandMessage(chunk));
                        offset += chunkLen;
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            try { accessor?.Dispose(); } catch { }
            try { mmf?.Dispose(); } catch { }
            try { directHidDevice?.Disconnect(); } catch { }
        }
    }
}
