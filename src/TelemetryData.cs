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
    public class TelemetryData
    {
        public double cpuTemp = 42;
        public double cpuUsage = 12;
        public double cpuClock = 4650;
        public double cpuPower = 45;
        public double gpuTemp = 46;
        public double gpuUsage = 8;
        public double gpuClock = 1950;
        public double gpuPower = 52;
        public double ramUsage = 38;
        public double ramUsedGb = 12.2;
        public double netDownKbps = 0;
        public double netUpKbps = 0;
    }
}
