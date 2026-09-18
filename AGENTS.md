# Repository Guidelines

## Project Structure & Module Organization

`Lyrider.sln` contains one unpackaged WinUI 3 application under `src/Lyrider/`.

- `App.xaml` and `App.xaml.cs`: application startup.
- `MainWindow.xaml` and `MainWindow.xaml.cs`: the current now-playing UI and one-second refresh loop.
- `Services/CiderService.cs`: HTTP access, authentication headers, JSON parsing, and connection errors.
- `Services/TokenStore.cs`: DPAPI-protected token persistence for the current Windows user.
- `Services/ArtworkBackdrop.cs`: composition-layer backdrop that blurs the artwork with a Win2D effect.
- `Models/`: minimal DTOs matching the Cider Local API response.

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

## Coding Style & Naming Conventions

Use four-space indentation in C# and XAML, file-scoped namespaces, nullable reference types, and implicit usings. Public types and members use `PascalCase`; private fields use `_camelCase`; locals and parameters use `camelCase`. Keep API DTO property mappings explicit with `JsonPropertyName`.

Prefer small, concrete services over framework-heavy abstractions. Keep network access in `CiderService`; `MainWindow` should coordinate UI state only. Use `System.Text.Json` and reuse `HttpClient`.

## Testing Guidelines

For every change, build x64 with zero errors and exercise affected states: Cider unavailable, HTTP 401/403, valid now-playing data, and malformed responses where relevant. Never use a real Token in committed tests. Name test files `<TypeName>Tests.cs` and test methods `Method_Scenario_ExpectedResult`. Changes to window chrome, DWM attributes, or XAML styling cannot be verified by a green build — run the app and look at it.

## Security & Configuration

Never hardcode, print, or commit Cider Tokens. Persist them only through `TokenStore`, which uses DPAPI `CurrentUser`. Keep the API base URL at `http://localhost:10767/` unless a requirement explicitly changes it.

## Commit & Pull Request Guidelines

History is currently minimal. Use Conventional Commits with a lowercase scope and a concise Chinese subject, for example `feat(auth): 保存 Cider Token`. Non-trivial commits should include bullet points explaining changes and motivation. Pull requests must describe behavior changes, list verification commands, link relevant issues, and include a screenshot for visible XAML changes.
