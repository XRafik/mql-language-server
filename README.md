# MQL Language Server

English | [Español](README.es.md) | [Русский](README.ru.md)

[![CI](https://github.com/davalillo/mql-language-server/actions/workflows/ci.yml/badge.svg)](https://github.com/davalillo/mql-language-server/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/davalillo/mql-language-server)](https://github.com/davalillo/mql-language-server/releases/latest)
[![License](https://img.shields.io/github/license/davalillo/mql-language-server)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![LSP](https://img.shields.io/badge/LSP-3.17-green.svg)](https://microsoft.github.io/language-server-protocol/)

Language Server Protocol (LSP) implementation for MQL4 and MQL5 (MetaTrader 4/5). Provides IDE features like auto-completion, go-to-definition, hover info, and symbol navigation.

## Features

- Symbol extraction (functions, variables, classes, structs, interfaces, enums, includes) across MQL4 and MQL5
- Go to Definition / Declaration / Type Definition / Implementation
- Find All References and Rename with document-local scope awareness (shadowing-aware)
- Document Symbols, Workspace Symbols, Document Highlight, Folding Ranges, Selection Ranges
- Completion (builtins + local symbols, with auto-import of `#include` for resolved symbols) and Hover
- Signature Help
- Diagnostics via pull mode (`textDocument/diagnostic`): semantic rules with MQL4 1000 / MQL5 5000 code windows, MQL4-only API migration radar in MQL5 files, dialect-aware MQL standard-library enum constants, and cross-file unresolved-symbol suppression through the include closure and workspace index
- Code Actions (include-assist QuickFix) and Color Swatches (`documentColor` / `colorPresentation`)
- Document Formatting and Range Formatting
- Preprocessor-aware parsing: function-like and object-like macro expansion, nested include chains, include-order conditional merge, and MQL Controls library event-map macros (`ON_EVENT`, `EVENT_MAP_BEGIN`/`END`)
- Parse-reuse LRU cache across didOpen/didClose cycles for large files
- Project-local `.mqlignore` to exclude paths (backup copies, installed terminals) from the workspace scan
- Startup readiness signals: the workspace scan emits `$/progress` (`workDoneProgress`) tokens to clients that declare the capability, and requests batched with the `initialize` handshake are answered instead of dropped (#160)
- Cross-platform binaries: Linux x64/ARM64, macOS Intel/Apple Silicon, Windows x64/ARM64

## MQL5 Support

This release adds first-class MQL5 support while keeping MQL4 behavior intact:

- `.mq5` and `.mqh` files are recognized automatically.
- MQL5-specific syntax is parsed: classes, structs, interfaces, inheritance, templates, `enum class`, `nullptr`, `union`, `final`, `pack(n)`, reference parameters, `using`, `#resource`, initialization lists, and heap `new`/`delete`.
- MQL5 built-in functions and predefined variables are included in completion and hover.
- Diagnostics for MQL5 files use a distinct `MQL5xxx` code range so CI filters can separate MQL4 and MQL5 issues.

## Workspace Scan Exclusions (`.mqlignore`)

The startup workspace scan indexes every `.mq4`/`.mq5`/`.mqh` file under each workspace folder, pruning built-in directories (`.git`, `bin`, `obj`, …) and dot-directories. A project can exclude its own paths by placing a **`.mqlignore`** file at the root of the workspace folder (issue #157).

Syntax (gitignore subset, last match wins):

- `#` comments and blank lines are ignored.
- `backup-utf8/` — a trailing `/` makes the pattern directory-only (it never matches a same-named file).
- `/src/gen.mq4` — a leading `/` anchors the pattern to the folder root; without it, the pattern matches at any depth.
- `*.mq4`, `?`, `**` — wildcards; `**` as a full segment matches zero or more segments.
- `!keep.mq4` — negation re-includes a path matched by an earlier pattern (last match wins).
- CRLF and a UTF-8 BOM are handled; malformed lines are skipped; matching is case-insensitive.

Semantics:

- A matched directory is pruned entirely — the scan pays zero cost for ignored subtrees, and the scan-start notification counts reflect the pruned candidate count.
- `.mqlignore` can only ADD exclusions: negation cannot resurrect built-in exclusions (`.git`, dot-directories) or enumeration rules (hidden/system attributes, reparse points).
- The ignore governs the **workspace index only**: opening an ignored file (`didOpen`) still gives it full document-level analysis.
- An ignored `.mqh` is not indexed; sources that include it may report unresolved symbols for its declarations ("ignored" means "outside the index").
- The file is read once per scan at startup — there is no watcher, so editing it requires a server restart.

## System Include Roots (`systemIncludeRoots`)

Angle-bracket includes (`#include <Trade\Trade.mqh>`) are system-library includes: they resolve against the MetaTrader terminal's standard-library directories, which are outside every workspace folder. The server accepts a **`systemIncludeRoots`** initialization option — an array of absolute directory paths, typically the terminal's `MQL5/Include` / `MQL4/Include` directories:

```json
{
  "initializationOptions": {
    "systemIncludeRoots": [
      "/path/to/MetaTrader/MQL5/Include"
    ]
  }
}
```

Semantics:

- Angle-bracket entries resolve against each configured root (in order), with path-separator normalization (`\` and `/` both accepted) and a Windows-style case-insensitive existence check; `..` traversal escaping the root is rejected.
- A resolved header is parsed with the dialect parser and lazily indexed into the global symbol index at `didOpen` (deduplicated — an already-indexed header is never re-parsed).
- Once indexed, Standard-Library classes and types (`CTrade`, `CArrayObj`, `MqlTradeRequest`, …) work with definition, hover, references, completion and the cross-file diagnostic suppression — no false `Undeclared symbol` positives for them.
- Fail open: with no roots configured, or when a header cannot be resolved or parsed, behavior is exactly as before — no errors, no extra diagnostics.
- Quoted includes (`#include "file.mqh"`) are unaffected: they still resolve relative to the including file.

### Project configuration file (`.mql-lsp.json`)

Roots can also be declared per project, so a repository carries its own configuration and every editor session picks it up without client settings:

```json
{
  "systemIncludeRoots": [
    "../Include",
    "C:/Program Files/MetaTrader 5/MQL5/Include"
  ]
}
```

- The file is read from each workspace-folder root during `initialize`; like `.mqlignore` there is no watcher, so editing it requires a server restart.
- Relative entries resolve against the directory containing `.mql-lsp.json`, which keeps the file portable across machines; absolute entries are used as-is.
- Precedence: a non-empty `systemIncludeRoots` initialization option wins and the project files are not consulted; otherwise the union across workspace folders applies. With neither source the server stays in fail-open mode.
- A malformed or unreadable file is ignored and a warning is logged — it never breaks the session.
- Inside a declared root, a quoted parent-relative hop that stays inside that same root is followed by the include closure exactly like the angle form: `#include "..\Arrays\ArrayLong.mqh"` written in `Trade\Trade.mqh` contributes its symbols (issue #186). A hop that leaves every declared root, or that lands in a *different* declared root, is still rejected.

### Unresolved system includes

When an angle-bracket include cannot be resolved, the server reports one `Warning` per include directive (`1004` for MQL4, `5004` for MQL5, source `mql-lsp`) naming the include entry and both remedies. Nothing is suppressed: `Undeclared symbol` diagnostics (`1070`/`5070`) are unchanged, so the warning explains the cause without hiding the effect.

Maintainers can validate resolution against a real terminal with the opt-in script `./tools/verify-system-includes.sh /path/to/MQL5/Include`, which emits JSON evidence and exits non-zero when symbols behind the Standard Library stay unresolved. It never runs in CI.

## Technical Decisions

This section documents the key technical decisions made during development to ease the onboarding of new developers.

### 1. Parser Strategy: ANTLR 4.13.1

**Chosen**: ANTLR 4.13.1 with Antlr4BuildTasks 12.14.0

**Alternatives considered**:
- Regex (rejected - insufficient for complex MQL code)
- Sprache (rejected - parser combinator, less robust for complex grammars)
- Superpower (rejected - newer, less documentation)
- Irony (rejected - not maintained)

**Main reason**:
The initial decision to use regex was reversed after experiencing limitations when parsing real MQL code. ANTLR provides:
- A formal and maintainable grammar
- An accurate Abstract Syntax Tree (AST)
- Better support for LSP use cases
- Robustness against complex syntax

**Lesson learned**: For an LSP that needs to parse complex code, regex is insufficient. ANTLR offers a perfect balance between robustness and ease of use.

### 2. ANTLR Tooling: Antlr4BuildTasks 12.14.0

**Chosen**: Antlr4BuildTasks 12.14.0 (auto-downloads JRE)

**Alternative**: Manual ANTLR installation + Java JDK

**Main reason**:
Avoid manual dependencies in the development environment. Antlr4BuildTasks:
- Automatically downloads the JRE and the ANTLR tool jar
- Requires no prior Java installation
- Works cross-platform (Windows, Linux, macOS)
- Runs during the MSBuild/dotnet build

**Configuration in .csproj**:
```xml
<PackageReference Include="Antlr4BuildTasks" Version="12.14.0" PrivateAssets="All" />
<Antlr4 Include="Mql4\Grammar\Mql4Grammar.g4">
  <AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>
</Antlr4>
```

**Lesson learned**: Antlr4BuildTasks is the ideal solution for .NET + ANTLR without manually configuring Java. Version 12.14.0 is stable and reliable.

### 3. LSP Libraries: OmniSharp.Extensions

**Chosen**: OmniSharp.Extensions.LanguageProtocol 0.19.9

**Alternative considered**: Microsoft.LanguageServer.Protocol (does not exist)

**Problem found**:
`Microsoft.LanguageServer.Protocol` does not exist on NuGet. It was a common mistake to assume that Microsoft maintained official LSP libraries for .NET.

**Migration performed**:
- Original: `Microsoft.LanguageServer.Protocol` (does not exist)
- Final: `OmniSharp.Extensions.LanguageProtocol` 0.19.9
- Related packages: `OmniSharp.Extensions.JsonRpc`, `OmniSharp.Extensions.LanguageServer.Shared`

**Lesson learned**: OmniSharp is the de facto standard for LSP in .NET, not Microsoft. It is actively maintained and widely used.

### 4. Grammar Strategy: Pragmatic Simplification

**Chosen**: Simplified but functional MQL grammar

**Alternative**: Full grammar with all MQL features

**Main reason**:
An LSP does not need to parse the entire language semantics, only enough syntactic structure to:
- Extract symbols (functions, variables)
- Find definitions and references
- Provide completions and hover

**Adopted approach**:
```antlr
// Example: Simplified but functional grammar
variableDeclaration
    : dataType IDENTIFIER (ASSIGN expression)? SEMICOLON
    ;
```

vs

```antlr
// Complex alternative: Not needed for LSP
variableDeclaration
    : storageClass? dataType IDENTIFIER (ASSIGN expression)? SEMICOLON
    | storageClass? dataType IDENTIFIER LBRACKET expression? RBRACKET SEMICOLON
    ;
```

**Lesson learned**: An effective LSP does not require parsing the whole language. Pragmatic simplification is key.

### 5. Build Configuration: AntlrOutDir

**Configuration**: `<AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>`

**Problem solved**:
By default, ANTLR generates files in `obj/Debug/net10.0/`. Without AntOutDir, a manual copy to `src/Parser/Generated/` would be required.

**Full configuration**:
```xml
<Antlr4 Include="Mql4\Grammar\Mql4Grammar.g4">
  <Generator>MSBuild:Compile</Generator>
  <Listener>true</Listener>
  <Visitor>true</Visitor>
  <Package>Mql4Grammar</Package>
  <AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>
</Antlr4>
```

**Benefit**:
- Automatic generation in the correct location
- No manual post-build copy
- Files visible in source control

### 6. Key Lessons Learned

#### K_ token prefix
Avoid conflicts between keywords and tokens:
```antlr
// BAD - Conflict with token DOUBLE
DOUBLE : 'double';

// GOOD - Prefix for keywords
K_DOUBLE : 'double';
dataType : K_DOUBLE | IDENTIFIER;
```

#### Uppercase context methods
ANTLR generates methods with exact token names:
```csharp
// BAD - compile error
var nameToken = context.identifier();

// GOOD - works
var nameToken = context.IDENTIFIER();
```

#### Comment visibility
`-> skip` requires a specific channel:
```antlr
// BAD - ANTLR compilation error
COMMENT : '/*' .*? '*/' -> skip;

// GOOD - Works
COMMENT : '/*' .*? '*/' -> channel(HIDDEN);
// Or separate rules:
COMMENT_BLOCK : '/*' .*? '*/' -> skip;
```

#### Simplicity vs Complexity
A simple parser that works is better than a complex one that fails.

### Rebuilding the ANTLR Parser

```bash
# Full build (regenerates parsers automatically)
dotnet build -c Release

# Files are generated in Parser/Generated/:
# MQL4 namespace Mql4Grammar:
# - Mql4GrammarParser.cs
# - Mql4GrammarLexer.cs
# - Mql4GrammarBaseVisitor.cs
# - Mql4GrammarListener.cs
# - Mql4GrammarVisitor.cs
# MQL5 namespace Mql5Grammar:
# - Mql5GrammarParser.cs
# - Mql5GrammarLexer.cs
# - Mql5GrammarBaseVisitor.cs
# - Mql5GrammarListener.cs
# - Mql5GrammarVisitor.cs
```

No additional steps required. Antlr4BuildTasks handles everything automatically.

### Current Status

- ✅ Dual ANTLR parsers (MQL4 + MQL5), preprocessor-aware (macro expansion, nested includes, conditionals)
- ✅ Full LSP surface: 20+ registered handlers (symbols, definitions, references, rename, completion, hover, signature help, diagnostics pull mode, code actions, color, formatting, folding, selection range, workspace symbols, moniker, inlay hints)
- ✅ Symbol index: workspace scan + occurrence index + parse-reuse LRU cache
- ✅ Test suite: 1192 tests green (`dotnet test`, excludes Performance/FpMeasurement categories)
- ✅ Binaries: Linux x64/ARM64, macOS x64/ARM64, Windows x64/ARM64 (self-contained, single-file)
- ✅ CI/CD: GitHub Actions — PR CI, release pipeline with native-ARM smoke tests, dependency vulnerability gate
- ✅ Published: GitHub Releases and nuget.org (`mql-language-server`, stable 2.4.0) via Trusted Publishing

## Security

The NuGet vulnerability warnings described in earlier revisions of this section were **resolved on 2026-09-10**: the vulnerable transitive dependencies were eliminated by dependency updates, and the build audit is clean. See [docs/references/SECURITY.md](docs/references/SECURITY.md) for the historical analysis.

## Testing

Run unit tests:
```bash
dotnet test
```

Test coverage: 1192 tests (see the [CHANGELOG](CHANGELOG.md) for the current suite status) covering the parser, LSP handlers, and edge cases.

### Code Coverage with Coverlet

This project uses **Coverlet** for measuring code coverage. Coverlet is a cross-platform code coverage library for .NET that provides comprehensive coverage reports.

#### Installing Coverlet

Install Coverlet as a global .NET tool:
```bash
dotnet tool install --global coverlet.console
```

Or use it directly with dotnet without installation:
```bash
dotnet tool install --tool-path . coverlet.console
```

#### Running Tests with Coverage

**Option 1: Coverlet as global tool**
```bash
# Basic coverage report
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build"

# Generate detailed coverage report in OpenCover format
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build" --format opencover --output ./coverage/coverage.xml

# Generate JSON coverage report
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build" --format json --output ./coverage/coverage.json

# Set coverage thresholds (fails build if below threshold)
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build" --threshold 80 --threshold-type line --threshold-stat total
```

**Option 2: Using Coverlet.MSBuild (package reference)**
Add to your test project (.csproj):
```xml
<PackageReference Include="coverlet.collector" Version="6.0.0">
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Then run:
```bash
dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
```

**Option 3: Simple local report**
```bash
# Build the project
dotnet build -c Release

# Run tests with coverage
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build"
```

#### Coverage Reports

Coverlet supports multiple output formats:

1. **Console** (default): Displays summary in terminal
2. **JSON**: Structured data for CI/CD integration
   ```bash
   --format json --output ./coverage/coverage.json
   ```
3. **OpenCover**: Industry-standard format
   ```bash
   --format opencover --output ./coverage/coverage.xml
   ```
4. **Cobertura**: Another common format
   ```bash
   --format cobertura --output ./coverage/cobertura.xml
   ```
5. **LCov**: For integration with CI systems
   ```bash
   --format lcov --output ./coverage/lcov.info
   ```

#### Coverage Thresholds

Set minimum coverage thresholds to ensure code quality:
```bash
# Fail if total line coverage is below 80%
--threshold 80 --threshold-type line --threshold-stat total

# Fail if any assembly falls below 70%
--threshold 70 --threshold-type line --threshold-stat assembly

# Fail if any class falls below 60%
--threshold 60 --threshold-type line --threshold-stat class
```

Combined thresholds:
```bash
--threshold 80 --threshold-type line --threshold-stat total
--threshold 90 --threshold-type method --threshold-stat total
```

#### Integration with CI/CD

Add to your GitHub Actions workflow:
```yaml
- name: Run tests with coverage
  run: |
    dotnet tool install --global coverlet.console
    coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll \
      --target "dotnet" \
      --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build" \
      --format opencover \
      --output ./coverage/coverage.xml

- name: Upload coverage to Codecov
  uses: codecov/codecov-action@v3
  with:
    file: ./coverage/coverage.xml
```

#### Viewing Coverage Reports

1. **Terminal**: Immediate feedback after running tests
2. **Visual Studio**: Open `coverage.json` or `coverage.xml` in Visual Studio
3. **Web**: Use tools like [ReportGenerator](https://github.com/danielpalme/ReportGenerator) to generate HTML reports:
   ```bash
   dotnet tool install --global dotnet-reportgenerator-globaltool
   reportgenerator -reports:./coverage/coverage.xml -targetdir:./coverage/html -reporttypes:Html
   open ./coverage/html/index.html
   ```

#### Coverage Best Practices

- **Target**: Aim for 80%+ line coverage for critical paths
- **Quality over quantity**: Better to have meaningful tests than high coverage on trivial code
- **Integration tests**: Cover cross-component interactions
- **Edge cases**: Test error handling and boundary conditions
- **Exclusions**: Exclude generated code and test utilities:
  ```bash
  --exclude-by-file "**/Generated/**" \
  --exclude-by-attribute "*GeneratedCodeAttribute*"
  ```

## CI/CD

Automated builds and releases via GitHub Actions:

### Workflow Triggers:

**main branch & PRs** (fast CI, `.github/workflows/ci.yml`):
- ✅ Build + unit tests on every push to `main` and every PR
- ✅ Ubuntu only (multi-platform validation happens at release time)
- ⚡ No artifact generation (faster)
- ⚡ Performance and FpMeasurement tests excluded (timing-sensitive; run locally)

**Tags v\*** (releases):
- ✅ Multi-platform builds
- ✅ Automated testing
- ✅ Binary releases (GitHub Releases)
- ✅ NuGet packaging
- ✅ Checksums (SHA256)
- ✅ Binary validation tests

### Release Process:

```bash
# Development (main branch)
git commit -am "feature: new capability"
git push origin main
# → Build + Tests (~3-5 minutes)

# Release
git tag v2.0.1
git push origin v2.0.1
# → Build + Tests + Release + Artifacts (~15-20 minutes)
# → All artifacts uploaded to GitHub Releases automatically
```

See [.github/workflows/build.yml](.github/workflows/build.yml) for details.

## Installation

### Standalone Binaries (Recommended)

Download a pre-built binary from [GitHub Releases](https://github.com/davalillo/mql-language-server/releases):

- **Linux**: x64 and ARM64 (`mql-lsp-server-linux-*`, self-contained)
- **macOS**: Intel and Apple Silicon (`mql-lsp-server-osx-*`, self-contained)
- **Windows**: x64 and ARM64 (`mql-lsp-server-win-*.exe`, self-contained)

Make executable (Linux/macOS):
```bash
chmod +x mql-lsp-server
```

### Via .NET Tool (nuget.org)

The package is published on nuget.org (stable channel); release candidates install with `--prerelease`.

```bash
dotnet tool install -g mql-language-server
```

Or install from local build:
```bash
./pack.ps1
dotnet tool install -g mql-language-server --add-source ./nupkg
```

### From Source

**Prerequisites**: .NET 10 SDK

**Linux/macOS**:
```bash
git clone https://github.com/davalillo/mql-language-server.git
cd mql-language-server
./build.sh

# Test the binary
./src/bin/linux-x64/mql-lsp-server --stdio
```

**Windows**:
```powershell
git clone https://github.com/davalillo/mql-language-server.git
cd mql-language-server
.\build.ps1

# Test the binary
.\src\bin\win-x64\mql-lsp-server.exe --stdio
```

### Build Outputs

After building, find binaries in:
- `src/bin/linux-x64/mql-lsp-server`
- `src/bin/osx-x64/mql-lsp-server`
- `src/bin/win-x64/mql-lsp-server.exe`
- `src/bin/linux-arm64/mql-lsp-server`
- `src/bin/osx-arm64/mql-lsp-server`
- `src/bin/win-arm64/mql-lsp-server.exe`

## Usage

### Command Line
```bash
mql-lsp-server --stdio
```

### VSCode

VS Code and other editors are configured through a generic LSP client extension — see [Editor Integration](docs/guides/EDITOR_INTEGRATION.md) for per-editor setup (VS Code, Neovim, Emacs, Vim, Sublime Text).

## Documentation

More guides and references live in the [`docs/`](docs/README.md) directory:

- [Editor Integration](docs/guides/EDITOR_INTEGRATION.md) — VSCode, Neovim, Emacs, Vim, Sublime Text setup
- [Local Installation](docs/guides/LOCAL_INSTALLATION.md) — Install the published nuget.org tool or a locally built package
- [Distribution](docs/guides/DISTRIBUTION.md) — GitHub Releases, NuGet, GitHub Packages, private feeds
- [Manual Testing](docs/guides/MANUAL_TESTING.md) — Verify LSP features by hand
- [FAQ](docs/references/FAQ.md) — Frequently asked questions and troubleshooting
- [Security Policy](SECURITY.md) — How to report vulnerabilities (canonical, referenced by GitHub)
- [Dependency Security Analysis](docs/references/SECURITY.md) — Historical NuGet vulnerability audit (RESOLVED)
- Architecture: see `docs/` and the architecture overview below. A detailed architecture wiki can be generated from the GitNexus knowledge graph with `npx gitnexus analyze && npx gitnexus wiki` (output in `.gitnexus/wiki/`, not published in the repository).
- [Changelog](CHANGELOG.md) — Version history
- [Contributing](CONTRIBUTING.md) — How to build, test, and submit changes
- [Code of Conduct](CODE_OF_CONDUCT.md) — Community standards and enforcement

## License

MIT — third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).