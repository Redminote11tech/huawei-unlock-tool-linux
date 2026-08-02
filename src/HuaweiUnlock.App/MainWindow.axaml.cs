using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HuaweiUnlocker.DIAGNOS;
using HuaweiUnlocker.TOOLS;
using HuaweiUnlocker.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static HuaweiUnlocker.FlashTool.FlashToolQClegacy;
using static HuaweiUnlocker.LangProc;

namespace HuaweiUnlocker.App
{
    public partial class MainWindow : Window, IUiHost
    {
        // Row view-models for the two DataGrids
        public class PartRow { public string Name { get; set; } public string Start { get; set; } public string Length { get; set; } }
        public class KirinRow { public string Name { get; set; } public string Length { get; set; } }

        private readonly ObservableCollection<PartRow> _parts = new();
        private readonly ObservableCollection<KirinRow> _kirin = new();

        private const string ConfigFile = "config.json";
        private Dictionary<string, string> _settings = new();
        private readonly Dictionary<string, string> _deviceUrls = new();
        private string _selectedPartition = "NaN";

        public MainWindow()
        {
            InitializeComponent();
            LangProc.Host = this;

            EnsureFolders();
            LoadSettings();

            PartList.ItemsSource = _parts;
            KirinFiles.ItemsSource = _kirin;

            VersionLbl.Text = $"Version [{APP_VERSION}] — Avalonia/.NET {Environment.Version}";
            EnvLbl.Text = $"OS: {RuntimeInformation.OSDescription}\nRuntime: {RuntimeInformation.FrameworkDescription}\n" +
                          $"Working dir: {Directory.GetCurrentDirectory()}";

            PopulateLanguages();
            PopulateLoaders();
            RefreshPortList();
            LoadDeviceListFromWeb();

            debug = _settings.TryGetValue("DEBUG", out var d) && d == "true";
            DebugChk.IsChecked = debug;

            ToolEmmcdl.Text = GetSetting("tool.emmcdl") ?? "emmcdl";
            ToolFhLoader.Text = GetSetting("tool.fh_loader") ?? "fh_loader";
            ToolMtkFlash.Text = GetSetting("tool.mtkflash") ?? "mtkflash";

            // Initial banner + tutorial lines (mirrors the original Window.Lang()).
            Language.CURRENTlanguage = _settings.TryGetValue("LANGUAGE", out var l) ? l : "English";
            LangBox.SelectedItem = Language.CURRENTlanguage;
            ApplyLanguageStrings();
        }

        // ================= IUiHost =================

