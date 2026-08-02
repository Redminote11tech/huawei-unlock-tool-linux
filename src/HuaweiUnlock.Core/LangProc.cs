using HuaweiUnlocker.DIAGNOS;
using HuaweiUnlocker.FlashTool;
using HuaweiUnlocker.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HuaweiUnlocker
{
    /// <summary>
    /// Platform-neutral port of the original LangProc god-object. The UI-touching
    /// members now delegate to <see cref="IUiHost"/> (set once at startup) instead
    /// of poking WinForms controls, and Windows-only bits (WMI port enumeration,
    /// registry, '\\' paths) are replaced with Linux-friendly equivalents.
    /// </summary>
    public static class LangProc
    {
        public const string APP_VERSION = "33F-linux";

        // The active front-end. Defaults to a head-less host so the core can run
        // in tests/CLI before a GUI attaches.
        public static IUiHost Host = new NullUiHost();

        public static string log, loge, newline = Environment.NewLine, PrevFolder = "UnlockFiles";
        public static StreamWriter se;
        private static readonly object _seLock = new object();

        public static IDentifyDev DeviceInfo = new IDentifyDev();
        public static Task CurTask;
        public static CancellationTokenSource ct = new CancellationTokenSource();
        public static CancellationToken token = ct.Token;
        public static Process CurProcess;
        public static bool debug = false;

        static LangProc()
        {
            try
            {
                Directory.CreateDirectory("Logs");
                se = new StreamWriter(Path.Combine("Logs", "session.log"), true) { AutoFlush = true };
            }
            catch { se = StreamWriter.Null; }
        }

        public class Port_D
        {
            public string ComName;
            public string FullName;
            public string Vid = "";
            public string Pid = "";
        }
        public struct Partition
        {
            public string BlockStart;
            public string BlockEnd;
            public string BlockLength;
            public string BlockNumSectors;
            public string BlockBytes;
        }
        public class IDentifyDev
        {
            public string BSN = "NaN";
            public string BUILD = "NaN";
            public string VERSION = "NaN";
            public string SerialNum = "NaN";
            public string HWID = "NaN";
            public string SWID = "NaN";
            public string PK_HASH = "NaN";
            public string SBLV = "NaN";
            public string Name = "Unknown";
            public string CPUName = "Unknown";
            public Dictionary<string, Partition> Partitions = new Dictionary<string, Partition>();
            public bool loadedhose = false;
            public Port_D Port = new Port_D();
        }

        public static bool SyncRUN(string command, string subcommand)
        {
            Host.SetBusy(true);
            try
            {
                // Preflight: give a clear, actionable message instead of a raw
                // "No such file or directory" when the external helper isn't installed.
                if (!Tools.Exists(command))
                {
                    LOG(2, "'" + command + "' not found. Install it (emmcdl / fh_loader — see packaging/) or set its path in the Debug tab.");
                    return false;
                }
                // emmcdl defaults to eMMC and the original tool only wired the UFS
                // checkbox to fh_loader, so every emmcdl call (loader upload, GPT read,
                // flash, dump) targeted eMMC. On a UFS device that fails storage-init
                // ("EMMC GPT empty") and can stall the loader. Pass the memory type.
                if (Host.Ufs
                    && Path.GetFileName(command).StartsWith("emmcdl", StringComparison.OrdinalIgnoreCase)
                    && !subcommand.Contains("-MemoryName"))
                {
                    subcommand += " -MemoryName ufs";
                    if (debug) LOG(0, "[UFS] emmcdl -> -MemoryName ufs");
                }
                log = "SUCCESS";
                if (debug) LOG(0, command + newline + subcommand);
                Process p = CurProcess = new Process();
                p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.FileName = command;
                p.StartInfo.Arguments = subcommand;
                p.Start();
                // The tool used to swallow stderr, so emmcdl/fh_loader errors looked
                // like a silent hang. Surface them in the log as they arrive.
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) LOG(2, "[err] " + e.Data); };
                p.BeginErrorReadLine();
                string outtext = "";
                // Inactivity watchdog: the original blocked forever on ReadLine, so a
                // stuck emmcdl/fh_loader (device not in EDL, port held, no loader...)
                // froze the whole tool. Abort if no output arrives for this long.
                const int InactivityMs = 90000;
                while (true)
                {
                    var readTask = p.StandardOutput.ReadLineAsync();
                    if (!readTask.Wait(InactivityMs))
                    {
                        LOG(2, "No response for " + (InactivityMs / 1000) + "s — aborting '" + Path.GetFileName(command) +
                               "'. Check: device really in EDL (05c6:9008), ModemManager stopped, cable/port, and that a loader is selected.");
                        try { p.Kill(true); } catch { }
                        return false;
                    }
                    outtext = readTask.Result;
                    if (outtext == null) break;
                    if (outtext.ToLower().Contains("partition name:"))
                    {
                        string[] partitionDATA = outtext.Split(' ');
                        Partition ps = new Partition()
                        {
                            BlockStart = partitionDATA[6],
                            BlockEnd = "" + int.Parse(partitionDATA[6]) + int.Parse(partitionDATA[10]),
                            BlockBytes = "512",
                            BlockNumSectors = partitionDATA[10],
                            BlockLength = (int.Parse(partitionDATA[10]) / 2).ToString()
                        };
                        if (debug) LOG(0, "Partition:", partitionDATA[3]);
                        DeviceInfo.Partitions.Add(partitionDATA[3], ps);
                    }
                    if (outtext.Contains("%") || outtext.ToLower().Contains("remaining"))
                    {
                        int percent = 1;
                        if (outtext.Contains("%"))
                            percent = int.Parse(outtext.Split(' ').Last().Replace("%}", "").Split('.')[0]);
                        else if (outtext.Contains("remaining"))
                        {
                            int sR = percent = int.Parse(outtext.Split(' ')[2]);
                            int dS = FlashToolQClegacy.CurPartLenght - int.Parse(outtext.Split(' ')[2]);
                            if (FlashToolQClegacy.CurPartLenght != 0)
                                percent = (int)Math.Round((double)(100 * dS / FlashToolQClegacy.CurPartLenght));
                            if (percent <= 0) percent = 1;
                        }
                        LOG(0, Language.Get("Percent"), percent + "%");
                        Progress(percent);
                    }
                    if (outtext.StartsWith("SerialNumber"))
                        DeviceInfo.SerialNum = outtext.Split(' ')[1];
                    if (outtext.StartsWith("MSM_HW_ID"))
                        DeviceInfo.CPUName = DataS.IdentifyCPUbyID(DeviceInfo.HWID = outtext.Split(' ')[1]);
                    if (outtext.StartsWith("OEM_PK_HASH"))
                        DeviceInfo.PK_HASH = outtext.Split(' ')[1];
                    if (outtext.Contains("SBL SW Version"))
                        DeviceInfo.SBLV = outtext.Split(' ')[3];
                    log = log + newline + outtext;
                    if (!outtext.Contains("remaining"))
                        if (debug) LOG(0, newline + outtext);
                    Thread.Sleep(5);
                }
                p.WaitForExit();
                p.Close();
                p.Dispose();
                LOG(0, "Done", DateTime.Now);
                return !isError(log);
            }
            finally
            {
                Host.SetBusy(false);
            }
        }

        public static bool isError(string i)
        {
            i = i.ToLower();
            if (i.Contains("the operation completed successfully") || i.Contains("success")) return false;
            if (i.Contains("failed") || i.Contains("error") || i.Contains("error setting com port timeouts") || i.Contains("fail") || i.Contains("status: 2") || i.Contains("failed to write hello response back to device") || i.Contains("failed to open com port")) return true;
            return false;
        }

        public static bool LOG(int o, object i, object j = null, string sepa = " ")
        {
            string state = "";
            j = j == null ? "" : j;
            switch (o)
            {
                default: state = ""; break;
                case 0: state = Language.Get("Info"); break;
                case 1: state = Language.Get("Warning"); break;
                case 2: state = Language.Get("Error"); break;
            }
            i = Language.isExist(i.ToString()) ? Language.Get(i.ToString()) : i;
            i = i.ToString().Contains("/n") ? i.ToString() : i;
            j = Language.isExist(j.ToString()) ? Language.Get(j.ToString()) : j;
            j = j.ToString().Contains("/n") ? i.ToString() : j;
            try
            {
                i = string.Join(newline, Regex.Split((string)i, @"(?:\r\n|\n|\r)"));
                j = string.Join(newline, Regex.Split((string)j, @"(?:\r\n|\n|\r)"));
            }
            catch { }
            string line = (newline + state + i + sepa + j);
            Host.Log(line);
            lock (_seLock) { se.WriteLine(line); }
            return true;
        }

        // ---- Linux serial-port enumeration (replaces the WMI Win32_PnPEntity query) ----
        //
        // The original code matched USB device *names* (e.g. "qdloader 9008") reported by
        // Windows and returned the COMx string. On Linux we enumerate /dev/serial/by-id
        // (whose symlink names carry the USB product string) plus raw /dev/ttyUSB*/ttyACM*,
        // and treat the device path as the "ComName".
        private static List<Port_D> EnumeratePorts()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Port_D>();
            var devs = new List<string>();
            try
            {
                if (Directory.Exists("/dev"))
                {
                    devs.AddRange(Directory.GetFiles("/dev", "ttyUSB*"));
                    devs.AddRange(Directory.GetFiles("/dev", "ttyACM*"));
                }
            }
            catch { }
            try { devs.AddRange(SerialPort.GetPortNames()); } catch { }

            foreach (var dev in devs)
            {
                if (!seen.Add(dev)) continue;
                var (vid, pid, friendly) = UdevInfo(dev);
                string label = string.IsNullOrEmpty(friendly) ? dev : friendly + " (" + dev + ")";
                if (!string.IsNullOrEmpty(vid)) label += " [" + vid + ":" + pid + "]";
                result.Add(new Port_D { ComName = dev, FullName = label, Vid = vid, Pid = pid });
            }
            return result;
        }

        // Query udev for a tty's USB VID/PID and a human name. Falls back to blanks
        // if udevadm is unavailable, so callers still get the raw /dev path.
        private static (string vid, string pid, string friendly) UdevInfo(string dev)
        {
            try
            {
                var psi = new ProcessStartInfo("udevadm", "info -q property -n " + dev)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                string Get(string k)
                {
                    var m = Regex.Match(outp, "^" + k + "=(.*)$", RegexOptions.Multiline);
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
                string vendor = Get("ID_VENDOR_FROM_DATABASE");
                if (string.IsNullOrEmpty(vendor)) vendor = Get("ID_VENDOR");
                string model = Get("ID_MODEL_FROM_DATABASE");
                if (string.IsNullOrEmpty(model)) model = Get("ID_MODEL");
                return (Get("ID_VENDOR_ID").ToLower(), Get("ID_MODEL_ID").ToLower(),
                        (vendor + " " + model).Trim());
            }
            catch { return ("", "", ""); }
        }

        // The two Qualcomm helpers disagree on the port argument format on Linux:
        //   * fh_loader does open(value)         -> wants the full path, e.g. /dev/ttyUSB0
        //   * emmcdl does atoi(value) then
        //     sprintf("/dev/ttyUSB%d", n)        -> wants the bare index, e.g. 0
        // fh_loader is fed ComName directly (already a /dev path). emmcdl must be
        // fed the trailing index, or atoi() silently yields 0 and it talks to the
        // wrong device. This maps /dev/ttyUSBN (or COMn) to the index emmcdl wants.
        public static string EmmcdlPort(string comName)
        {
            if (string.IsNullOrEmpty(comName)) return comName;
            var m = Regex.Match(comName, @"(\d+)\s*$");
            return m.Success ? m.Groups[1].Value : comName;
        }

        // `name` is the Windows product-string intent (e.g. "qdloader 9008",
        // "huawei usb com"); `devicename` is a specific port the user picked, or
        // "Auto"/empty for auto-detect. On Linux the product string differs, so we
        // auto-detect by USB VID:PID instead (EDL 9008 = 05c6:9008, Huawei = 12d1),
        // and resolve a manual pick against the real enumerated /dev path.
        public static Port_D GETPORT(string name, string devicename = "")
        {
            List<Port_D> ports;
            try { ports = EnumeratePorts(); }
            catch (Exception) { LOG(2, "NoRights"); ports = new List<Port_D>(); }

            // --- Manual selection: resolve to the real device the user chose ---
            if (!string.IsNullOrEmpty(devicename) && devicename != "Auto")
            {
                var sel = ports.FirstOrDefault(p => p.FullName == devicename)
                       ?? ports.FirstOrDefault(p => p.ComName == devicename)
                       ?? ports.FirstOrDefault(p => p.FullName.Contains(devicename));
                if (sel != null) return sel;
                if (devicename.StartsWith("/dev/")) return new Port_D { ComName = devicename, FullName = devicename };
                return new Port_D { ComName = "NaN", FullName = "NaN" };
            }

            // --- Auto-detect: map the intent to Linux USB IDs ---
            var n = name.ToLower();
            Func<Port_D, bool> byId;
            if (n.Contains("9008") || n.Contains("qdloader") || n.Contains("qualcomm"))
                byId = p => p.Vid == "05c6" && p.Pid == "9008";
            else if (n.Contains("huawei") || n.Contains("usb com"))
                byId = p => p.Vid == "12d1";
            else
                byId = p => p.FullName.ToLower().Contains(n);

            var hit = ports.FirstOrDefault(byId)
                   ?? ports.FirstOrDefault(p => p.FullName.ToLower().Contains(n));
            return hit ?? new Port_D { ComName = "NaN", FullName = "NaN" };
        }

        public static List<Port_D> GETPORTLIST()
        {
            var req = new List<Port_D>();
            try { req.AddRange(EnumeratePorts()); }
            catch (Exception) { LOG(2, "NoRights"); }
            return req;
        }

        public static string PickLoader(string dev)
        {
            DeviceInfo.Name = dev;
            string pth = Path.Combine(Directory.GetCurrentDirectory(), "qc_boot", dev);
            if (!Directory.Exists(pth)) return "NaN";
            foreach (var a in Directory.GetFiles(pth))
                if (a.EndsWith(".mbn") || a.EndsWith(".elf") || a.EndsWith(".hex")) return a;
            return "";
        }

        public static Dictionary<string, Partition> GET_GPT_FROM_FILE(string GPT_File, int block_size)
        {
            Dictionary<string, Partition> GPT = new Dictionary<string, Partition>();
            DataS.GPT_Struct magic_number = new DataS.GPT_Struct(0x00, 8, string.Empty);
            DataS.GPT_Struct gpt_startadress = new DataS.GPT_Struct(0x48, 8, string.Empty);
            DataS.GPT_Struct max_gpt_blocks = new DataS.GPT_Struct(0x50, 4, string.Empty);
            DataS.GPT_Struct record_length = new DataS.GPT_Struct(0x54, 4, string.Empty);
            string Full_GPT = BitConverter.ToString(File.ReadAllBytes(GPT_File)).Replace("-", "");
            string GPT_Header = Full_GPT.Remove(0, block_size * 2);
            string gpt_header = GPT_Header.Remove(block_size * 2);
            magic_number.ValueString = gpt_header.Substring(magic_number.StartAdress * 2, magic_number.Length * 2);
            if (!magic_number.ValueString.Equals("4546492050415254"))
            {
                LOG(2, "THIS FILE IS NOT a gpt_####0.bin");
                return GPT;
            }
            gpt_startadress.ValueString = gpt_header.Substring(gpt_startadress.StartAdress * 2, gpt_startadress.Length * 2);
            string gsa = gpt_startadress.ValueString;
            while (gsa.EndsWith("00")) gsa = gsa.Remove(gsa.Length - 2, 2);
            gsa = gsa.TrimStart('0');
            max_gpt_blocks.ValueString = gpt_header.Substring(max_gpt_blocks.StartAdress * 2, max_gpt_blocks.Length * 2);
            string mgb = string.Empty;
            for (int i = 0; i < max_gpt_blocks.Length; i++)
                mgb = mgb.Insert(0, max_gpt_blocks.ValueString.Substring(i * 2, 2));
            mgb = mgb.TrimStart('0');
            record_length.ValueString = gpt_header.Substring(record_length.StartAdress * 2, record_length.Length * 2);
            string rl = record_length.ValueString;
            while (rl.EndsWith("00")) rl = rl.Remove(rl.Length - 2, 2);
            rl = rl.TrimStart('0');
            int rlint = Convert.ToInt32(rl, 16);
            string GPT_Values = Full_GPT.Remove(0, block_size * 2 * Convert.ToInt32(gsa, 16));
            DataS.GPT_Struct block_startadress = new DataS.GPT_Struct(0x20, 8, string.Empty);
            DataS.GPT_Struct block_endadress = new DataS.GPT_Struct(0x28, 8, string.Empty);
            DataS.GPT_Struct block_name = new DataS.GPT_Struct(0x38, 72, string.Empty);
            string[] blocks_array = new string[Convert.ToInt32(mgb, 16)];
            for (int i = 0; i < blocks_array.Length; i++)
                blocks_array[i] = GPT_Values.Substring(i * rlint * 2, rlint * 2);
            foreach (string block_string in blocks_array)
            {
                block_startadress.ValueString = block_string.Substring(block_startadress.StartAdress * 2, block_startadress.Length * 2);
                string bsa = string.Empty;
                for (int k = 0; k < block_startadress.Length; k++)
                    bsa = bsa.Insert(0, block_startadress.ValueString.Substring(k * 2, 2));
                block_endadress.ValueString = block_string.Substring(block_endadress.StartAdress * 2, block_endadress.Length * 2);
                string bea = string.Empty;
                for (int m = 0; m < block_endadress.Length; m++)
                    bea = bea.Insert(0, block_endadress.ValueString.Substring(m * 2, 2));
                block_name.ValueString = block_string.Substring(block_name.StartAdress * 2, block_name.Length * 2);
                StringBuilder bn = new StringBuilder();
                for (int p = 0; p < block_name.Length; p += 4)
                {
                    string unichar = (block_name.ValueString.Substring(p + 2, 2) + block_name.ValueString.Substring(p, 2)).TrimStart('0');
                    if (!string.IsNullOrEmpty(unichar)) bn.Append(Convert.ToChar(Convert.ToInt32(unichar, 16)));
                }
                if (!string.IsNullOrEmpty(bsa) && !string.IsNullOrEmpty(bea))
                {
                    uint blocks_count = Convert.ToUInt32(bea, 16) - Convert.ToUInt32(bsa, 16) + 1;
                    if (!GPT.ContainsKey(bn.ToString()) & !bn.ToString().Contains("userdata") & !string.IsNullOrEmpty(bn.ToString()))
                        GPT.Add(bn.ToString().Replace(".img", ""), new Partition()
                        {
                            BlockStart = Convert.ToInt32(bsa, 16).ToString(),
                            BlockEnd = Convert.ToInt32(bea, 16).ToString(),
                            BlockBytes = (blocks_count * block_size).ToString(),
                            BlockNumSectors = blocks_count.ToString(),
                            BlockLength = (blocks_count / 2).ToString(),
                        });
                }
            }
            return GPT;
        }

        public static void ClearLog() => Host.ClearLog();

        public static bool CheckDevice(string path, string DeviceName = "")
        {
            Host.ClearLog();
            LOG(0, "CheckCon");
            DeviceInfo.Port = GETPORT("qdloader 9008", DeviceName);
            if (DeviceInfo.Port.ComName == "NaN" || DeviceInfo.Port.FullName == "NaN")
            {
                LOG(2, "DeviceNotCon");
                DeviceInfo.loadedhose = false;
            }
            else
            {
                if (DeviceInfo.loadedhose) return true;
                if (Host.AutoLoader & DeviceInfo.HWID.Contains("NaN"))
                {
                    DeviceInfo.Name = "NaN";
                    DeviceInfo.Port = GETPORT("qdloader 9008", "Auto");
                    FlashToolQClegacy.GetIdentifier();
                    LOG(0, "LoaderSearch");
                    var ambn = GuessMbn();
                    if (!string.IsNullOrEmpty(ambn))
                    {
                        if (ambn == "True") return false;
                        DeviceInfo.Name = ambn.Split(Path.DirectorySeparatorChar).Length > 1
                            ? ambn.Split(Path.DirectorySeparatorChar)[1]
                            : ambn;
                        return true;
                    }
                    else return !LOG(0, "NoDEVICEAnsw");
                }
                if (!File.Exists(path) && !DeviceInfo.loadedhose & !Host.AutoLoader)
                    LOG(2, "ErrLdr", path);
                else
                    return true;
            }
            return false;
        }

        public static bool WriteGPT_TO_XML(string papthto, Dictionary<string, Partition> partbI, bool verify)
        {
            StreamWriter writer = new StreamWriter(papthto);
            writer.WriteLine("<?xml version=\"1.0\" ?>");
            writer.WriteLine("<data>");
            writer.WriteLine("  <!--NOTE: This is an ** Autogenerated file **-->");
            writer.WriteLine("  <!--NOTE: HUT_HUAWEI UNLOCK TOOL **-->");
            foreach (var i in partbI)
            {
                if (string.IsNullOrEmpty(i.Key)) continue;
                if (verify && !File.Exists(Path.Combine("UnlockFiles", "UpdateAPP", i.Key + ".img"))) continue;
                string line = "  <program SECTOR_SIZE_IN_BYTES=\"512\" file_sector_offset=\"0\" filename=\"" + i.Key + ".img\"" + " label=\"" + i.Key + "\" num_partition_sectors=\"" + i.Value.BlockNumSectors + "\" physical_partition_number=\"0\" size_in_KB=\"" + i.Value.BlockLength + "\" sparse=\"false\" start_byte_hex=\"" + string.Format("0x{0:x16}", i.Value.BlockStart) + "\" start_sector=\"" + i.Value.BlockStart + "\" />";
                if (i.Key.ToLower() == "userdata")
                    line = "  <program SECTOR_SIZE_IN_BYTES=\"512\" file_sector_offset=\"0\" filename=\"" + i.Key + ".img\"" + " label=\"" + i.Key + "\" num_partition_sectors=\"" + 1 + "\" physical_partition_number=\"0\" size_in_KB=\"" + i.Value.BlockLength + "\" sparse=\"false\" start_byte_hex=\"" + string.Format("0x{0:x16}", i.Value.BlockStart) + "\" start_sector=\"" + i.Value.BlockStart + "\" />";
                writer.WriteLine(line);
                if (debug) LOG(0, line);
            }
            writer.WriteLine("</data>");
            LOG(0, "-==>" + DeviceInfo.Name + "<==-");
            LOG(0, "RrGPTXMLS", papthto);
            writer.Close();
            writer.Dispose();
            return partbI.Count > 0;
        }

        public static void Progress(int v, int max = 100) => Host.SetProgress(v, max);

        public static string GuessMbn()
        {
            if (!string.IsNullOrEmpty(Host.SelectedLoader))
                return PickLoader(Host.SelectedLoader);
            if (Host.AutoLoader & DeviceInfo.HWID.Contains("NaN"))
                return LOG(0, "NoDEVICEAnsw").ToString();
            if (debug) LOG(0, "LoaderSearch");
            string[] subdirectoryEntries = Directory.GetDirectories("qc_boot");
            foreach (string subdirectory in subdirectoryEntries)
            {
                var a = Directory.GetFiles(subdirectory).First();
                var b = File.ReadAllBytes(a);
                if (Encoding.ASCII.GetString(b).ToLower().Contains(DeviceInfo.HWID.Replace("0x", "")))
                {
                    if (debug) LOG(0, "LoaderFound", a);
                    return a;
                }
            }
            return "";
        }

        public static string GuessMbnTest()
        {
            if (!string.IsNullOrEmpty(Host.SelectedLoader))
                return PickLoader(Host.SelectedLoader);
            if (Host.AutoLoader & DeviceInfo.HWID.Contains("NaN"))
                return LOG(0, "NoDEVICEAnsw").ToString();
            if (debug) LOG(0, "LoaderSearch");
            string any = "";
            string[] subdirectoryEntries = Directory.GetDirectories("qc_boot");
            foreach (string subdirectory in subdirectoryEntries)
            {
                var a = Directory.GetFiles(subdirectory).First();
                var b = File.ReadAllBytes(a);
                if (Encoding.ASCII.GetString(b).ToLower().Contains(DeviceInfo.HWID.Replace("0x", "")))
                    LOG(0, "LoaderFound", any = a);
            }
            return any;
        }

        public static byte[] GetResource(string ResourceName)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            byte[] data = new byte[stream.Length];
            stream.Read(data, 0, (int)stream.Length);
            return data;
        }

        public static void SaveResources(string ResourceName, string SavePath, string SaveName = "")
        {
            if (!Directory.Exists(SavePath)) Directory.CreateDirectory(SavePath);
            string myspace = typeof(LangProc).Namespace;
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(myspace + "." + ResourceName);
            FileStream filewritter = new FileStream(Path.Combine(SavePath, string.IsNullOrEmpty(SaveName) ? ResourceName : SaveName), FileMode.CreateNew);
            for (int i = 0; i < stream.Length; i++) filewritter.WriteByte((byte)stream.ReadByte());
            filewritter.Close();
            filewritter.Dispose();
        }
    }
}
