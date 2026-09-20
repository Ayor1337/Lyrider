# Repository Guidelines

## Project Structure & Module Organization

`Lyrider.sln` contains two projects: the unpackaged WinUI 3 application and a WPF/Win32 interop library, both in the same process.

**`src/Lyrider/`** — the WinUI 3 application:

- `App.xaml` and `App.xaml.cs`: application startup.
- `MainWindow.xaml` and `MainWindow.xaml.cs`: the current now-playing UI and one-second refresh loop.
- `Services/CiderService.cs`: HTTP access, authentication headers, JSON parsing, and connection errors.
- `Services/LyricsService.cs` and `Services/LyricsProviders.cs`: lyrics-source selection and the Cider/NetEase/QQ Music/Musixmatch/LRCLIB provider adapters, including Apple storefront alias resolution, matching, fallback, parsing, and translation alignment.
- `Services/TokenStore.cs`: DPAPI-protected Cider Token and Musixmatch API Key persistence for the current Windows user.
- `Services/ArtworkBackdrop.cs`: composition-layer backdrop that blurs the artwork with a Win2D effect.
- `Models/`: minimal DTOs matching the Cider Local API response.

**`src/Lyrider.TaskbarWidget/`** — WPF windows and Win32 interop, referenced by the app above:

- `TaskbarWidgetHost.cs`, `TaskbarWidgetWindow.xaml` and `.xaml.cs`: the Windows 11 taskbar playback bar, parented into `Shell_TrayWnd` on its own STA thread.
- `TrayIconHost.cs`, `TrayMenuWindow.xaml` and `.xaml.cs`: the system tray icon and its context menu.
- `WindowMaterial.cs`: asks DWM for the acrylic system backdrop, reporting whether it applied so callers can fall back to an opaque background.
- `NativeMethods.cs`: all P/Invoke declarations, plus the `NativeRect` / `NativePoint` / `NativeMargins` structs.
- `TaskbarPlacement.cs`, `TrayMenuPlacement.cs`, `TaskbarPresentation.cs`: pure layout and text logic with no WPF or Win32 types, source-linked into the test project. `TaskbarPlacement.cs` also declares the `PixelPoint` / `PixelRect` records the others build on.
- `TaskbarContracts.cs`: the `TaskbarPlaybackState` and `TaskbarDisplayText` records plus the `TaskbarPlaybackCommand` enum.

**`scripts/`** holds repository tooling rather than application code: `package-msix.ps1` builds and packs the MSIX.

Build artifacts belong in `bin/` and `obj/`; both are ignored. Tests live in `tests/Lyrider.Tests/` rather than beside production classes.

**`tests/Lyrider.Tests` is deliberately not in `Lyrider.sln`.** It targets plain `net10.0` without `UseWPF`/`UseWindowsForms`, so it cannot `ProjectReference` the widget project; instead `Lyrider.Tests.csproj` source-links individual production files via `<Compile Include="..\..\src\...">`. Two consequences:

- `dotnet build .\Lyrider.sln` never builds the tests, so a `dotnet test --no-build` run silently executes a stale assembly. Let `dotnet test` build, or build the test project explicitly.
- A source-linked file may not reference `System.Windows`, `System.Windows.Forms`, `System.Drawing`, or any type in a file that is not itself linked (for example `NativeMethods`). Keep pure-logic helpers free of those, and add the file to the `<Compile Include>` list to make it testable.

## Build, Test, and Development Commands

Run commands from the repository root:

```powershell
dotnet restore .\Lyrider.sln -p:Platform=x64
dotnet build .\Lyrider.sln -p:Platform=x64 --no-restore
dotnet run --project .\src\Lyrider\Lyrider.csproj -p:Platform=x64
dotnet test .\tests\Lyrider.Tests\Lyrider.Tests.csproj
```

Point `dotnet test` at the test project directly — it is not in the solution, and do not pass `--no-build` unless you just built that project.

Use `-c Release` for release verification. Close Lyrider before rebuilding Debug because the running executable locks the output file. Run `git diff --check` before handing off changes.

### Packaging

```powershell
.\scripts\package-msix.ps1 -Version 1.0.1.0
```

The script builds self-contained for `x64`, uses the build output as the package layout, writes `AppxManifest.xml`, packs with `makeappx`, and signs with the `CN=Lyrider` self-signed certificate in the current user's certificate store (creating it on first run). Output lands in `AppPackages/Lyrider_<version>_<arch>/` as an `.msix` plus the exported `Lyrider.cer`. Installing elsewhere requires trusting that certificate (`Cert:\CurrentUser\TrustedPeople`); the same package on a machine without it fails to deploy.

Do not pack the `dotnet publish` output: publish omits `App.xbf`, `MainWindow.xbf`, and `Lyrider.pri` for this project, and a package built from it fails to start. The script asserts those files exist and copies the layout itself for that reason.

## Coding Style & Naming Conventions

Use four-space indentation in C# and XAML, file-scoped namespaces, nullable reference types, and implicit usings. Public types and members use `PascalCase`; private fields use `_camelCase`; locals and parameters use `camelCase`. Keep API DTO property mappings explicit with `JsonPropertyName`.

Prefer small, concrete services over framework-heavy abstractions. Keep network access in `CiderService`; `MainWindow` should coordinate UI state only. Use `System.Text.Json` and reuse `HttpClient`.

## Testing Guidelines

For every change, build x64 with zero errors and exercise affected states: Cider unavailable, HTTP 401/403, valid now-playing data, and malformed responses where relevant. Never use a real Token in committed tests. Name test files `<TypeName>Tests.cs` and test methods `Method_Scenario_ExpectedResult`. Changes to window chrome, DWM attributes, or XAML styling cannot be verified by a green build — run the app and look at it.

## Security & Configuration

Never hardcode, print, or commit Cider Tokens. Persist them only through `TokenStore`, which uses DPAPI `CurrentUser`. Keep the API base URL at `http://localhost:10767/` unless a requirement explicitly changes it.

## Commit & Pull Request Guidelines

History is currently minimal. Use Conventional Commits with a lowercase scope and a concise Chinese subject, for example `feat(auth): 保存 Cider Token`. Non-trivial commits should include bullet points explaining changes and motivation. Pull requests must describe behavior changes, list verification commands, link relevant issues, and include a screenshot for visible XAML changes.
