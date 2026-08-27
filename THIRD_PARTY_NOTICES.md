# Third-party notices

This inventory is based on the resolved Windows x64 package graph and the files produced by the Release build. Review it whenever a package lock file, target runtime, bundled asset, or installer dependency changes.

## Redistributed .NET and native components

The application package redistributes the following components:

- **.NET 10 runtime and Windows Desktop runtime** — MIT; copyright Microsoft Corporation and .NET Foundation contributors. The self-contained application includes the runtime rather than requiring a separate .NET installation. Source and upstream notices: https://github.com/dotnet/dotnet
- **CommunityToolkit.Mvvm 8.4.2** — MIT; copyright .NET Foundation and contributors. Source: https://github.com/CommunityToolkit/dotnet
- **Google OR-Tools 9.15.6755 and its Windows x64 runtime** — Apache License 2.0; copyright Google LLC. The runtime package supplies OR-Tools and native dependency libraries including Abseil, bzip2, HiGHS, Protobuf, RE2, SCIP, and zlib. Source, licence, and dependency inventory: https://github.com/google/or-tools/tree/v9.15
- **Google.Protobuf 3.33.1** — BSD 3-Clause; copyright Google Inc. Source: https://github.com/protocolbuffers/protobuf
- **Microsoft.Data.Sqlite.Core 10.0.11** — MIT; copyright Microsoft Corporation. Source: https://github.com/dotnet/efcore
- **SQLitePCLRaw.core and SQLitePCLRaw.provider.winsqlite3 3.0.5** — Apache License 2.0; copyright SourceGear, LLC. The provider uses the `winsqlite3` library supplied by Windows; it does not add a native SQLite library to the application package. Source: https://github.com/ericsink/SQLitePCL.raw
- **Microsoft.Web.WebView2 SDK and Loader 1.0.4129.50** — distributed under the licence included in the NuGet package; copyright Microsoft Corporation. Colony Optimiser redistributes the SDK assemblies and `WebView2Loader.dll`.
- **Microsoft Edge WebView2 online bootstrapper** — the release build downloads Microsoft's signed online bootstrapper and includes it in each distributable. It does not bundle or preinstall the Evergreen Runtime. If WebView2 initialisation fails, the application can invoke the bootstrapper to install or repair the runtime; this requires an internet connection and presents Microsoft's installer terms. Distribution guidance: https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution

The release package includes this inventory, the Colony Optimiser MIT licence, the WebView2 licence and NOTICE file, the .NET runtime licence and third-party notices, and the bundled ELK licence. Package metadata and upstream repositories remain authoritative for component-specific terms. The OR-Tools NuGet packages declare Apache-2.0 but do not supply separate licence or NOTICE files; this inventory records OR-Tools and the native libraries found in the published Windows package.

## Bundled visualisation assets

The offline visualisation assets include the following open-source components.

## D3.js 5.16.0

Copyright 2010-2017 Mike Bostock

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the author nor the names of contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Source: https://github.com/d3/d3/tree/v5.16.0

## d3-sankey-circular and elementary-circuits-directed-graph

Copyright (c) 2017 Tom Shanley

Copyright (c) 2018 Antoine Roy-Gobeil

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Sources: https://github.com/tomshanley/d3-sankey-circular and https://github.com/antoinerg/elementary-circuits-directed-graph

## Eclipse Layout Kernel for JavaScript (elkjs) 0.12.0

The node visualiser includes elkjs 0.12.0, licensed under the Eclipse Public License 2.0. The full licence text is included at `src/ColonyOptimizer.App/Assets/Visualisation/LICENSES/ELK-EPL-2.0.md` and is copied into the application package.

Source: https://github.com/kieler/elkjs

## Build-time dependencies

## WiX Toolset

The MSI and Setup EXE are built with WiX Toolset 5.0.0. WiX is an installer build dependency; the release packages use its Windows Installer and bootstrapper components. WiX is licensed under the Microsoft Reciprocal License.

Source: https://github.com/wixtoolset/wix
