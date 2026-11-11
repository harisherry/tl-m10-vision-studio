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
    public class WasapiSpectrumCapture : IDisposable
    {
        private WasapiLoopbackCapture capture;
        private SoundInSource soundInSource;
        private SingleBlockNotificationStream notificationStream;
        private IWaveSource finalSource;
        private FftProvider fftProvider;
        private readonly float[] fftBuffer = new float[1024];
        private readonly double[] smoothedSpectrum = new double[64];
        private readonly object syncLock = new object();

        public void Start()
        {
            try
            {
                capture = new WasapiLoopbackCapture();
                capture.Initialize();
                soundInSource = new SoundInSource(capture);

                ISampleSource sampleSource = soundInSource.ToSampleSource();
                fftProvider = new FftProvider(sampleSource.WaveFormat.Channels, FftSize.Fft1024);

                notificationStream = new SingleBlockNotificationStream(sampleSource);
                notificationStream.SingleBlockRead += (s, e) =>
                {
                    fftProvider.Add(e.Left, e.Right);
                };

                finalSource = notificationStream.ToWaveSource();
                byte[] readBuf = new byte[finalSource.WaveFormat.BytesPerSecond / 8];
                soundInSource.DataAvailable += (s, e) =>
                {
                    while (finalSource.Read(readBuf, 0, readBuf.Length) > 0) { }
                };

                capture.Start();
            }
            catch { }
        }

        public double[] GetSpectrum64()
        {
            double[] result = new double[64];
            try
            {
                if (fftProvider != null && fftProvider.GetFftData(fftBuffer))
                {
                    lock (syncLock)
                    {
                        for (int i = 0; i < 64; i++)
                        {
                            int startBin = 1 + (int)(Math.Pow(i / 64.0, 1.75) * 180);
                            int endBin = Math.Max(startBin + 1, 1 + (int)(Math.Pow((i + 1) / 64.0, 1.75) * 180));

                            float sum = 0f;
                            int count = 0;
                            for (int b = startBin; b <= endBin && b < 512; b++)
                            {
                                sum += fftBuffer[b];
                                count++;
                            }
                            float avg = count > 0 ? sum / count : 0f;
                            double scaled = Math.Min(1.0, Math.Sqrt(avg) * (2.6 + i * 0.04));
                            smoothedSpectrum[i] = smoothedSpectrum[i] * 0.55 + scaled * 0.45;
                            result[i] = Math.Round(smoothedSpectrum[i], 3);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        public void Dispose()
        {
            try { capture?.Stop(); } catch { }
            try { finalSource?.Dispose(); } catch { }
            try { soundInSource?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
        }
    }
}
