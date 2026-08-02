using System.Collections.Generic;

namespace HuaweiUnlocker
{
    /// <summary>
    /// Abstraction the platform-neutral core uses to talk to whatever front-end
    /// is hosting it. The original WinForms build spoke to controls directly
    /// (LOGGBOX/PRG/Tab/wndw.*). On Linux the Avalonia app implements this and
    /// marshals to the UI thread itself, so the core stays UI-toolkit agnostic.
    /// </summary>
    public interface IUiHost
    {
        /// <summary>Append one already-formatted line to the log view.</summary>
        void Log(string line);

        /// <summary>Replace the whole log contents (used by ClearLog / CheckDevice).</summary>
        void ClearLog();

        /// <summary>Update the progress bar (value out of max).</summary>
        void SetProgress(int value, int max);

        /// <summary>Enable/disable interaction while a long operation runs.</summary>
        void SetBusy(bool busy);

        // --- State the core reads that used to live on form controls ---

        /// <summary>wndw.AutoLdr.Checked</summary>
        bool AutoLoader { get; }

        /// <summary>wndw.LoaderBox.Text</summary>
        string SelectedLoader { get; }

        /// <summary>wndw.UFSChk.Checked</summary>
        bool Ufs { get; }

        // --- Persisted settings (replaces the Windows registry key) ---

        string GetSetting(string key);
        void SetSetting(string key, string value);
    }

    /// <summary>
    /// A no-op host so the core can be exercised head-less (tests, CLI probes)
    /// without a running UI.
    /// </summary>
    public sealed class NullUiHost : IUiHost
    {
        private readonly Dictionary<string, string> _settings = new Dictionary<string, string>();
        public void Log(string line) { }
        public void ClearLog() { }
        public void SetProgress(int value, int max) { }
        public void SetBusy(bool busy) { }
        public bool AutoLoader => true;
        public string SelectedLoader => "";
        public bool Ufs => false;
        public string GetSetting(string key) => _settings.TryGetValue(key, out var v) ? v : null;
        public void SetSetting(string key, string value) => _settings[key] = value;
    }
}
