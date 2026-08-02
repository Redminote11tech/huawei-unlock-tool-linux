# Huawei Unlock Tool — Linux port

A native Linux port of the original Windows WinForms tool, rebuilt on
**.NET / Avalonia**. The original targeted .NET Framework 4.8 + WinForms and is
Windows-only; this port keeps the phone-servicing logic and gives it a
cross-platform GUI.

> **License:** AGPL-3.0-or-later (see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE)).
> Derivative work of [werasik2aa/Huawei-Unlock-Tool](https://github.com/werasik2aa/Huawei-Unlock-Tool)
> — © Moongamer (Qualcomm portion) and mashed-potatoes / penn5 (Kirin/PotatoNV portion).
> No proprietary Qualcomm/Huawei binaries are redistributed here; the `emmcdl`/`fh_loader`
> helpers are external programs (see `packaging/`).

```
.
├── HuaweiUnlockLinux.slnx
├── packaging/               # Arch/CachyOS PKGBUILDs (app + emmcdl + fh_loader)
└── src/
    ├── HuaweiUnlock.Core/   # platform-neutral logic (EDL/DIAG, GPT, update.app, HISI, USB)
    └── HuaweiUnlock.App/     # Avalonia GUI (implements IUiHost)
```

## Build & run

Requires the **.NET SDK (9.0+)** and **libusb-1.0** (`libusb-1.0-0` on
Debian/Ubuntu, `libusb` on Arch). Serial access needs your user in the
`dialout`/`uucp` group; raw USB (Kirin fastboot) needs a udev rule or running
with sufficient privileges.

```bash
cd linux
dotnet run --project src/HuaweiUnlock.App
```

## What changed vs. the Windows original

| Concern | Windows original | Linux port |
|---|---|---|
| UI toolkit | WinForms (`Window.cs`, custom-drawn `NButton`/`NProgressBar`, GDI+) | Avalonia XAML (`MainWindow.axaml`) |
| UI ↔ logic coupling | `LangProc` poked form controls directly (`LOGGBOX`, `PRG`, `Tab`, `wndw.*`) | `IUiHost` abstraction; `LangProc` routes through it |
| Serial-port discovery | WMI `Win32_PnPEntity` query | `/dev/serial/by-id` + `SerialPort.GetPortNames()` |
| Settings | Windows registry (`SOFTWARE\4PDA_HUAWEI_UNLOCK`) | `config.json` next to the app |
| Path separators | hard-coded `\` | `Path.Combine` / `Path.DirectorySeparatorChar` |
| External tools | bundled `Tools\emmcdl.exe`, `fh_loader.exe`, `mtkflash.exe` | `Tools` resolver → config path / `./Tools/` / `$PATH` |
| EDL port arg | `--port=\\.\COMx` | `--port=/dev/ttyUSBx` (device path) |

Portable logic modules (CRC, HDLC, DataS, LibCrypt, Bootloader, OemInfoTool,
the whole `UpdateUtil/` update.app parser, `FlashToolQClegacy`, `HISI`,
`ImageFlasher`, `Fastboot`) were carried over almost verbatim — only the small
UI/registry/WMI/path seams were rewritten. Base62, BouncyCastle, DotNetZip and
LibUsbDotNet come from NuGet and are cross-platform.

## Install on CachyOS / Arch (PKGBUILD)

```bash
cd linux/packaging

# 1) The Qualcomm flashing helpers — same binary names & CLI as on Windows,
#    so the tool's flashing arguments are used unchanged:
(cd emmcdl    && makepkg -si)   # github.com/Zalexanninev15/emmcdl
(cd fh_loader && makepkg -si)   # github.com/LonelyFool/fh_loader

# 2) The app itself (builds the in-place source; needs network for NuGet, so
#    run plain makepkg, not a clean chroot):
makepkg -si
```

This installs a self-contained build (bundled .NET runtime — no `dotnet`
dependency), a `huawei-unlock-tool` launcher, a desktop entry and icon. The
launcher runs the app from `~/.local/share/huawei-unlock-tool/` so its working
files stay out of your home root.

### Device access — no groups, no runtime sudo

The package ships `/usr/lib/udev/rules.d/51-huawei-unlock-tool.rules`, which tags
the relevant device modes with logind **`uaccess`**: your active desktop session
gets an ACL on the device, so **no `uucp`/`dialout` membership and no `sudo` at
run time** are needed — just plug the phone in. Covered:

- Qualcomm **EDL 9008** (`05c6:9008`) — tty + raw USB (emmcdl / fh_loader)
- Kirin **fastboot** (`18d1:d00d`) — libusb
- Huawei native modes (`12d1`) — VCOM / USB COM / fastboot

`pacman` installs the rule as root and the install hook reloads udev, so the only
privileged step is the package install itself. After install, replug the device.
(Over a headless SSH session `uaccess` doesn't apply — the `uucp` group fallback
in the rule covers that case.)

## Parity: nothing was dropped

Earlier I flagged Upgrade Mode (`FirmwareUpdate.dll`) and QMSL as Linux-blockers.
On closer inspection of the shipped code that was wrong — **they were never wired
to anything:**

- `UpgradeMode` (the `FirmwareUpdate.dll` P/Invokes) is **called from nowhere** —
  dead declarations in the original.
- **QMSL** is referenced in no source file and isn't even in the original
  `.csproj`; the DLLs just sit unused in `packages/`.
- The **MTK** tab was a **non-functional placeholder** upstream (`MTKFlash` is
  never called), and is kept as one here.

So this port is 1:1 with the shipped Windows app — no feature was lost.

## External tool setup (Debug tab)

The Qualcomm flows shell out to `emmcdl` and `fh_loader`, exactly as on Windows.
Both have native Linux builds with **identical command-line arguments**, so
`FlashToolQClegacy` is byte-for-byte unchanged. Paths are configured in the
**Debug** tab (stored as `tool.emmcdl` / `tool.fh_loader` / `tool.mtkflash` in
`config.json`), or resolve via `$PATH`:

- **emmcdl** — [Zalexanninev15/emmcdl](https://github.com/Zalexanninev15/emmcdl) or [nijel8/emmcdl](https://github.com/nijel8/emmcdl).
- **fh_loader** — [LonelyFool/fh_loader](https://github.com/LonelyFool/fh_loader) (Qualcomm's own source, compilable for Linux).
- **mtkflash** — only needed if the placeholder MTK tab is ever wired up ([mtkclient](https://github.com/bkerler/mtkclient)).

## Status

The GUI runs natively and reproduces the original's 8-tab layout (Home, QCOM
Unlock, QCOM Partitions, Kirin Unlock, Kirin Flash, MTK, Oeminfo, Debug) with the
shared port/loader/log/progress panel. Kirin (libusb) and update.app/oeminfo
flows are fully in-process; Qualcomm flows drive the native `emmcdl`/`fh_loader`
binaries. Everything compiles and launches, and USB port auto-detection works —
but see **Known issues** below: the Qualcomm (EDL) flashing path is currently
blocked on Linux by an `emmcdl` limitation, independent of this port.

## Known issues

### Qualcomm EDL flashing hangs on Linux (`< waiting for device >`)

On real hardware, the Qualcomm/EDL operations (Read GPT, flash, dump, erase via
the `[QCOM]` tabs) upload the firehose loader and then **hang**, with `emmcdl`
printing `< waiting for device >` to stderr.

**Root cause — this is in `emmcdl`, not in this port:** `emmcdl`'s Linux USB
backend (`usb_linux.c` / `usbport.cpp`) opens the raw USB device but **never
detaches the kernel driver**. The `qcserial` kernel module claims the
`05c6:9008` device (creating `/dev/ttyUSB0`); after the loader is uploaded over
the serial port, `emmcdl` needs raw USB for the firehose transfer, can't claim
the device because `qcserial` still holds it, and busy-loops forever waiting for
it. This is why `emmcdl` works on Windows (Qualcomm's WinUSB/QDLoader driver) but
not on Linux. It affects **both UFS and eMMC** devices.

What this means: the QCOM tabs will detect the device and start, then time out
after 90s. There is no fix on the C# side — it would require replacing the
`emmcdl`/`fh_loader` backend with a libusb-based EDL tool such as
[bkerler/edl](https://github.com/bkerler/edl), which detaches the kernel driver.
That backend swap is **not implemented** (it would break the 1:1 mapping to the
original's `emmcdl` command lines).

### Other notes

- **UFS storage:** the original tool only passed the UFS memory type to
  `fh_loader`, never to `emmcdl`, so `emmcdl` always defaulted to eMMC (Read GPT
  returned "EMMC GPT empty" on UFS devices). This port fixes that by passing
  `-MemoryName ufs` to `emmcdl` when **UFS storage** is ticked — but the
  `emmcdl` USB limitation above still applies.
- **Kirin factory bootloader flash** is beta upstream ("can't flash big files
  properly", per the original author) and inherits that limitation here.
- Nothing here has completed a real hardware flash end-to-end.
