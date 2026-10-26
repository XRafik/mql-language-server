# Guía de Distribución - MQL Language Server

Esta guía documenta todos los métodos de distribución disponibles para el MQL Language Server (MQL4/MQL5).

## 📦 Métodos de Distribución

### 1. Extensión VS Code (Prioridad 1 — Recomendado)

**¿Qué es?**
- Extensión `davalillo.mql-language-server` distribuida como VSIX en GitHub Releases
- Resuelve, descarga y verifica el binario `mql-lsp-server` automáticamente (SHA256 contra `CHECKSUMS.txt`)
- El usuario final no configura nada: instalar y abrir un `.mq4`/`.mq5`

**Ventajas**:
- ✅ Instalación en un paso (**Extensions: Install from VSIX…**)
- ✅ Descarga automática y verificada del binario por plataforma
- ✅ No requiere .NET runtime ni configuración manual del editor

**Desventajas**:
- ❌ Solo VS Code (otros editores usan los binarios standalone, ver [Editor Integration](EDITOR_INTEGRATION.md))
- ❌ Publicación en el Marketplace aplazada (requiere cuenta de publisher y `VSCE_PAT`)

**Detalles completos**: ver [VS Code Extension](VSCODE_EXTENSION.md).

### 2. Standalone Binaries (Recomendado)

**¿Qué son?**
- Binarios ejecutables auto-contenidos
- NO requieren .NET runtime instalado
- Incluyen todo lo necesario (~71-72MB cada uno)

**Ventajas**:
- ✅ Instalación simple (descargar + ejecutar)
- ✅ No dependencias externas
- ✅ Multiplataforma (Linux, Windows, macOS Intel y Apple Silicon)
- ✅ Ideal para usuarios finales

**Desventajas**:
- ❌ Tamaño grande (~71-72MB por binario)
- ❌ Updates manuales

**Cómo distribuir**:
```bash
# 1. Build script
./build.sh  # Linux/macOS
# .\build.ps1  # Windows

# 2. Binarios generados en:
# src/bin/linux-x64/mql-lsp-server
# src/bin/osx-x64/mql-lsp-server
# src/bin/win-x64/mql-lsp-server.exe

# 3. Subir a GitHub Releases
# Automático: GitHub Actions (configurado en .github/workflows/build.yml)
# El workflow genera CHECKSUMS.txt con los SHA256 de todos los binarios
```

**Para usuarios finales**:
```bash
# Descargar desde GitHub Releases (Linux x64)
wget https://github.com/davalillo/mql-language-server/releases/latest/download/mql-lsp-server-linux-x64
chmod +x mql-lsp-server-linux-x64

# Ejecutar
./mql-lsp-server-linux-x64 --stdio
```

**Artefactos publicados por el CI** (ver `.github/workflows/build.yml`):
- `mql-language-server-<tag>.vsix` (extensión VS Code)
- `mql-lsp-server-linux-x64`
- `mql-lsp-server-osx-x64`
- `mql-lsp-server-osx-arm64` (Apple Silicon)
- `mql-lsp-server-win-x64.exe`
- `CHECKSUMS.txt`
- `mql-language-server.<versión>.nupkg`

### 3. .NET Global Tool (NuGet)

**¿Qué es?**
- Paquete NuGet distribuido via nuget.org o GitHub Packages
- Se instala globalmente con: `dotnet tool install -g mql-language-server`
- Requiere .NET 10 SDK (framework-dependent)

**Ventajas**:
- ✅ Instalación moderna: `dotnet tool install -g mql-language-server`
- ✅ Actualizaciones via `dotnet tool update -g mql-language-server`
- ✅ Gestión de dependencias automática
- ✅ Ideal para desarrolladores .NET

**Desventajas**:
- ❌ Requiere .NET 10 SDK instalado
- ❌ Tamaño menor pero necesita runtime
- ❌ Registro en nuget.org o GitHub Packages

**Configuración del Proyecto** (ver `src/MqlLanguageServer.Server.csproj`):
```xml
<!-- En .csproj -->
<PropertyGroup>
  <!-- NuGet Tool Configuration -->
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>mql-lsp-server</ToolCommandName>
  <PackageId>mql-language-server</PackageId>
  <Authors>MQL Language Server Team</Authors>
  <Description>Language Server Protocol implementation for MQL4 and MQL5 (MetaTrader 4/5)</Description>
  <PackageTags>lsp;mql4;mql5;metatrader;language-server</PackageTags>
</PropertyGroup>
```

