# Upstream sync

This Linux port tracks the original Windows tool:

- **Upstream:** https://github.com/werasik2aa/Huawei-Unlock-Tool (branch `main`)
- **Last synced upstream commit:** `d2a633c` ("WTF5", 2025-03-13) — version `33F`

Upstream is a Windows WinForms app; this repo is a rewritten Avalonia/.NET app.
There is **no clean `git merge`** between them — syncing means re-porting the
changes in the shared *logic* files and re-implementing any UI/behavior changes.

## Procedure

1. Diff upstream since the recorded baseline:
   `git clone --bare https://github.com/werasik2aa/Huawei-Unlock-Tool && \
    git -C Huawei-Unlock-Tool.git log --stat d2a633c..origin/main`
2. For each changed file, apply per the mapping below.
3. `dotnet build HuaweiUnlockLinux.slnx -c Release` must pass.
4. Update the "Last synced" commit above and commit with message
   `Sync upstream <newhash> (<version>)`.

## File mapping (upstream → this repo)

| Upstream file | Port location | How to sync |
|---|---|---|
| `HuaweiUnlock/DIAGNOS/{CRC,DataS,LibCrypt,Bootloader,OemInfoTool}.cs` | `src/HuaweiUnlock.Core/DIAGNOS/` | copy, then re-apply adaptations* |
| `HuaweiUnlock/FlashTool/{FlashToolQClegacy,MTKFlash}.cs` | `src/HuaweiUnlock.Core/FlashTool/` | copy, then re-apply adaptations* |
| `HuaweiUnlock/TOOLS/{Fastboot,HISI,ImageFlasher,UpdateApp}.cs`, `TOOLS/UpdateUtil/*` | `src/HuaweiUnlock.Core/TOOLS/` | copy, then re-apply adaptations* |
| `HuaweiUnlock/UI/Language.cs` | `src/HuaweiUnlock.Core/UI/Language.cs` | copy, fix `Path.Combine` |
| `HuaweiUnlock/LangProc.cs` | `src/HuaweiUnlock.Core/LangProc.cs` | **manual** — port logic into the `IUiHost`-abstracted version |
| `HuaweiUnlock/Window.cs`, `Window.Designer.cs` | `src/HuaweiUnlock.App/MainWindow.axaml(.cs)` | **manual** — replicate new buttons/behavior, not code |
| `HuaweiUnlock/{English,Russian}.ini` | `src/HuaweiUnlock.App/Languages/` | copy verbatim |

Not ported (dead upstream code, never in its build): `DIAGNOS/DIAG.cs`,
`DIAGNOS/HDLC.cs`, `FlashTool/FlashToolHisi.cs`, `TOOLS/SerialManager.cs`,
`TOOLS/Guide.cs`, `TOOLS/OemInfo.cs`, `TOOLS/ResourcesMNG.cs`,
`TOOLS/UpgradeMode.cs`. Port-only files: `Tools.cs`, `IUiHost.cs`.

## \*Adaptations to re-apply to copied logic files

- Windows path separators `"a\\b"` → `Path.Combine(...)` / `Path.DirectorySeparatorChar`.
- `"Tools\\emmcdl.exe"`/`fh_loader.exe`/`mtkflash.exe` → `Tools.Emmcdl`/`Tools.FhLoader`/`Tools.MtkFlash`.
- emmcdl port arg `"-p " + DeviceInfo.Port.ComName` → `"-p " + EmmcdlPort(DeviceInfo.Port.ComName)` (index); fh_loader keeps `--port=<path>` (drop the `\\.\ ` prefix).
- Remove `using System.Windows.Forms;`; `Application.DoEvents()` → delete.
- `Tab.Enabled = true/false` → `Host.SetBusy(false/true)`; `wndw.AutoLdr.Checked`→`Host.AutoLoader`, `wndw.LoaderBox.Text`→`Host.SelectedLoader`, `wndw.UFSChk.Checked`→`Host.Ufs`.
- `.NET 9`: `byte[].Reverse()` → `Enumerable.Reverse(...)`.
