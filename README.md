# CdRipper

A lightweight Windows utility that rips audio CDs to WAV by talking to
the drive directly over SCSI pass-through, instead of going through
Windows' CD-audio shim. It reads raw 2352-byte CD-DA sectors, pulls
CD-TEXT when the disc has it, and can rip from several drives in
parallel.

## Features

- **Direct SCSI pass-through** reading (`IOCTL_SCSI_PASS_THROUGH_DIRECT`)
  — no reliance on Windows' built-in CD-audio extraction path.
- **CD-TEXT support** — reads album/track titles and performers from the
  disc (format 5 TOC) and uses them to name output files and tag the
  resulting WAVs (ID3v2.3 `id3 ` RIFF chunk).
- **Multi-drive ripping** — tick multiple drives and rip them all at
  once, each with its own destination folder and progress bar.
- **Pause and resume** — pause mid-rip and the exact sector position per
  drive is saved to disk; on next launch, CdRipper waits for each disc
  to be reinserted (verified by TOC + PCM hash) before resuming.
- **Enhanced CD / CD-Extra aware** — skips the trailing data-session gap
  so track boundaries stay correct on mixed-mode discs.
- **Free-space and overwrite checks** before a rip starts.
- **Optional auto-eject** of each drive once its rip finishes.

## Requirements

- Windows (uses Win32 SCSI pass-through — not portable to other OSes).
- .NET Framework 3.5.
- A CD/DVD drive exposed as a normal Windows drive letter.

## Building

### Visual Studio

Open `CdRipper.sln` and build. The project is configured for the x64
platform.

### Command line (no IDE)

Run `build.bat` from the project folder. It locates `csc.exe` for
.NET Framework 2.0/3.5 and compiles a 64-bit `CdRipper.exe` directly
from the `.cs` files, with `icon.ico` embedded as the executable icon.

```
build.bat
```

## Usage

1. Launch `CdRipper.exe`.
2. Insert audio CDs — every drive holding a valid CD-DA disc is listed
   automatically (the list updates on drive arrival/removal).
3. Tick the drives you want to rip and choose a destination folder for
   each.
4. Click **Rip selected drives**.
5. Use **Pause** to stop safely and save progress, or **Abort** to stop
   immediately and leave files exactly as they are. Progress and a log
   are kept under `%APPDATA%\CdRipper`.

If a paused rip exists, CdRipper offers to resume it at startup once
the original discs are back in their drives.

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | Entry point |
| `MainForm.cs` | Drive list, folder selection, rip kickoff |
| `RipForm.cs` | Progress UI, pause/abort, resume-state saving |
| `ResumeForm.cs` | Disc-reinsertion verification before resuming |
| `RipEngine.cs` | Per-drive ripping job logic |
| `Scsi.cs` | Low-level SCSI pass-through (TOC, CD-TEXT, READ CD) |
| `Model.cs` | Disc/track model, TOC parsing, CD-TEXT decoding |
| `Wav.cs` | WAV header + ID3v2.3 tag construction |
| `Store.cs` | Settings and paused-rip state persistence |
| `Util.cs` | Formatting and hashing helpers |

## License

Licensed under the GNU General Public License v3.0 — see
[LICENSE](LICENSE) for the full text.

Copyright (C) 2026 ClearAct290
