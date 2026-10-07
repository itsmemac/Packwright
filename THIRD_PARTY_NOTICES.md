# Third-party notices

Packwright, Copyright (c) 2026 itsmemac, is licensed under the GNU General Public License v3.0
(see `LICENSE`). It is based on the original application listed first below. The components after
it are used under their own terms.

## PS5 PKG Tool (original application)

Packwright is based on PS5 PKG Tool by pearlxcore (https://github.com/pearlxcore/PS5PkgTool),
licensed under the GNU General Public License v3.0. Copyright (c) pearlxcore. The exFAT, UFS2/FFPKG,
PFSC/FFPFSC and package code, the library scanner and the task queue originate from that project, and
the cross-platform application logic is ported from its Windows interface. The original copyright
notice and license are retained in this repository.

### Modifications (GPL-3.0 section 5)

Packwright is a **modified version** of PS5 PKG Tool. The changes below were made in 2026 by
itsmemac:

- the Windows Forms interface was replaced by a cross-platform Avalonia interface (Windows, Linux,
  macOS), with a new design, name and application icon;
- ZArchive (`.zar`) support was added: writing, reading, library scanning, browsing and conversion;
- the package engine was replaced by one of its own (`src/Packwright.Pkg`), which reads, verifies, extracts and builds
  packages, including Kraken compression, with no third-party engine and no keys (see below);
- the library gained artwork cards, grouping, saved views, a search syntax, a file explorer with
  preview, rename/move/delete tools, task persistence and a task history with game icons;
- editing of a title's details (name, IDs, region, versions, firmware, SDK, DRM, language, features and icon), in the
  library or written into dumps, images and packages, with offline autofill and an optional lookup of Title IDs in a
  public title list;
- a library health report, export of the library (CSV, JSON, web page), saved presets for conversions, a command-line
  mode, and sorting and column choice in the list view;
- "Send to console": an FTP client and a local file server for installers on the user's own network;
- an update check against this repository's GitHub releases, and installers for Windows (Inno Setup), macOS (disk image)
  and Linux (.deb and .rpm);
- the projects and folders were renamed and reorganised, and build and release automation was added.

Everything else remains under the original GPL-3.0 terms. The application icon is original to this
project (Copyright (c) 2026 itsmemac, GPL-3.0).

## Source code

The complete corresponding source of each release is the tag with the same version in
https://github.com/itsmemac/Packwright.

## Packwright.Pkg (the package engine)

`src/Packwright.Pkg` is Packwright's own package engine: it reads, validates and extracts PS5 packages and builds
them (inner image, outer filesystem, metadata container, install metadata, Kraken compression). It contains no
Sony code, no Sony keys and no third-party binaries.

The engine builds on the format work of LibProsperoPKG by SvenGDK (https://github.com/SvenGDK/LibProsperoPKG,
Copyright (c) 2026 SvenGDK, GPL-3.0-or-later): source files of that project (the PFS and container structures, the
utility code, the Kraken encoder and decoder) are included in adapted form with their original copyright headers, and
the layout and digest rules of the writer follow it. LibProsperoPKG in turn contains code derived from LibOrbisPkg by
maxton (https://github.com/maxton/LibOrbisPkg, GPL-3.0) and an index-based translation of the Kraken decoder "ooz" by
Powzix (https://github.com/powzix/ooz, GPL-3.0). Their notices are kept in the headers of the files concerned. Every
part of the engine was checked against real packages on disk: file for file, digest for digest.

The engine ships no key. A few fields of a package are normally sealed with public RSA values; the user may import them
from a file of their own (see `docs/KEYS.md`). Packwright only checks the file against fingerprints that do not reveal
the values, and refuses a private key.

GNU General Public License v3.0 or later. Because these parts are GPL-3.0-or-later, the combined work is distributed
under the GPL-3.0 (see `LICENSE`).

## UFS2Tool

The `src/Packwright.Ufs2` filesystem implementation is based on
[SvenGDK/UFS2Tool](https://github.com/SvenGDK/UFS2Tool), used and modified under the BSD 2-Clause
License. Copyright (c) 2026, SvenGDK. The original copyright and license text are retained in
`src/Packwright.Ufs2/LICENSE` and in the imported source files.

## Avalonia UI

The cross-platform interface is built on Avalonia UI (and Avalonia.Controls.DataGrid,
Avalonia.Themes.Fluent, Avalonia.Fonts.Inter), licensed under the MIT License.
Copyright (c) AvaloniaUI OÜ and contributors. https://github.com/AvaloniaUI/Avalonia
The Inter font is licensed under the SIL Open Font License 1.1.

## ZstdSharp.Port

MIT License. Copyright (c) Oleg Stepanischev and contributors. https://github.com/oleg-st/ZstdSharp
It provides the zstd compression used for ZArchive output. zstd itself is BSD-licensed, Copyright
(c) Meta Platforms, Inc. and affiliates.

## ZArchive

The `.zar` container format was designed by Exzap (https://github.com/Exzap/ZArchive, Copyright 2022
Exzap, "MIT No Attribution" license). The writer and reader in `src/Packwright.Containers`
(`ZArchiveWriter.cs`, `ZArchiveVolume.cs`, `ZArchiveImage.cs`) are managed C# implementations of the
format that follow the reference implementation, and were checked against archives made by it.

## Graphics libraries bundled with Avalonia

- **SkiaSharp** (MIT License, Copyright (c) Microsoft Corporation and contributors) with the **Skia**
  graphics library (BSD 3-Clause, Copyright (c) 2011 Google Inc.): `third-party/licenses/Skia-BSD-3-Clause.txt`.
- **HarfBuzzSharp** (MIT License) with **HarfBuzz** (Old MIT License, Copyright (c) the HarfBuzz authors).
- **ANGLE** (`av_libglesv2.dll`, BSD 3-Clause, Copyright 2018 The ANGLE Project Authors):
  `third-party/licenses/ANGLE-BSD-3-Clause.txt`.
- **MicroCom.Runtime**, **Tmds.DBus.Protocol** and the other Avalonia platform packages are MIT licensed.

## Icons and fonts

- Interface icons (sidebar and file-type symbols) are paths from **Material Design Icons** by Google,
  licensed under the Apache License 2.0 (`third-party/licenses/Apache-2.0.txt`).
- The **Inter** font (bundled through Avalonia.Fonts.Inter) is Copyright (c) 2016 The Inter Project
  Authors, licensed under the SIL Open Font License 1.1 (`third-party/licenses/Inter-OFL-1.1.txt`).
- The application icon is original to this project.

## SixLabors ImageSharp

ImageSharp (used through BCnEncoder.Net.ImageSharp) is licensed under the Six Labors Split License.
This project is open source, and ImageSharp is also a transitive dependency here, so ImageSharp is used
under the Apache License 2.0 (`third-party/licenses/Apache-2.0.txt`). 

## DarkUI

Earlier Windows-only versions of the original application were built on DarkUI (MIT License,
Copyright (c) 2017 Robin Perris, https://github.com/RobinPerris/DarkUI). DarkUI is no longer part of
this project.

## BCnEncoder.Net

BCnEncoder.Net (and BCnEncoder.Net.ImageSharp) is distributed under the MIT License. It is used to
decode the BC7 textures of PS5 artwork for display. https://github.com/Nominom/BCnEncoder.NET

## PlayStation-Titles (title list, downloaded on request)

When you ask Packwright to look a title up by its Title ID, it downloads the PS5 title list kept by andshrew,
https://github.com/andshrew/PlayStation-Titles (MIT License, Copyright (c) 2022 andshrew), from GitHub and keeps
a copy in its data folder. The list is not part of Packwright or of any download; it is fetched only when you use
that feature. Names and IDs in it belong to their owners.

## Image format references

The managed exFAT, UFS2/FFPKG, PFSC and PFS implementations were written from public format
documentation and validated against independent tools. Credit to the authors of the tools that
document and handle these formats:

- MkPFS, PSBrew / Renan Barreto (https://github.com/PSBrew/MkPFS), PFS and PFSC/FFPFSC format work.
- UFS2Tool and LibProsperoPKG, SvenGDK (https://github.com/SvenGDK/UFS2Tool), UFS2/FFPKG work. UFS2Tool is used under the BSD 2-Clause License.
- ps5-exfat-builder, kerrdec97 (https://github.com/PSBrew/ps5-exfat-builder), exFAT format work.
- PS5 Dump and Image Converter, strongt1me (https://github.com/strongt1me/PS5-Dump-Image-Converter).

## MIT License text

This text applies to every component above that is marked "MIT License" (Avalonia
UI, ZstdSharp.Port, BCnEncoder.Net, DarkUI and the PlayStation-Titles list), each with the
copyright notice given for it in its own section.

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
