# Third-party notices

MusicFinder ships with the components below. Versions are the ones resolved in this build
(`Directory.Packages.props`, plus the transitive packages the build pulls in). Licence identifiers are taken
from each package's own metadata (SPDX).

A unit test (`ThirdPartyNoticesTests`) fails the build if a package in `Directory.Packages.props`
is missing from this file, so the list cannot silently fall behind.

## Managed packages that are shipped

| Package | Version | Licence | Project |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Dapper | 2.1.89 | Apache-2.0 | https://github.com/DapperLib/Dapper |
| Microsoft.Data.Sqlite (and .Core) | 9.0.22 | MIT | https://learn.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Extensions.Hosting | 9.0.22 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions | 9.0.22 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 9.0.22 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.* (Configuration, Logging, Options, DependencyInjection, ...; pulled in by Hosting) | 9.0.x | MIT | https://github.com/dotnet/runtime |
| System.Diagnostics.EventLog, System.Numerics.Tensors | 9.0.x | MIT | https://github.com/dotnet/runtime |
| Serilog | 4.2.0 | Apache-2.0 | https://serilog.net |
| Serilog.Extensions.Hosting | 9.0.0 | Apache-2.0 | https://github.com/serilog/serilog-extensions-hosting |
| Serilog.Extensions.Logging | 9.0.0 | Apache-2.0 | https://github.com/serilog/serilog-extensions-logging |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| TagLibSharp | 2.3.0 | LGPL-2.1-only | https://github.com/mono/taglib-sharp |
| NAudio.Core, NAudio.Wasapi | 3.3.0 | MIT | https://github.com/naudio/NAudio |
| NAudio.Vorbis | 3.0.0 | MIT | https://github.com/naudio/Vorbis |
| NVorbis | 0.10.5 | MIT | https://github.com/NVorbis/NVorbis |
| SQLitePCLRaw.core, .provider.e_sqlite3, .lib.e_sqlite3, .bundle_e_sqlite3 | 2.1.x | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |

## Native components that are shipped

- **SQLite** (`e_sqlite3.dll`, from SQLitePCLRaw.lib.e_sqlite3). SQLite is in the public domain
  (https://www.sqlite.org/copyright.html). It is built with FTS5, which the search index requires.
- **.NET runtime and Windows Desktop runtime** (self-contained publish). MIT, Copyright (c) .NET Foundation and Contributors.
  https://github.com/dotnet/runtime, https://github.com/dotnet/wpf

## TagLibSharp (LGPL-2.1)

TagLibSharp is used for reading and writing audio tags. It is licensed under the GNU Lesser General Public
License, version 2.1. MusicFinder uses it unmodified and keeps it as its own file (`TagLibSharp.dll`) next to
the application, so it can be replaced by a different build of the same library. The library's source code and the
full licence text are available at https://github.com/mono/taglib-sharp (see `COPYING` there).

## Not shipped

- **Chromaprint `fpcalc`** (optional audio fingerprinting) is **not** included. If you want fingerprint lookups,
  download it yourself from https://acoustid.org/chromaprint and point Settings at it; its own licence applies to it.
- **Test-only packages** (xunit, Microsoft.NET.Test.Sdk, coverlet, Microsoft.Extensions.TimeProvider.Testing) are used to
  build and test MusicFinder and are not part of the release.

## Online services

MusicFinder talks to MusicBrainz and AcoustID only when you turn on online identification in Settings.
Their service terms apply to that use; no key or contact detail is bundled with the application.

## MusicFinder itself

No licence has been chosen for MusicFinder's own code yet. Add one before distributing the package to other people.
