using System.IO;

namespace HuaweiUnlocker
{
    /// <summary>
    /// Resolves the external flashing helpers the tool shells out to.
    /// The original Windows build bundled emmcdl.exe / fh_loader.exe / mtkflash.exe
    /// under Tools\. On Linux we look, in order:
    ///   1. an override path stored in config (key "tool.&lt;name&gt;")
    ///   2. a local ./Tools/&lt;name&gt; binary (ELF build dropped next to the app)
    ///   3. the bare name, resolved via $PATH (e.g. distro/pip-installed emmcdl)
    /// Linux replacements: emmcdl -> github.com/bkerler/edl's emmcdl or the
    /// original emmcdl Linux build; fh_loader has no free Linux port (Qualcomm
    /// proprietary) so bkerler's edl is the practical substitute; mtkflash ->
    /// mtkclient. The exact args differ, so wiring is best-effort here.
    /// </summary>
    public static class Tools
    {
        public static string Emmcdl => Resolve("emmcdl");
        public static string FhLoader => Resolve("fh_loader");
        public static string MtkFlash => Resolve("mtkflash");

        private static string Resolve(string name)
        {
            var overridePath = LangProc.Host?.GetSetting("tool." + name);
            if (!string.IsNullOrEmpty(overridePath))
                return overridePath;

            var local = Path.Combine("Tools", name);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            // Fall back to $PATH lookup by bare name.
            return name;
        }
    }
}