**Crear Paquete**:
```bash
# Método 1: Con dotnet pack
dotnet pack src/MqlLanguageServer.Server.csproj -c Release -o ./nupkg

# Método 2: pack.ps1 script (PowerShell)
.\pack.ps1
```

**Distribuir**:

**Opción A: NuGet.org (Trusted Publishing — recomendado)**

NuGet.org ya no recomienda API keys de larga duración: se usa *Trusted Publishing* (OIDC).
El workflow de release (`.github/workflows/build.yml`, job `publish-binaries`) ya está
configurado: solicita una API key efímera (1h, de un solo uso) con la acción `NuGet/login@v1`
y publica con `dotnet nuget push`.

Configuración única (manual, en nuget.org y GitHub):
1. En nuget.org → tu usuario → **Trusted Publishing** → añadir política:
   - Repository Owner: `davalillo`
   - Repository: `mql-language-server`
   - Workflow File: `build.yml` (solo el nombre del archivo)
2. En GitHub → repo → Settings → Secrets and variables → Actions:
   crear el secret `NUGET_USER` con tu nombre de perfil de nuget.org (no el email).
3. Nota: las políticas nuevas quedan provisionales 7 días hasta el primer publish
   exitoso, que las activa de forma permanente.

Publicación manual (alternativa, no recomendada):
```bash
dotnet nuget push ./nupkg/*.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate

# Instalación para usuarios:
dotnet tool install -g mql-language-server
```

**Opción B: GitHub Packages (Recomendado)**
```bash
# 1. Habilitar GitHub Packages en el repo
# Repo Settings → Actions → General → Workflow permissions
# Enable "Read and write permissions"

# 2. Autenticación con GitHub Token
export GITHUB_TOKEN="ghp_xxx..."
dotnet nuget add source "https://nuget.pkg.github.com/davalillo/index.json" \
  --name "GitHub" \
  --username "davalillo" \
  --password "$GITHUB_TOKEN"

# 3. Subir paquete
dotnet nuget push ./nupkg/*.nupkg \
  --api-key "$GITHUB_TOKEN" \
  --source "github"

# 4. Para usuarios (instalar desde GitHub Packages):
dotnet tool install -g mql-language-server \
  --add-source "https://nuget.pkg.github.com/davalillo/index.json"

# Configurar source permanente:
dotnet nuget add source "https://nuget.pkg.github.com/davalillo/index.json" \
  --name "GitHub" \
  --username "davalillo" \
  --password "$GITHUB_TOKEN"

# Ahora instalación simple:
dotnet tool install -g mql-language-server
```

### 4. Distribución Privada/Local

Para empresas o distribución limitada sin repositorios públicos:

```bash
# Crear paquete local
dotnet pack src/MqlLanguageServer.Server.csproj -c Release -o ./nupkg

# Distribuir vía ZIP, email, etc.
zip -r mql-language-server-nupkg.zip nupkg/

# Para usuarios (instalar desde source local):
dotnet tool install -g mql-language-server \
  --add-source ./nupkg
```

## 🔐 Seguridad: Checksums

**¿Por qué son importantes?**

1. **Verificar integridad**: El archivo no está corrupto
2. **Verificar autenticidad**: El archivo no ha sido modificado
3. **Build reproducible**: Same code → Same checksum

**Ejemplo de uso**:
```bash
# El CI genera CHECKSUMS.txt automáticamente en cada release
# (ver .github/workflows/build.yml, step "Generate CHECKSUMS.txt")

# Usuario (verificar)
wget https://github.com/davalillo/mql-language-server/releases/latest/download/mql-lsp-server-linux-x64
wget https://github.com/davalillo/mql-language-server/releases/latest/download/CHECKSUMS.txt
sha256sum -c CHECKSUMS.txt

# Output si es válido:
# mql-lsp-server-linux-x64: OK
```

## 📊 Comparación de Métodos

| Método | Tamaño | Dependencias | Instalación | Updates | Mejor para |
|--------|--------|--------------|-------------|---------|------------|
| Extensión VS Code | ~120KB (VSIX) | Descarga el binario solo | Install from VSIX | Con cada release | Usuarios finales de VS Code |
| Standalone Binaries | 71-72MB | Ninguna | Manual | Manual | Otros editores / scripts |
| NuGet Tool | 2-5MB | .NET 10 SDK | `dotnet tool install` | `dotnet tool update` | Desarrolladores |
| GitHub Packages | 2-5MB | .NET 10 SDK | `dotnet tool install` | `dotnet tool update` | Open source |
| NuGet.org | 2-5MB | .NET 10 SDK | `dotnet tool install` | `dotnet tool update` | Público (Trusted Publishing) |