        public void Log(string line) => Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });

        public void ClearLog() => Dispatcher.UIThread.Post(() => LogBox.Text = "");

        public void SetProgress(int value, int max) => Dispatcher.UIThread.Post(() =>
        {
            Prg.Maximum = max <= 0 ? 100 : max;
            Prg.Value = Math.Clamp(value, 0, (int)Prg.Maximum);
        });

        public void SetBusy(bool busy) => Dispatcher.UIThread.Post(() => Tabs.IsEnabled = !busy);

        public bool AutoLoader => Dispatcher.UIThread.Invoke(() => AutoLdrChk.IsChecked == true);
        public string SelectedLoader => Dispatcher.UIThread.Invoke(() => LoaderBox.SelectedItem as string ?? "");
        public bool Ufs => Dispatcher.UIThread.Invoke(() => UfsChk.IsChecked == true);

        public string GetSetting(string key) => _settings.TryGetValue(key, out var v) ? v : null;
        public void SetSetting(string key, string value) { _settings[key] = value; SaveSettings(); }

        // ================= setup helpers =================

        private static void EnsureFolders()
        {
            foreach (var f in new[] { "UnlockFiles", "Logs", "Languages", "Tools", "qc_boot" })
                Directory.CreateDirectory(f);
        }

        private void LoadSettings()
        {
            try { if (File.Exists(ConfigFile)) _settings = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(ConfigFile)) ?? new(); }
            catch { _settings = new(); }
        }

        private void SaveSettings()
        {
            try { File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(_settings, Formatting.Indented)); }
            catch { }
        }

        private void PopulateLanguages()
        {
            LangBox.Items.Clear();
            foreach (var f in Directory.GetFiles("Languages", "*.ini"))
                LangBox.Items.Add(Path.GetFileNameWithoutExtension(f));
            if (LangBox.Items.Count == 0) LangBox.Items.Add("English");
        }

        private void PopulateLoaders()
        {
            LoaderBox.Items.Clear();
            LoaderBox.Items.Add("");
            if (Directory.Exists("qc_boot"))
                foreach (var dir in Directory.GetDirectories("qc_boot"))
                    LoaderBox.Items.Add(Path.GetFileName(dir));
        }

        private void RefreshPortList()
        {
            var current = PortBox.SelectedItem as string;
            PortBox.Items.Clear();
            PortBox.Items.Add("");
            PortBox.Items.Add("Auto");
            foreach (var p in GETPORTLIST())
                PortBox.Items.Add(p.FullName);
            if (current != null && PortBox.Items.Contains(current)) PortBox.SelectedItem = current;
        }

        private void LoadDeviceListFromWeb()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    var text = http.GetStringAsync("https://werasik2aa.github.io/Huawei-Unlock-Tool/js/Data.js").GetAwaiter().GetResult();
                    var devices = new List<string>();
                    var hisi = new List<string>();
                    // Data.js holds MANY arrays (Devices, Tools, Loaders, GPTS, ...).
                    // Like the original, parse ONLY the "Devices" array: enter at its
                    // header and stop at the first "];" — otherwise loader/tool/GPT
                    // entries leak into the device dropdown as garbage.
                    bool inDevices = false;
                    foreach (var raw in text.Replace("\r", "").Split('\n'))
                    {
                        var line = raw.Trim();
                        if (!inDevices)
                        {
                            if (line.StartsWith("const Devices") && line.Contains("[")) inDevices = true;
                            continue;
                        }
                        if (line.Contains("];")) break;   // end of the Devices array
                        if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#")) continue;
                        var parts = line.Replace("\t", "").Replace("\"", "").Replace(",", "").Split('\'');
                        if (parts.Length < 2) continue;
                        var name = parts[0].Split(' ')[0];
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.StartsWith("KIRIN")) hisi.Add(name); else devices.Add(name);
                        _deviceUrls[name] = parts[1];
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var d in devices) DeviceBox.Items.Add(d);
                        foreach (var h in hisi) HisiBox.Items.Add(h);
                    });
                }
                catch { LOG(2, "WebCon"); }
                // Local unlock-file folders as fallback entries
                if (Directory.Exists("UnlockFiles"))
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var dir in Directory.GetDirectories("UnlockFiles"))
                        {
                            var f = Path.GetFileName(dir);
                            if (f.StartsWith("KIRIN")) { if (!HisiBox.Items.Contains(f)) HisiBox.Items.Add(f); }
                            else if (!DeviceBox.Items.Contains(f)) DeviceBox.Items.Add(f);
                        }
                    });
            });
        }

        private void ApplyLanguageStrings()
        {
            try
            {
                if (!Language.ReadLngFile()) return;
                LogBox.Text = $"Version [{APP_VERSION}] BETA{newline}(C) MOONGAMER (QUALCOMM UNLOCKER){newline}(C) MASHED-POTATOES (KIRIN UNLOCKER){newline}";
                foreach (var key in new[] { "SMAIN1", "SMAIN2", "SMAIN3", "MAIN1", "MAIN2", "MAIN3", "TutrQC", "TutrHI" })
                    if (Language.isExist(key)) LOG(0, key);
            }
            catch { }
        }

        // ================= file dialog helpers =================

        private async Task<string> PickOpen(string title, params (string, string[])[] filters)
        {
            var opts = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
            opts.FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Item1) { Patterns = f.Item2 }).ToList();
            var res = await StorageProvider.OpenFilePickerAsync(opts);
            return res.Count > 0 ? res[0].TryGetLocalPath() : null;
        }

        private async Task<string> PickSave(string title, string suggested = null)
        {
            var res = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
            return res?.TryGetLocalPath();
        }

        private async Task<bool> Confirm(string message, string title)
        {
            var dlg = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var yes = new Button { Content = "Yes", Width = 90, Margin = new(6) };
            var no = new Button { Content = "No", Width = 90, Margin = new(6) };
            var tcs = new TaskCompletionSource<bool>();
            yes.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
            no.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
            dlg.Content = new StackPanel
            {
                Margin = new(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0,0,0,12) },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { yes, no } }
                }
            };
            dlg.Closed += (_, _) => tcs.TrySetResult(false);
            await dlg.ShowDialog(this);
            return await tcs.Task;
        }

        private string PortText => PortBox.SelectedItem as string ?? "";
        private string LoaderArg => AutoLoader ? "" : SelectedLoader;

        // ================= left panel handlers =================

        private void RefreshPorts_Click(object s, RoutedEventArgs e) => RefreshPortList();
        private void AutoLdr_Changed(object s, RoutedEventArgs e) { if (LoaderBox != null) LoaderBox.IsEnabled = AutoLdrChk.IsChecked != true; }
        private void Debug_Changed(object s, RoutedEventArgs e) { debug = DebugChk.IsChecked == true; SetSetting("DEBUG", debug ? "true" : "false"); }

        private async void PickLoader_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select loader", ("Programmer files", new[] { "*.mbn", "*.elf", "*.hex" }), ("All files", new[] { "*" }));
            if (f != null) { if (!LoaderBox.Items.Contains(f)) LoaderBox.Items.Add(f); LoaderBox.SelectedItem = f; PrevFolder = f; }
        }

        private void ApplyLang_Click(object s, RoutedEventArgs e)
        {
            Language.CURRENTlanguage = LangBox.SelectedItem as string ?? "English";
            SetSetting("LANGUAGE", Language.CURRENTlanguage);
            ClearLog();
            ApplyLanguageStrings();
        }

        private void Identify_Click(object s, RoutedEventArgs e) => Run(() =>
        {
            LOG(0, "CheckCon", " [HISI]");
            var portHISI = DeviceInfo.Port = GETPORT("huawei usb com", PortText);
            LOG(0, "CheckCon", " [QCOM]");
            var portQC = DeviceInfo.Port = GETPORT("qdloader 9008", PortText);
            if (!portHISI.ComName.Equals("NaN")) LOG(0, "CPort", "[HISI] " + DeviceInfo.Port.FullName);
            if (!portQC.ComName.Equals("NaN"))
            {
                LOG(0, "CPort", "[QCOM] " + DeviceInfo.Port.FullName);
                GetIdentifier();
                LOG(0, "LoaderSearch");
                GuessMbnTest();
            }
            if (portQC.ComName == "NaN" && portHISI.ComName == "NaN") LOG(1, "NoDEVICEAnsw");
        });

        // ================= QCOM unlock =================

        private void Unlock_Click(object s, RoutedEventArgs e) => Run(() =>
        {
            SetProgress(0, 100);
            var device = (DeviceBox.SelectedItem as string ?? "").ToUpper();
            if (!device.Contains("-")) { LOG(0, "SelDev"); return; }
            var path = Path.Combine("UnlockFiles", device);
            if (!Directory.Exists(path))
            {
                LOG(2, "Unlock files for " + device + " not found under UnlockFiles/. Download/extract them first.");
                if (_deviceUrls.TryGetValue(device, out var url)) LOG(0, "URL: " + url);
                return;
            }
            if (!CheckDevice(LoaderArg, PortText)) return;
            DeviceInfo.Name = device;
            LOG(0, "PrcsUnl");
            Unlock(AutoLoader ? GuessMbn() : SelectedLoader, path);
            SetProgress(100, 100);
        });

        private void UnlockFrp_Click(object s, RoutedEventArgs e) => Run(() =>
        {
            SetProgress(0, 100);
            if (!CheckDevice(LoaderArg, PortText)) return;
            if (!UnlockFrp(AutoLoader ? GuessMbn() : SelectedLoader)) LOG(2, "FailFrp");
            else LOG(0, "SUCC_FrpUnlock");
            SetProgress(100, 100);
        });

        private async void EraseMemory_Click(object s, RoutedEventArgs e)
        {
            if (!await Confirm(Language.Get("ERmINFO"), Language.Get("CZdmg"))) return;
            Run(() =>
            {
                if (!CheckDevice(LoaderArg, PortText)) return;
                if (EraseMemory(AutoLoader ? GuessMbn() : SelectedLoader)) LOG(0, "EraseMS");
                else LOG(2, "EEraseMS");
                SetProgress(100, 100);
            });
        }

        // ================= QCOM partitions =================

        private void ReadGpt_Click(object s, RoutedEventArgs e) => Run(() =>
        {
            if (!CheckDevice(LoaderArg, PortText)) return;
            LOG(0, "ReadGPT");
            DeviceInfo.Partitions = new Dictionary<string, Partition>();
            if (ReadGPT(AutoLoader ? GuessMbn() : SelectedLoader))
            {
                LOG(0, "SUCC_ReadGPT");
                Dispatcher.UIThread.Post(() =>
                {
                    _parts.Clear();
                    foreach (var o in DeviceInfo.Partitions)
                        _parts.Add(new PartRow { Name = o.Key, Start = o.Value.BlockStart, Length = o.Value.BlockLength });
                });
                SetProgress(100, 100);
            }
            else LOG(2, "ERR_ReadGPT");
        });

        private void PartList_DoubleTapped(object s, RoutedEventArgs e)
        {
            if (PartList.SelectedItem is PartRow r)
            {
                _selectedPartition = r.Name;
                SelPartLbl.Text = r.Name;
                LOG(0, "PartSled", r.Name);
            }
        }

        private async void PartRead_Click(object s, RoutedEventArgs e)
        {
            if (_selectedPartition == "NaN" || !DeviceInfo.Partitions.ContainsKey(_selectedPartition)) { LOG(2, "Select a partition first."); return; }
            var f = await PickSave("Read partition to file", _selectedPartition + ".img");
            if (f == null) return;
            Run(() =>
            {
                int i = int.Parse(DeviceInfo.Partitions[_selectedPartition].BlockStart);
                int j = int.Parse(DeviceInfo.Partitions[_selectedPartition].BlockNumSectors);
                LOG(0, "EdPS", _selectedPartition + newline);
                Dump(i, j, _selectedPartition, AutoLoader ? GuessMbn() : SelectedLoader, f);
                SetProgress(100, 100);
            });
        }

        private async void PartWrite_Click(object s, RoutedEventArgs e)
        {
            if (_selectedPartition == "NaN") { LOG(2, "Select a partition first."); return; }
            var f = await PickOpen("Select image to write", ("All files", new[] { "*" }));
            if (f == null) return;
            Run(() =>
            {
                LOG(0, "EwPS", _selectedPartition + newline);
                Write(_selectedPartition, AutoLoader ? GuessMbn() : SelectedLoader, f);
                SetProgress(100, 100);
            });
        }

        private async void PartErase_Click(object s, RoutedEventArgs e)
        {
            if (_selectedPartition == "NaN") { LOG(2, "Select a partition first."); return; }
            if (!await Confirm(Language.Get("AreY") + _selectedPartition, Language.Get("CZdmg"))) return;
            Run(() =>
            {
                if (!CheckDevice(LoaderArg, PortText)) return;
                LOG(1, "ErPS", _selectedPartition);
                Erase(_selectedPartition, LoaderArg);
                SetProgress(100, 100);
            });
        }

        private async void Flash_Click(object s, RoutedEventArgs e)
        {
            bool raw = RawChk.IsChecked == true;
            if (raw)
            {
                var img = await PickOpen("Select raw image", ("Images", new[] { "*.img", "*.bin", "*.emmc" }), ("All files", new[] { "*" }));
                if (img == null) return;
                Run(() =>
                {
                    if (!CheckDevice(LoaderArg, PortText)) return;
                    LOG(0, "EemmcWPS", img);
                    FlashPartsRaw(AutoLoader ? GuessMbn() : SelectedLoader, img);
                });
            }
            else
            {
                var xml = await PickOpen("Select rawprogram xml", ("XML", new[] { "*.xml" }), ("All files", new[] { "*" }));
                if (xml == null) return;
                var dir = Path.GetDirectoryName(xml);
                var patch = Directory.GetFiles(dir, "*.xml").FirstOrDefault(f => f.Contains("patch")) ?? "";
                Run(() =>
                {
                    if (!CheckDevice(LoaderArg, PortText)) return;
                    LOG(0, "EemmcXML_WPS", dir);
                    FlashPartsXml(xml, patch, AutoLoader ? GuessMbn() : SelectedLoader, dir);
                });
            }
        }

        private async void DumpAll_Click(object s, RoutedEventArgs e)
        {
            var f = await PickSave("Dump storage to file", "full_dump.img");
            if (f == null) return;
            Run(() =>
            {
                if (!CheckDevice(LoaderArg, PortText)) return;
                LOG(0, "DumpTr", f);
                Dump(AutoLoader ? GuessMbn() : SelectedLoader, f);
                SetProgress(100, 100);
            });
        }

        private async void CreateGptFromDevice_Click(object s, RoutedEventArgs e)
        {
            var save = await PickSave("Save rawprogram0.xml", "rawprogram0.xml");
            if (save == null) return;
            Run(() =>
            {
                if (!CheckDevice(LoaderArg, PortText)) return;
                LOG(0, "ReadGPT");
                if (ReadGPT(AutoLoader ? GuessMbn() : SelectedLoader) && DeviceInfo.Partitions.Count > 0)
                {
                    LOG(0, "RrGPTXMLSPR", save);
                    WriteGPT_TO_XML(save, DeviceInfo.Partitions, false);
                    SetProgress(100, 100);
                }
                else LOG(2, "RrGPTXMLE", "ERR_ReadGPT");
            });
        }

        private async void CreateGptFromFile_Click(object s, RoutedEventArgs e)
        {
            var bin = await PickOpen("Select gpt_####0.bin", ("GPT bin", new[] { "*.bin", "*.img" }), ("All files", new[] { "*" }));
            if (bin == null) return;
            var save = await PickSave("Save rawprogram0.xml", "rawprogram0.xml");
            if (save == null) return;
            Run(() =>
            {
                var table = GET_GPT_FROM_FILE(bin, 512);
                if (table.Count > 0) { WriteGPT_TO_XML(save, table, false); SetProgress(100, 100); }
                else LOG(2, "RrGPTXMLE", "ERR_ReadGPTFile");
            });
        }

        // ---- Update.app (mirrors the original UnpBTN / FlashUpdAppBTN) ----

        // Extract only: UpdateApp.Unpack(path, 1). No device needed.
        private async void UpdateAppUnpack_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select update.app", ("Update.app", new[] { "*.app", "*.APP" }), ("All files", new[] { "*" }));
            if (f == null) return;
            Run(() =>
            {
                UpdateApp.unpacked = false;   // force a fresh extraction of this file
                UpdateApp.Unpack(f, 1);
            });
        }

        // Extract + flash each partition via EDL 9008: UpdateApp.Unpack(path, 2).
        // The original checked the device first, so we do too.
        private async void UpdateAppFlash_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select update.app", ("Update.app", new[] { "*.app", "*.APP" }), ("All files", new[] { "*" }));
            if (f == null) return;
            Run(() =>
            {
                if (!CheckDevice(LoaderArg, PortText)) return;
                UpdateApp.unpacked = false;
                UpdateApp.Unpack(f, 2);
            });
        }

        // ================= KIRIN =================

        private Task ConnectKirin()
        {
            return Task.Run(() =>
            {
                DeviceInfo.loadedhose = false;
                var device = (HisiBox.SelectedItem as string ?? "").ToUpper();
                if (string.IsNullOrEmpty(device)) { LOG(1, "HISISelectCpu"); return; }
                var path = Path.Combine("UnlockFiles", device, "manifest.xml");
                DeviceInfo = new IDentifyDev { CPUName = device.Replace("KIRIN", ""), Port = GETPORT("huawei usb com", PortText) };
                bool vcom = Dispatcher.UIThread.Invoke(() => VcomChk.IsChecked == true);
                if (vcom)
                {
                    LOG(0, "[VCOM] ", "CheckCon");
                    if (DeviceInfo.Port.ComName != "NaN" && File.Exists(path))
                    {
                        LOG(0, "CPort", "[VCOM] " + DeviceInfo.Port.FullName);
                        HISI.FlashBootloader(Bootloader.ParseBootloader(path), DeviceInfo.Port.ComName);
                        DeviceInfo.loadedhose = HISI.IsDeviceConnected(100);
                    }
                    else LOG(2, "[Huawei USB COM 1.0] ", "DeviceNotCon");
                }
                else
                {
                    LOG(0, "[Fastboot] ", "CheckCon");
                    DeviceInfo.loadedhose = HISI.IsDeviceConnected(3);
                }
                if (DeviceInfo.loadedhose)
                {
                    HISI.ReadInfo();
                    HISI.UnlockFBLOCK();
                    Dispatcher.UIThread.Post(() =>
                    {
                        BlKeyResult.Text = HISI.BLKEY;
                        HisiInfoLbl.Text = $"Build: {HISI.AVER}\nModel: {HISI.MODEL}\nVersion: {HISI.BNUM}\nFBLOCK: {HISI.FBLOCKSTATE}";
                    });
                    LOG(0, "DeviceInfoTag", " [Fastboot] " + DeviceInfo.CPUName);
                }
                else LOG(2, "DeviceNotCon", "in [FastBoot] or [Huawei USB COM 1.0]");
            });
        }

        private async void KirinConnect_Click(object s, RoutedEventArgs e) { SetBusy(true); try { await ConnectKirin(); } finally { SetBusy(false); LOG(0, "Done", DateTime.Now); } }

        private async void KirinUnlock_Click(object s, RoutedEventArgs e)
        {
            var key = BlKeyBox.Text ?? "";
            if (key.Length != 16) { LOG(2, "KeyLenghtERR", key.Length + " : 16"); return; }
            SetBusy(true);
            try
            {
                await ConnectKirin();
                if (DeviceInfo.loadedhose)
                {
                    LOG(-1, "=====UNLOCKER BL/FRP (KIRIN TESTPOINT)=====");
                    var res = HISI.WriteKEY(key.ToUpper());
                    Dispatcher.UIThread.Post(() => BlKeyResult.Text = res);
                    HISI.ReadAllMethods();
                    if (RebootChk.IsChecked == true) HISI.Reboot();
                    HISI.Disconnect();
                }
            }
            catch (Exception ex) { if (debug) LOG(2, ex.Message); }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        private async void KirinFrp_Click(object s, RoutedEventArgs e)
        {
            SetBusy(true);
            try { await ConnectKirin(); if (DeviceInfo.loadedhose) { HISI.UnlockFRP(); } else LOG(2, "FailFrp"); }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        private async void KirinReboot_Click(object s, RoutedEventArgs e)
        {
            SetBusy(true);
            try { await ConnectKirin(); if (DeviceInfo.loadedhose) HISI.Reboot(); }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        private async void KirinSelectFirmware_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select firmware", ("Firmware", new[] { "*.img", "*.app", "*.APP" }), ("All files", new[] { "*" }));
            if (f == null) return;
            SetBusy(true);
            try
            {
                await Task.Run(() =>
                {
                    if (f.ToLower().EndsWith(".app")) UpdateApp.Unpack(f, 3);
                    else if (f.ToLower().EndsWith(".img"))
                    {
                        var dir = new DirectoryInfo(Path.Combine("UnlockFiles", "UpdateAPP"));
                        var gpt = dir.Exists ? dir.GetFiles("*gpt*.img") : Array.Empty<FileInfo>();
                        if (gpt.Length == 0) { LOG(2, "NotFoundF", "GPT.img"); UpdateApp.ReadFilesInDirAsPartitions(); }
                        else DeviceInfo.Partitions = GET_GPT_FROM_FILE(gpt[0].FullName, 512);
                    }
                });
                Dispatcher.UIThread.Post(() =>
                {
                    _kirin.Clear();
                    foreach (var p in DeviceInfo.Partitions) _kirin.Add(new KirinRow { Name = p.Key, Length = p.Value.BlockLength });
                });
            }
            catch { LOG(2, "Selected file not an Update.APP"); }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        private void KirinFlash_Click(object s, RoutedEventArgs e) => Run(() =>
        {
            if (!DeviceInfo.loadedhose) { LOG(2, "[FASTBOOT] ", "DeviceNotCon"); return; }
            if (DeviceInfo.Partitions.Count <= 1) { LOG(2, "ErrBin"); return; }
            if (!HISI.FBLOCK) { LOG(2, "HISIInfoS"); return; }
            try
            {
                if (HISI.fb.Connect(10))
                {
                    var gpt = Path.Combine("UnlockFiles", "UpdateAPP", "hisiufs_gpt.img");
                    if (File.Exists(gpt)) { HISI.fb.UploadData(gpt, "partition"); HISI.fb.UploadData(gpt, "ptable"); }
                    foreach (var a in DeviceInfo.Partitions)
                    {
                        LOG(0, "Writer", a.Key);
                        HISI.fb.UploadData(Path.Combine("UnlockFiles", "UpdateAPP", a.Key + ".img"), a.Key);
                    }
                    LOG(0, "Done", DateTime.Now);
                }
                HISI.fb.Disconnect();
            }
            catch (Exception ex) { LOG(2, "Unknown", ex.Message); }
        });

        private async void KirinWritePart_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select image", ("All files", new[] { "*" }));
            if (f == null) return;
            Run(() =>
            {
                if (!HISI.IsDeviceConnected()) HISI.fb.Connect(10);
                if (!HISI.IsDeviceConnected()) return;
                LOG(0, "Writer", f);
                HISI.fb.UploadData(f, Path.GetFileNameWithoutExtension(f));
                LOG(0, "Done", DateTime.Now);
            });
        }

        // ================= Oeminfo =================

        private async void OemDecompile_Click(object s, RoutedEventArgs e)
        {
            var f = await PickOpen("Select update.app", ("Update.app", new[] { "*.app", "*.APP" }), ("All files", new[] { "*" }));
            if (f == null) return;
            OemList.Items.Clear();
            OemInfoTool.data.Clear(); OemInfoTool.Offsets.Clear(); OemInfoTool.DevStats.Clear();
            SetBusy(true);
            try
            {
                await Task.Run(() => OemInfoTool.Decompile(f));
                foreach (var item in OemInfoTool.data) OemList.Items.Add(item);
                OemCompileBtn.IsEnabled = true;
            }
            catch { LOG(2, "FileWrong"); }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        private void OemList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (OemList.SelectedItem is not string item || OemInfoTool.data.Count == 0) return;
            Run(() =>
            {
                var p = Path.Combine("UnlockFiles", "OemInfoData", item);
                if (!File.Exists(p)) return;
                var a = File.ReadAllBytes(p);
                if (TrimChk.IsChecked == true) a = CRC.HexStringToBytes(CRC.BytesToHexString(a).Replace("FF", ""));
                var dump = CRC.HexDump(a);
                Dispatcher.UIThread.Post(() => OemContent.Text = dump);
            });
        }

        private async void OemCompile_Click(object s, RoutedEventArgs e)
        {
            SetBusy(true);
            try
            {
                LOG(0, "Compiling oeminfo.img");
                await Task.Run(() => OemInfoTool.Compile(Path.Combine("UnlockFiles", "OemInfoData") + Path.DirectorySeparatorChar,
                                                          Path.Combine("UnlockFiles", "oeminfo-unsigned-unhashed.img")));
            }
            finally { SetBusy(false); LOG(0, "Done", DateTime.Now); }
        }

        // ================= footer =================

        private void SaveToolPaths_Click(object s, RoutedEventArgs e)
        {
            SetSetting("tool.emmcdl", (ToolEmmcdl.Text ?? "").Trim());
            SetSetting("tool.fh_loader", (ToolFhLoader.Text ?? "").Trim());
            SetSetting("tool.mtkflash", (ToolMtkFlash.Text ?? "").Trim());
            LOG(0, "Saved tool paths to config.json");
        }

        private void ClearLog_Click(object s, RoutedEventArgs e) => ClearLog();

        private void Cancel_Click(object s, RoutedEventArgs e)
        {
            try { CurProcess?.Kill(); } catch { }
            try { ct.Cancel(); ct = new(); token = ct.Token; } catch { }
            DeviceInfo = new IDentifyDev();
            _parts.Clear(); _kirin.Clear();
            _selectedPartition = "NaN"; SelPartLbl.Text = "(double-click a row)";
            UpdateApp.unpacked = false;
            HISI.BSN = HISI.AVER = HISI.BNUM = HISI.MODEL = "NaN";
            try { HISI.fb.Disconnect(); } catch { }
            SetBusy(false);
            LOG(1, "Canceled");
        }

        // Fire-and-forget a blocking core operation on a worker thread.
        private void Run(Action work) => Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { LOG(2, "Unknown", ex.Message); }
        });
    }
}
