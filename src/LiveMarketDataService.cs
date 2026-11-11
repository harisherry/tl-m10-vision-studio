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
    /// Background service that polls live prices & intraday sparklines for:
    /// 1) Bitcoin (BTC) & Top Cryptocurrencies (Binance + CoinGecko fallback, in USD & Pakistani Rupees Rs.)
    /// 2) Pakistan Stock Exchange (PSX) KSE-100 Index & Top PSX Blue-Chip Stocks in Rs. (from official dps.psx.com.pk)
    /// 3) US Wall Street Stocks (NVDA, AAPL, TSLA, MSFT, AMZN, META, GOOGL, AMD from Yahoo Finance Chart API)
    /// </summary>
    public class LiveMarketDataService : IDisposable
    {
        private readonly HttpClient http;
        private CancellationTokenSource cts;
        private double usdToPkr = 278.65;
        private string[] psxSymbols = new[] { "SYS", "LUCK", "OGDC", "HUBC", "MEBL", "ENGRO", "PPL", "FFC", "PSO", "MARI", "TRG", "HBL" };
        private string[] usSymbols = new[] { "NVDA", "AAPL", "TSLA", "MSFT", "AMZN", "META", "GOOGL", "AMD" };
        private string[] cryptoSymbols = new[] { "BTC", "ETH", "SOL", "BNB", "XRP", "DOGE", "ADA" };

        public JObject LatestMarketData { get; private set; }
        public event Action<JObject> OnMarketDataUpdated;

        private static readonly Dictionary<string, string> PsxCompanyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYS"] = "Systems Limited",
            ["LUCK"] = "Lucky Cement",
            ["OGDC"] = "Oil & Gas Dev Co",
            ["HUBC"] = "Hub Power Co",
            ["MEBL"] = "Meezan Bank",
            ["ENGRO"] = "Engro Holdings",
            ["PPL"] = "Pakistan Petroleum",
            ["FFC"] = "Fauji Fertilizer",
            ["PSO"] = "Pakistan State Oil",
            ["MARI"] = "Mari Energies",
            ["TRG"] = "TRG Pakistan",
            ["HBL"] = "Habib Bank Ltd",
            ["UBL"] = "United Bank Ltd",
            ["MCB"] = "MCB Bank Ltd",
            ["EFERT"] = "Engro Fertilizers",
            ["POL"] = "Pakistan Oilfields",
            ["ATRL"] = "Attock Refinery",
            ["DGKC"] = "D.G. Khan Cement",
            ["PAEL"] = "Pak Elektron",
            ["SEARL"] = "The Searle Co"
        };

        private static readonly Dictionary<string, string> UsCompanyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NVDA"] = "NVIDIA Corp",
            ["AAPL"] = "Apple Inc",
            ["TSLA"] = "Tesla Inc",
            ["MSFT"] = "Microsoft Corp",
            ["AMZN"] = "Amazon.com Inc",
            ["META"] = "Meta Platforms",
            ["GOOGL"] = "Alphabet Inc",
            ["AMD"] = "Adv Micro Devices",
            ["NFLX"] = "Netflix Inc",
            ["SPY"] = "S&P 500 ETF",
            ["QQQ"] = "NASDAQ 100 ETF"
        };

        public LiveMarketDataService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        }

        public void UpdateSymbols(string psxCsv, string usCsv, string cryptoCsv, double? customPkrRate)
        {
            if (!string.IsNullOrWhiteSpace(psxCsv))
            {
                var arr = psxCsv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
                if (arr.Length > 0) psxSymbols = arr;
            }
            if (!string.IsNullOrWhiteSpace(usCsv))
            {
                var arr = usCsv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
                if (arr.Length > 0) usSymbols = arr;
            }
            if (!string.IsNullOrWhiteSpace(cryptoCsv))
            {
                var arr = cryptoCsv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
                if (arr.Length > 0) cryptoSymbols = arr;
            }
            if (customPkrRate.HasValue && customPkrRate.Value > 50)
            {
                usdToPkr = customPkrRate.Value;
            }
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Task.Run(async () =>
            {
                await FetchUsdPkrRateAsync();
                while (!token.IsCancellationRequested)
                {
                    await FetchAllNowAsync();
                    try
                    {
                        await Task.Delay(14000, token);
                    }
                    catch { break; }
                }
            }, token);
        }

        private async Task FetchUsdPkrRateAsync()
        {
            try
            {
                string json = await http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
                var obj = JObject.Parse(json);
                double? pkr = obj["rates"]?["PKR"]?.Value<double>();
                if (pkr.HasValue && pkr.Value > 100)
                {
                    usdToPkr = Math.Round(pkr.Value, 2);
                }
            }
            catch { }
        }

        public async Task FetchAllNowAsync()
        {
            try
            {
                var cryptoTask = FetchCryptoDataAsync();
                var btcChartTask = FetchBtcChartAsync();
                var psxTask = FetchPsxDataAsync();
                var usTask = FetchUsStocksDataAsync();

                await Task.WhenAll(cryptoTask, btcChartTask, psxTask, usTask);

                var payload = new JObject
                {
                    ["usdToPkr"] = usdToPkr,
                    ["lastUpdated"] = DateTime.Now.ToString("HH:mm:ss")
                };

                if (cryptoTask.Result != null && cryptoTask.Result.Count > 0)
                    payload["crypto"] = cryptoTask.Result;
                if (btcChartTask.Result != null && btcChartTask.Result.Count > 2)
                    payload["btcChart"] = btcChartTask.Result;
                if (psxTask.Result != null)
                {
                    if (psxTask.Result["kse100"] != null) payload["kse100"] = psxTask.Result["kse100"];
                    if (psxTask.Result["psxStocks"] != null) payload["psxStocks"] = psxTask.Result["psxStocks"];
                }
                if (usTask.Result != null && usTask.Result.Count > 0)
                    payload["usStocks"] = usTask.Result;

                LatestMarketData = payload;
                OnMarketDataUpdated?.Invoke(payload);
            }
            catch { }
        }

        private async Task<JArray> FetchCryptoDataAsync()
        {
            var result = new JArray();
            try
            {
                foreach (string sym in cryptoSymbols.Take(7))
                {
                    string pair = sym.EndsWith("USDT") ? sym : (sym + "USDT");
                    try
                    {
                        string raw = await http.GetStringAsync($"https://api.binance.com/api/v3/ticker/24hr?symbol={pair}");
                        var jo = JObject.Parse(raw);
                        double price = jo.Value<double?>("lastPrice") ?? 0;
                        double chgPct = jo.Value<double?>("priceChangePercent") ?? 0;
                        double high = jo.Value<double?>("highPrice") ?? price;
                        double low = jo.Value<double?>("lowPrice") ?? price;
                        double open = jo.Value<double?>("openPrice") ?? price;

                        var spark = new JArray { open, (open + low) / 2.0, low + (high - low) * 0.4, (open + price) / 2.0, high * 0.995, price };
                        result.Add(new JObject
                        {
                            ["symbol"] = sym,
                            ["name"] = sym,
                            ["priceUsd"] = price,
                            ["change24h"] = chgPct,
                            ["high24h"] = high,
                            ["low24h"] = low,
                            ["spark"] = spark
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private async Task<JArray> FetchBtcChartAsync()
        {
            var chart = new JArray();
            try
            {
                string raw = await http.GetStringAsync("https://api.binance.com/api/v3/klines?symbol=BTCUSDT&interval=15m&limit=36");
                var arr = JArray.Parse(raw);
                foreach (var k in arr)
                {
                    if (k is JArray candle && candle.Count > 4)
                    {
                        if (double.TryParse(candle[4].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double closePrice))
                        {
                            chart.Add(closePrice);
                        }
                    }
                }
            }
            catch { }
            return chart;
        }

        private async Task<JObject> FetchPsxDataAsync()
        {
            var res = new JObject();
            try
            {
                // 1. Fetch KSE-100 Index intraday timeseries from official PSX Data Portal
                try
                {
                    string kseRaw = await http.GetStringAsync("https://dps.psx.com.pk/timeseries/int/KSE100");
                    var kseObj = JObject.Parse(kseRaw);
                    if (kseObj["data"] is JArray dataArr && dataArr.Count > 0)
                    {
                        // PSX timeseries data is ordered newest-first: [[timestamp, price, vol], ...]
                        double latestVal = dataArr[0][1].Value<double>();
                        double openVal = dataArr[dataArr.Count - 1][1].Value<double>();
                        double chg = latestVal - openVal;
                        double chgPct = openVal > 0 ? (chg / openVal) * 100.0 : 0.0;

                        var spark = new JArray();
                        int step = Math.Max(1, dataArr.Count / 28);
                        for (int i = dataArr.Count - 1; i >= 0; i -= step)
                        {
                            spark.Add(dataArr[i][1].Value<double>());
                        }
                        spark.Add(latestVal);

                        res["kse100"] = new JObject
                        {
                            ["value"] = latestVal,
                            ["change"] = chg,
                            ["changePct"] = chgPct,
                            ["spark"] = spark
                        };
                    }
                }
                catch { }

                // 2. Fetch individual PSX Blue-Chip stocks from dps.psx.com.pk/timeseries/int/{SYMBOL}
                var stocksArr = new JArray();
                foreach (string sym in psxSymbols.Take(8))
                {
                    try
                    {
                        string symRaw = await http.GetStringAsync($"https://dps.psx.com.pk/timeseries/int/{sym}");
                        var symObj = JObject.Parse(symRaw);
                        if (symObj["data"] is JArray sArr && sArr.Count > 0)
                        {
                            double latest = sArr[0][1].Value<double>();
                            double first = sArr[sArr.Count - 1][1].Value<double>();
                            double chg = latest - first;
                            double chgPct = first > 0 ? (chg / first) * 100.0 : 0.0;

                            var spark = new JArray();
                            int step = Math.Max(1, sArr.Count / 14);
                            for (int i = sArr.Count - 1; i >= 0; i -= step)
                            {
                                spark.Add(sArr[i][1].Value<double>());
                            }
                            spark.Add(latest);

                            PsxCompanyNames.TryGetValue(sym, out string compName);
                            stocksArr.Add(new JObject
                            {
                                ["symbol"] = sym,
                                ["name"] = compName ?? sym,
                                ["price"] = latest,
                                ["change"] = chg,
                                ["changePct"] = chgPct,
                                ["spark"] = spark
                            });
                        }
                    }
                    catch { }
                }

                if (stocksArr.Count > 0)
                {
                    res["psxStocks"] = stocksArr;
                }
            }
            catch { }
            return res;
        }

        private async Task<JArray> FetchUsStocksDataAsync()
        {
            var stocksArr = new JArray();
            try
            {
                foreach (string sym in usSymbols.Take(8))
                {
                    try
                    {
                        string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{sym}?interval=5m&range=1d";
                        string raw = await http.GetStringAsync(url);
                        var jo = JObject.Parse(raw);
                        var result0 = jo["chart"]?["result"]?[0];
                        if (result0 != null)
                        {
                            var meta = result0["meta"];
                            double price = meta?.Value<double?>("regularMarketPrice") ?? 0;
                            double prevClose = meta?.Value<double?>("chartPreviousClose") ?? meta?.Value<double?>("previousClose") ?? price;
                            double chg = price - prevClose;
                            double chgPct = prevClose > 0 ? (chg / prevClose) * 100.0 : 0.0;

                            var spark = new JArray();
                            var closes = result0["indicators"]?["quote"]?[0]?["close"] as JArray;
                            if (closes != null && closes.Count > 0)
                            {
                                int step = Math.Max(1, closes.Count / 16);
                                for (int i = 0; i < closes.Count; i += step)
                                {
                                    if (closes[i].Type == JTokenType.Float || closes[i].Type == JTokenType.Integer)
                                    {
                                        spark.Add(closes[i].Value<double>());
                                    }
                                }
                            }
                            if (spark.Count < 2)
                            {
                                spark.Add(prevClose);
                                spark.Add(price);
                            }

                            UsCompanyNames.TryGetValue(sym, out string compName);
                            stocksArr.Add(new JObject
                            {
                                ["symbol"] = sym,
                                ["name"] = compName ?? sym,
                                ["price"] = price,
                                ["change"] = chg,
                                ["changePct"] = chgPct,
                                ["spark"] = spark
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return stocksArr;
        }

        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            try { http?.Dispose(); } catch { }
        }
    }
}