## 🎯 Recomendación por Audiencia

### Para Usuarios Finales (Traders, Quants)
→ **Extensión VS Code** (instalar el VSIX desde GitHub Releases); sin VS Code, usar **Standalone Binaries**

### Para Desarrolladores .NET
→ **GitHub Packages** (más rápido, sin revisión manual)

### Para Distribución Masiva
→ **NuGet.org** (más discoverable, pero requiere revisión)

### Para Empresas/Privado
→ **Distribución Local** (control total)

## 🚀 GitHub Releases (Recomendado)

**¿Por qué GitHub Releases?**

1. **Versionado**: Tags semánticos (v2.0.0, v2.1.0)
2. **Changelog**: Release notes automáticas
3. **Assets**: Binarios, checksums, packages
4. **Downloads**: Analytics de descarga
5. **CI/CD**: Automatizado con GitHub Actions

**Proceso AUTOMÁTICO (Recomendado)**:
```bash
# 1. Tag y push
git tag v2.0.0
git push origin v2.0.0

# 2. GitHub Actions automáticamente:
# - Build para las 4 plataformas (Linux, Windows, macOS Intel, macOS Apple Silicon)
# - Ejecutar tests unitarios
# - Verificar que el tag coincide con la versión del .csproj
# - Crear release en GitHub
# - Upload binarios standalone
# - Upload NuGet package
# - Generar y upload CHECKSUMS.txt (SHA256)

# 3. Resultado (~15-20 min):
# https://github.com/davalillo/mql-language-server/releases/tag/v2.0.0
# → mql-language-server-<tag>.vsix (extensión VS Code)
# → mql-lsp-server-linux-x64 (~71MB)
# → mql-lsp-server-osx-x64 (~71MB)
# → mql-lsp-server-osx-arm64 (~71MB)
# → mql-lsp-server-win-x64.exe (~72MB)
# → mql-language-server.<versión>.nupkg (~2MB)
# → CHECKSUMS.txt
```

**Proceso MANUAL (alternativo, ya no necesario)**:
- Crear release en https://github.com/davalillo/mql-language-server/releases
- Subir binarios manualmente
- Generar checksums
- ⚠️ **Recomendado usar proceso automático**

## ✅ Checklist de Release

Para cada release (v2.0.0, v2.1.0, etc.):

### Proceso AUTOMÁTICO (GitHub Actions):
- [ ] Tests pasan: `dotnet test` (en main branch)
- [ ] Git tag creado: `git tag v2.0.0`
- [ ] Git tag push: `git push origin v2.0.0`
- [ ] ✅ **TODO LO DEMÁS ES AUTOMÁTICO:**
  - ✅ Build para las 4 plataformas
  - ✅ Ejecutar tests unitarios
  - ✅ Verificación tag ↔ versión del .csproj
  - ✅ Crear GitHub Release
  - ✅ Upload binarios standalone
  - ✅ Upload NuGet package
  - ✅ Generar CHECKSUMS.txt (SHA256)

### Proceso MANUAL (alternativo, no recomendado):
- [ ] Tests pasan: `dotnet test`
- [ ] Binarios compilados: `build.sh` / `build.ps1`
- [ ] Checksums generados: `sha256sum * > CHECKSUMS.txt`
- [ ] NuGet package creado: `dotnet pack ...`
- [ ] GitHub Release creado manualmente
- [ ] Binarios subidos manualmente
- [ ] Checksums subidos manualmente
- [ ] Release notes escritas
- [ ] README.md actualizado

## 📚 Recursos Adicionales

- [NuGet Tool Documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools)
- [GitHub Packages](https://docs.github.com/en/packages)
- [Semantic Versioning](https://semver.org/)
- [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github)
- [LSP Specification](https://microsoft.github.io/language-server-protocol/)

## 📋 Metadatos del paquete NuGet

El paquete cumple las prácticas recomendadas de autoría de NuGet
(https://learn.microsoft.com/nuget/create-packages/package-authoring-best-practices):
`Copyright`, `PackageProjectUrl`, `PackageReadmeFile` (README.md embebido),
`PackageReleaseNotes` (enlace a CHANGELOG.md), `PackageIcon` (packaging/icon.png),
`PackageTags` ampliados, `RepositoryType` y expresión de licencia SPDX (`MIT`).
Definidos en `src/MqlLanguageServer.Server.csproj`.

---

**Versión**: v2.0.1
**Autor**: MQL Language Server Team
