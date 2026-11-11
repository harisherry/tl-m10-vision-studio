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
    /// 1:1 1920x462 borderless draggable desktop overlay window.
    /// </summary>
    public class PopoutPreviewForm : Form
    {
        private readonly PictureBox pictureBox;
        private Point dragStart;
        private bool isDragging = false;

        public PopoutPreviewForm(Icon appIcon)
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "TL-M10 Vision 1:1 Live Output (1920 Ã— 462) â€” Drag to Move, Double-Click to Close";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);
            ClientSize = new Size(1920, 462);
            BackColor = Color.Black;
            TopMost = true;
            ShowInTaskbar = false;
            if (appIcon != null) Icon = appIcon;

            pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };

            pictureBox.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = true;
                    dragStart = e.Location;
                }
            };
            pictureBox.MouseMove += (s, e) =>
            {
                if (isDragging)
                {
                    Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y);
                }
            };
            pictureBox.MouseUp += (s, e) => isDragging = false;
            pictureBox.DoubleClick += (s, e) => Hide();

            Controls.Add(pictureBox);
        }

        public void UpdateFrame(Bitmap src)
        {
            try
            {
                var old = pictureBox.Image;
                pictureBox.Image = (Bitmap)src.Clone();
                old?.Dispose();
            }
            catch { }
        }
    }
}
