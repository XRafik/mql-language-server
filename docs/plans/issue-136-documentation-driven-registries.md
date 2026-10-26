# Plan: Issue #136 — Registros documentation-driven + capa de migración (mql5Mapping)

> Estado: **IMPLEMENTADO** (Fases 0–4; Fase 5 release pendiente). Ver "Estado de implementación" al final.
> Issue: https://github.com/davalillo/mql-language-server/issues/136
> Base: v2.5.1-rc.1 · 20 fp-1070 residuales en corpus real · 14/26 nombres documentados faltantes en MQL4Builtins

## Objetivo

Sustituir los registros hand-curated de builtins (y el registro paralelo `Mql4OnlyApiRegistry`) por un **único dataset dorado derivado de la documentación oficial**, con:

1. Completitud verificable por CI (test de paridad registro ↔ golden list).
2. Procedencia por entrada (URL de documentación).
3. Metadatos de migración MQL4→MQL5 (`apiTier`, `mql5Mapping`) que alimenten la regla 5060 (`Mql4OnlyApiRule`) y futuras herramientas de migración/quick-fixes.

## Estado actual (verificado)

- `src/Mql4/Builtins/Mql4Builtins.cs` (678 líneas) y `src/Mql5/Builtins/Mql5Builtins.cs` (611): diccionarios `Lazy<Dictionary<string,string>>` escritos a mano, con comentarios de parche por incidente (#46, #109, #126). Funciones y constantes/enum mezcladas en `LazyBuiltInVariables`.
- `src/Analysis/Mql4OnlyApiRegistry.cs`: 47 entradas curadas (`Mql4OnlyApiEntry`: Name, Kind, Reason, Replacement, SemanticsChanged). Mismo problema de raíz: sin procedencia, sin garantía de completitud.
- Consumidores a respetar: `UnresolvedSymbolRule` (1070/5070), `Mql4OnlyApiRule` (5060, solo MQL5 docs), `LanguageMisuseRule` (5040), `CodeActionHandler`, adapters de builtins.
- CI (`.github/workflows/ci.yml`) ya corre `dotnet test` → un test de paridad xUnit es build gate sin tocar workflows.
- Precedentes de regresión: `tests/Analysis/Issue109BuiltinRegistryCoverageTests.cs`, `tests/FpMeasurement/ReferenceFalsePositiveMeasurementTests.cs`.

## Decisiones de diseño

| Decisión | Elección | Razón |
|---|---|---|
| Fuente de verdad | JSON versionado en `data/builtins/` (commit, no scrape en CI) | Reproducible, diffable, sin dependencia de red |
| Carga en runtime | `EmbeddedResource` + patrón `Lazy<>` existente | Elimina la duplicación C#↔datos; startup perf intacta |
| Dialect handling | Nombres legacy MQL4 + build-600+ ambos en registro MQL4 (per reference MQL4); MQL5 per reference MQL5 | Decisión del issue #136; `Mql4OnlyApiRule` no cambia de dueño |
| No documentados pero aceptados por el compilador | Allowlist explícita `compilerAcceptedUndocumented` con justificación | El gate de paridad no se rompe por falsos "extra" |
| Migración | Campo `mql5Mapping` en el mismo dataset; regla 5060 pasa a consumir del dataset (se elimina/derive `Mql4OnlyApiRegistry`) | Una sola lista, no tres paralelas que envejecen |

### Esquema de entrada

```json
{
  "name": "TimeToStr",
  "kind": "function",            // function | variable | enum-constant
  "enum": null,                  // p.ej. "ENUM_TIMEFRAMES" si kind=enum-constant
  "signature": "string TimeToStr(datetime value, int mode=TIME_DATE|TIME_MINUTES)",
  "apiTier": "legacy",           // legacy | build600 | shared
  "docUrl": "https://docs.mql4.com/convert/timetostr",
  "mql5Mapping": {               // solo entradas MQL4 con destino MQL5
    "replacement": "TimeToString",
    "kind": "rename",            // rename | semantic | manual | none
    "semanticsChanged": false,   // heredado de Mql4OnlyApiEntry
    "note": "Idéntica conversión, distinto nombre"
  }
}
```

Mapeo al modelo actual de 5060: `kind`→`Mql4OnlyApiKind`, `note`→`Reason`, `replacement`→`Replacement`, `semanticsChanged`→`SemanticsChanged`. Entradas shared con semántica sin cambio (OrderSend, i*, …) quedan **excluidas** de 5060 (REQ-MA-02 se mantiene: solo nombres rechazados por el compilador MQL5 o con semántica cambiada).

## Fases

### Fase 0 — Verificación (½ día)
1. Probe de los 26 nombres del issue contra ambos registros → confirmar 14 faltantes MQL4 + 3 MQL5 (`StringInit`, `EventKillTimer`, `EventSetMillisecondTimer`).
2. Ejecutar `tests/FpMeasurement` para línea base.
3. **Antes de editar**: impacto GitNexus sobre `Mql4Builtins`, `Mql5Builtins`, `Mql4OnlyApiRegistry` y consumidores (`UnresolvedSymbolRule`, `Mql4OnlyApiRule`, `CodeActionHandler`, adapters). Advertencias HIGH/CRITICAL se reportan, nunca se saltan.

### Fase 1 — Golden lists (2–3 días)
4. `data/builtins/mql4.{functions,variables,enum-constants}.json` + `mql5.*`. Entrada según esquema de arriba.
5. Fuentes: `docs.mql4.com` (Checkup, Common, Conversion, Datetime, File, GlobalVariables, Math, Objects, String, Indicators, Timeseries, Trading, Window, EventHandling + Constantes/Enumeraciones + Predefined variables) y `mql5.com/en/docs` equivalente.
6. Script one-off `tools/generate-golden-lists/` (scrape/parse + merge) con README de procedencia y fecha. El JSON resultado se commitea.
7. Cross-check docs.mql4.com (parcialmente deprecated) contra la sección MQL4 de la referencia MQL5; discrepancias se resuelven priorizando lo que acepta el compilador MQL4 build 600+.
8. **Riesgo dimensionable aquí**: si el scrape revela cientos de nombres nuevos, las fases 2–3 crecen; el diff de paridad lo dirá antes de tocar C#.

### Fase 2 — Migración de runtime a datos (1–2 días)
9. `Mql4Builtins`/`Mql5Builtins` cargan de JSON embebido, conservando `Lazy<>`. Firmas desconocidas → fallback neutro.
10. Mapear `kind` correctamente: hoy constantes viven en `LazyBuiltInVariables` — verificar todos los consumidores para no romper semántica 1070/5060.
11. Unificar 5060: `Mql4OnlyApiRule` pasa a construir sus entradas desde el dataset (proyección de `mql5Mapping`); `Mql4OnlyApiRegistry` se elimina o deriva. Mantener tests existentes de `Mql4OnlyApiRuleTests` en verde.
12. Allowlist `compilerAcceptedUndocumented` con justificación por nombre.

### Fase 3 — Gate de paridad en CI (½ día)
13. `tests/Analysis/RegistryGoldenListParityTests.cs`: diff registro ↔ golden list vacío; en fallo imprime exactamente faltantes/extras. Por dialecto (no global, por colisiones de constantes).
14. Sin cambios en `ci.yml` (corre en el job existente).

### Fase 4 — Regresión de corpus (½ día)
15. El residuo de #126 (20 fp: `REASON_*`, `TERMINAL_SCREEN_DPI`, `FILE_BIN/SHARE_*`, `StringInit`, `Event*Timer`, `_LastError`, `_StopFlag`, `_UninitReason`, `_AppliedTo`, …) cae del dataset; añadir pins de regresión estilo `Issue109…Tests`.
16. **Negative control**: identificadores genuinamente no declarados siguen produciendo 1070 (verificado en rc.1).

### Fase 5 — Release (½ día)
17. Bump `2.5.1-rc.2`, changelog, medir fp-1070 en corpus real (objetivo 0), cerrar #136.

## PRs
1. **PR A**: golden lists + script de generación + test de paridad (+ relleno de faltantes que el diff revele).
2. **PR B**: carga de datos en runtime + unificación de 5060 + pins de regresión + negative control.

## Estado de implementación (sesión 2026-10)

- ✅ Fase 0: impacto GitNexus (UNKNOWN→confirmado por texto); consumidores: adapters, `Mql4AntlrParser`, `CompletionHandler`, reglas 1070/5060/5040.
- ✅ Fase 1: `data/builtins/{mql4,mql5}.json` (541/484 entradas) + `tools/generate-golden-lists/{extract_registry,apply_issue136}.py` + READMEs. Seed desde registries HEAD v2.5.1-rc.1 (procedencia por entrada), suplemento #136 con docUrls, 47 admissions REQ-MA-02 migradas a metadata `mql4OnlyApi`+`mql5Mapping` (27 eran nombres que ni siquiera estaban en el registry hand-curated: gap latente corregido).
- ✅ Fase 2: `src/Builtins/BuiltinGoldenData.cs` (loader EmbeddedResource + cache, diccionarios OrdinalIgnoreCase preservados); `Mql4Builtins`/`Mql5Builtins` reescritos (API pública intacta); `Mql4OnlyApiRegistry` es ahora proyección del dataset (tipos `Mql4OnlyApiKind`/`Mql4OnlyApiEntry` intactos, Ordinal preservado); csproj con `LogicalName` determinista.
- ✅ Fase 3: `tests/Analysis/RegistryGoldenListParityTests.cs` (paridad exacta por dialecto, sets + valores).
- ✅ Fase 4: `tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs` (26 pins); negative control existente en verde (`UndeclaredSymbol_Mql4Document_EmitsCode1070WithSymbolData`).
- ✅ Suite completa: 1348/1348. detect-changes: riesgo medio (firmas preservadas).
- 🔲 Fase 5: bump `2.5.1-rc.2`, changelog, medición corpus real, PRs + cierre de #136.
- 🔲 Follow-up: scrape completo de docs para enriquecer docUrls per-name; modo de análisis pre-600 sin `#property strict`.

## Riesgos
- docs.mql4.com desactualizado → cross-check MQL5 reference (mitigado en Fase 1).
- Colisiones de constantes entre dialectos → paridad por dialecto.
- Volumen de nombres nuevos → dimensionado en Fase 1 antes de tocar C#.
- Semántica pre-600 (sin `#property strict`, conversiones implícitas, handlers `init/start/deinit`) → fuera de alcance de #136; el `apiTier: legacy` del dataset deja el gancho para un modo de análisis legacy futuro (follow-up, nuevo issue).
