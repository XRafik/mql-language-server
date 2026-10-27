# MQL Language Server

[English](README.md) | [Español](README.es.md) | Русский

[![CI](https://github.com/davalillo/mql-language-server/actions/workflows/ci.yml/badge.svg)](https://github.com/davalillo/mql-language-server/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/davalillo/mql-language-server)](https://github.com/davalillo/mql-language-server/releases/latest)
[![License](https://img.shields.io/github/license/davalillo/mql-language-server)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![LSP](https://img.shields.io/badge/LSP-3.17-green.svg)](https://microsoft.github.io/language-server-protocol/)

Реализация Language Server Protocol (LSP) для MQL4 и MQL5 (MetaTrader 4/5). Обеспечивает функциональность уровня IDE: автодополнение, переход к определению, справку при наведении курсора и навигацию по символам.

| | |
|---|---|
| **Редакторы** | Любой LSP-клиент — VS Code, Neovim, Emacs, Vim, Sublime Text |
| **Знание встроенных функций** | 3 670 документированных символов MQL4/MQL5 (функции, константы, enum, structs, макросы) с происхождением каждой записи из официальных справочников |
| **Диагностика, уважающая реальный код** | Ложные `Undeclared symbol` подавляются через замыкание include и индекс workspace; радар миграции MQL4→MQL5 отмечает устаревший API с подсказками замены |
| **Работает везде** | Самодостаточные бинарные файлы для Windows/Linux/macOS (x64 + ARM64) и .NET-инструмент на nuget.org |

## Возможности

- Извлечение символов (функции, переменные, классы, structs, interfaces, enums, includes) в MQL4 и MQL5
- Переход к определению / объявлению / определению типа / реализации
- Иерархия типов (LSP 3.17): навигация по супертипам/подтипам для классов, interfaces и enums
- Поиск всех ссылок и переименование с учётом локальной области видимости документа (с учётом затенения)
- Символы документа, символы workspace, подсветка в документе, диапазоны сворачивания, диапазоны выделения
- Автодополнение (встроенные элементы + локальные символы, с автоматическим импортом `#include` для разрешённых символов) и hover с реальными сигнатурами и официальными описаниями встроенных элементов
- База данных встроенных функций на основе документации: 3 670 функций, констант, членов enum, structs и макросов MQL4/MQL5 с происхождением каждой записи (886 MQL4 / 2 784 MQL5) — аудит против официальных справочников в рамках проходов по dataset, чьи результаты закреплены регрессионными gates
- Справка по сигнатурам
- Диагностика через pull mode (`textDocument/diagnostic`): семантические правила с окнами кодов MQL4 1000 / MQL5 5000, радар миграции API, доступной только в MQL4, для файлов MQL5, константы enum стандартной библиотеки MQL с учётом диалекта, а также подавление межфайловых неразрешённых символов через замыкание include и индекс workspace
- Code Actions (QuickFix с помощью include) и образцы цвета (`documentColor` / `colorPresentation`)
- Форматирование документа и диапазона
- Разбор с учётом препроцессора: раскрытие function-like и object-like макросов, цепочки вложенных include, условное слияние по порядку include и макросы карты событий библиотеки MQL Controls (`ON_EVENT`, `EVENT_MAP_BEGIN`/`END`)
- LRU-кэш повторного использования разбора между циклами didOpen/didClose для больших файлов
- Проектный файл `.mqlignore` для исключения путей (резервные копии, установленные терминалы) из сканирования workspace
- Сигналы готовности при старте: сканирование workspace отправляет токены `$/progress` (`workDoneProgress`) клиентам, объявившим соответствующую capability, а запросы, пакетированные вместе с handshake `initialize`, получают ответ вместо молчаливого отбрасывания (#160)
- Кроссплатформенные бинарные файлы: Linux x64/ARM64, macOS Intel/Apple Silicon, Windows x64/ARM64

## Поддержка MQL5

В этом выпуске добавлена полноценная поддержка MQL5 без изменения поведения MQL4:

- Файлы `.mq5` и `.mqh` распознаются автоматически.
- Разбирается специфичный для MQL5 синтаксис: классы, structs, interfaces, наследование, шаблоны, `enum class`, `nullptr`, `union`, `final`, `pack(n)`, параметры по ссылке, `using`, `#resource`, списки инициализации и `new`/`delete` в куче.
- 2 784 документированных встроенных символа MQL5 (функции, константы, enum, structs, макросы) включены в автодополнение и hover, аудит против официального справочника MQL5.
- Диагностика файлов MQL5 использует отдельный диапазон кодов `MQL5xxx`, чтобы фильтры CI могли разделять проблемы MQL4 и MQL5.

## Исключения сканирования workspace (`.mqlignore`)

Стартовое сканирование workspace индексирует каждый файл `.mq4`/`.mq5`/`.mqh` в каждой папке workspace, пропуская встроенные каталоги (`.git`, `bin`, `obj`, …) и скрытые каталоги. Проект может исключить собственные пути, поместив файл **`.mqlignore`** в корень папки workspace (issue #157).

Синтаксис (подмножество gitignore, побеждает последнее совпадение):

- `#` комментарии и пустые строки игнорируются.
- `backup-utf8/` — завершающий `/` делает шаблон только-для-каталогов (он никогда не совпадает с одноимённым файлом).
- `/src/gen.mq4` — начальный `/` привязывает шаблон к корню папки; без него шаблон совпадает на любой глубине.
- `*.mq4`, `?`, `**` — подстановочные знаки; `**` как целый сегмент совпадает с нулём или более сегментов.
- `!keep.mq4` — отрицание заново включает путь, совпавший с более ранним шаблоном (побеждает последнее совпадение).
- Поддерживаются CRLF и BOM UTF-8; некорректные строки пропускаются; сравнение регистронезависимо.

Семантика:

- Совпавший каталог пропускается целиком — сканирование не тратит ресурсов на игнорируемые поддеревья, а счётчик в уведомлении о старте отражает усечённое число кандидатов.
- `.mqlignore` может только ДОБАВЛЯТЬ исключения: отрицание не может воскресить встроенные исключения (`.git`, скрытые каталоги) или правила перечисления (скрытые/системные атрибуты, reparse points).
- Игнорирование касается только **индекса workspace**: открытие игнорируемого файла (`didOpen`) по-прежнему даёт ему полный анализ на уровне документа.
- Игнорируемый `.mqh` не индексируется; исходники, которые его включают, могут сообщать о неразрешённых символах его объявлений ("игнорируется" означает "вне индекса").
- Файл читается один раз за сканирование при запуске — наблюдателя нет, поэтому после изменения файла требуется перезапуск сервера.

## Корневые каталоги системных include (`systemIncludeRoots`)

Include в угловых скобках (`#include <Trade\Trade.mqh>`) — это include системной библиотеки: они разрешаются относительно каталогов стандартной библиотеки терминала MetaTrader, которые находятся вне всех папок workspace. Сервер принимает опцию инициализации **`systemIncludeRoots`** — массив абсолютных путей к каталогам, обычно каталоги `MQL5/Include` / `MQL4/Include` терминала:

```json
{
  "initializationOptions": {
    "systemIncludeRoots": [
      "/путь/к/MetaTrader/MQL5/Include"
    ]
  }
}
```

Семантика:

- Записи в угловых скобках разрешаются относительно каждого настроенного корневого каталога (по порядку) с нормализацией разделителей пути (принимаются `\` и `/`) и проверкой существования без учёта регистра в стиле Windows; обход `..`, выходящий за пределы корня, отклоняется.

- Найденный заголовочный файл разбирается парсером диалекта и лениво индексируется в глобальный индекс символов при `didOpen` (с дедупликацией — уже проиндексированный заголовок не разбирается повторно).

- После индексации классы и типы Стандартной библиотеки (`CTrade`, `CArrayObj`, `MqlTradeRequest`, …) работают с definition, hover, references, completion и межфайловым подавлением диагностик — без ложных срабатываний `Undeclared symbol`.

- Fail open: без настроенных корней, а также если заголовок не удаётся разрешить или разобрать, поведение в точности прежнее — без ошибок и лишних диагностик.

- Include в кавычках (`#include "file.mqh"`) не затронуты: они по-прежнему разрешаются относительно включающего файла.

### Файл конфигурации проекта (`.mql-lsp.json`)

Корневые каталоги можно также объявлять на уровне проекта, чтобы репозиторий нёс собственную конфигурацию и каждая сессия редактора подхватывала её без настроек клиента:

```json
{
  "systemIncludeRoots": [
    "../Include",
    "C:/Program Files/MetaTrader 5/MQL5/Include"
  ]
}
```

- Файл читается из корня каждой папки workspace во время `initialize`; как и `.mqlignore`, наблюдателя нет, поэтому после изменения файла требуется перезапуск сервера.
- Относительные записи разрешаются относительно каталога, содержащего `.mql-lsp.json`, что делает файл переносимым между машинами; абсолютные используются как есть.
- Приоритет: непустая опция инициализации `systemIncludeRoots` имеет преимущество, и файлы проекта не читаются; иначе применяется объединение по папкам workspace. Если нет ни одного источника, сервер остаётся в режиме fail-open.
- Некорректный или нечитаемый файл игнорируется с записью предупреждения в журнал — сессия никогда не прерывается.
- Внутри объявленного корня межкаталожный переход в кавычках с восхождением к родителю, остающийся внутри этого же корня, включается в замыкание include так же, как угловая форма: `#include "..\Arrays\ArrayLong.mqh"`, записанный в `Trade\Trade.mqh`, даёт свои символы (issue #186). Переход, выходящий за все объявленные корни, или попадающий в *другой* объявленный корень, по-прежнему отвергается.

### Неразрешённые системные include

Если include в угловых скобках не удаётся разрешить, сервер выдаёт одно предупреждение `Warning` на директиву (`1004` для MQL4, `5004` для MQL5, источник `mql-lsp`), указывая запись и оба способа решения. Ничего не подавляется: диагностики `Undeclared symbol` (`1070`/`5070`) не меняются, поэтому предупреждение объясняет причину, не скрывая следствие.

Сопровождающие могут проверить разрешение на реальном терминале с помощью opt-in скрипта `./tools/verify-system-includes.sh /путь/к/MQL5/Include`, который выдаёт JSON-свидетельство и завершается с ненулевым кодом, если символы Стандартной библиотеки остаются неразрешёнными. В CI он никогда не запускается.

## Технические решения

В этом разделе описаны ключевые технические решения, принятые в ходе разработки, чтобы упростить освоение проекта новыми разработчиками.

### 1. Стратегия парсера: ANTLR 4.13.1

**Выбрано**: ANTLR 4.13.1 с Antlr4BuildTasks 12.14.0

**Рассмотренные альтернативы**:
- Regex (отклонено — недостаточно для разбора сложного кода MQL)
- Sprache (отклонено — комбинатор парсеров, менее надёжен для сложных грамматик)
- Superpower (отклонено — более новый проект, меньше документации)
- Irony (отклонено — не поддерживается)

**Основная причина**:
Первоначальное решение использовать regex было пересмотрено после того, как проявились ограничения при разборе реального кода MQL. ANTLR предоставляет:
- Формальную и поддерживаемую грамматику
- Точное абстрактное синтаксическое дерево (AST)
- Лучшую поддержку сценариев использования LSP
- Устойчивость к сложному синтаксису

**Полученный урок**: для LSP, которому нужно разбирать сложный код, возможностей regex недостаточно. ANTLR предлагает идеальный баланс между надёжностью и простотой использования.

### 2. Инструментарий ANTLR: Antlr4BuildTasks 12.14.0

**Выбрано**: Antlr4BuildTasks 12.14.0 (автоматически загружает JRE)

**Альтернатива**: ручная установка ANTLR + Java JDK

**Основная причина**:
Избежать ручных зависимостей в среде разработки. Antlr4BuildTasks:
- Автоматически загружает JRE и JAR-файл инструмента ANTLR
- Не требует предварительной установки Java
- Работает кроссплатформенно (Windows, Linux, macOS)
- Выполняется во время сборки MSBuild/dotnet

**Конфигурация в .csproj**:
```xml
<PackageReference Include="Antlr4BuildTasks" Version="12.14.0" PrivateAssets="All" />
<Antlr4 Include="Mql4\Grammar\Mql4Grammar.g4">
  <AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>
</Antlr4>
```

**Полученный урок**: Antlr4BuildTasks — оптимальное решение для связки .NET + ANTLR без ручной настройки Java. Версия 12.14.0 стабильна и надёжна.

### 3. Библиотеки LSP: OmniSharp.Extensions

**Выбрано**: OmniSharp.Extensions.LanguageProtocol 0.19.9

**Рассмотренная альтернатива**: Microsoft.LanguageServer.Protocol (не существует)

**Обнаруженная проблема**:
Пакета `Microsoft.LanguageServer.Protocol` нет в NuGet. Было распространённой ошибкой предполагать, что Microsoft поддерживает официальные библиотеки LSP для .NET.

**Выполненная миграция**:
- Изначально: `Microsoft.LanguageServer.Protocol` (не существует)
- Итог: `OmniSharp.Extensions.LanguageProtocol` 0.19.9
- Связанные пакеты: `OmniSharp.Extensions.JsonRpc`, `OmniSharp.Extensions.LanguageServer.Shared`

**Полученный урок**: фактическим стандартом для LSP в .NET является OmniSharp, а не Microsoft. Библиотека активно поддерживается и широко используется.

### 4. Стратегия грамматики: прагматичное упрощение

**Выбрано**: упрощённая, но функциональная грамматика MQL

**Альтернатива**: полная грамматика со всеми возможностями MQL

**Основная причина**:
LSP не нужно разбирать всю семантику языка — достаточно синтаксической структуры, позволяющей:
- Извлекать символы (функции, переменные)
- Находить определения и ссылки
- Предоставлять автодополнение и hover

**Принятый подход**:
```antlr
// Пример: упрощённая, но функциональная грамматика
variableDeclaration
    : dataType IDENTIFIER (ASSIGN expression)? SEMICOLON
    ;
```

vs

```antlr
// Сложная альтернатива: для LSP не нужна
variableDeclaration
    : storageClass? dataType IDENTIFIER (ASSIGN expression)? SEMICOLON
    | storageClass? dataType IDENTIFIER LBRACKET expression? RBRACKET SEMICOLON
    ;
```

**Полученный урок**: эффективному LSP не требуется разбор всего языка. Ключ к успеху — прагматичное упрощение.

### 5. Конфигурация сборки: AntlrOutDir

**Конфигурация**: `<AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>`

**Решённая проблема**:
По умолчанию ANTLR генерирует файлы в `obj/Debug/net10.0/`. Без AntOutDir потребовалось бы вручную копировать их в `src/Parser/Generated/`.

**Полная конфигурация**:
```xml
<Antlr4 Include="Mql4\Grammar\Mql4Grammar.g4">
  <Generator>MSBuild:Compile</Generator>
  <Listener>true</Listener>
  <Visitor>true</Visitor>
  <Package>Mql4Grammar</Package>
  <AntOutDir>$(MSBuildProjectDirectory)\Parser\Generated</AntOutDir>
</Antlr4>
```

**Преимущества**:
- Автоматическая генерация в нужном расположении
- Никакого ручного копирования после сборки
- Файлы видны в системе контроля версий

### 6. Ключевые уроки

#### Префикс K_ для токенов
Исключает конфликты между ключевыми словами и токенами:
```antlr
// ПЛОХО - конфликт с токеном DOUBLE
DOUBLE : 'double';

// ХОРОШО - префикс для ключевых слов
K_DOUBLE : 'double';
dataType : K_DOUBLE | IDENTIFIER;
```

#### Методы контекста в верхнем регистре
ANTLR генерирует методы с точными именами токенов:
```csharp
// ПЛОХО - ошибка компиляции
var nameToken = context.identifier();

// ХОРОШО - работает
var nameToken = context.IDENTIFIER();
```

#### Видимость комментариев
Для `-> skip` требуется специальный канал:
```antlr
// ПЛОХО - ошибка компиляции ANTLR
COMMENT : '/*' .*? '*/' -> skip;

// ХОРОШО - работает
COMMENT : '/*' .*? '*/' -> channel(HIDDEN);
// Или отдельные правила:
COMMENT_BLOCK : '/*' .*? '*/' -> skip;
```

#### Простота против сложности
Простой парсер, который работает, лучше сложного, который не работает.

### Пересборка ANTLR-парсера

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

Дополнительные шаги не требуются — Antlr4BuildTasks выполняет всё автоматически.

### Текущее состояние

- ✅ Два ANTLR-парсера (MQL4 + MQL5) с учётом препроцессора (раскрытие макросов, вложенные include, условная компиляция)
- ✅ Полная поверхность LSP: более 20 зарегистрированных обработчиков (символы, определения, ссылки, переименование, автодополнение, hover, справка по сигнатурам, диагностика через pull mode, code actions, цвет, форматирование, сворачивание, диапазон выделения, символы workspace, moniker, inlay hints)
- ✅ Индекс символов: сканирование workspace + индекс вхождений + LRU-кэш повторного использования разбора
- ✅ Набор тестов: 1 778 зелёных тестов (`dotnet test`, исключая категории Performance/FpMeasurement)
- ✅ Dataset встроенных функций: 3 670 записей на основе документации (886 MQL4 / 2 784 MQL5) с происхождением каждой записи, наращиваемых и проверяемых в рамках проходов по dataset
- ✅ Бинарные файлы: Linux x64/ARM64, macOS x64/ARM64, Windows x64/ARM64 (самодостаточные, в один файл)
- ✅ CI/CD: GitHub Actions — CI для PR, релизный конвейер со smoke-тестами на нативном ARM, шлюз уязвимостей зависимостей
- ✅ Опубликовано: GitHub Releases и nuget.org (`mql-language-server`, стабильная 2.7.0) через Trusted Publishing

## Безопасность

Предупреждения об уязвимостях NuGet, описанные в предыдущих версиях этого раздела, были **устранены 10.09.2026**: уязвимые транзитивные зависимости удалены путём обновления зависимостей, аудит сборки чист. Исторический анализ см. в [docs/references/SECURITY.md](docs/references/SECURITY.md).

## Тестирование

Запуск модульных тестов:
```bash
dotnet test
```

Покрытие тестами: 1 778 тестов (актуальное состояние набора см. в [CHANGELOG](CHANGELOG.md)), охватывающих парсер, обработчики LSP и граничные случаи.

### Покрытие кода с помощью Coverlet

В проекте используется **Coverlet** для измерения покрытия кода. Coverlet — кроссплатформенная библиотека измерения покрытия кода для .NET, предоставляющая подробные отчёты о покрытии.

#### Установка Coverlet

Установите Coverlet как глобальный инструмент .NET:
```bash
dotnet tool install --global coverlet.console
```

Либо используйте его напрямую через dotnet без установки:
```bash
dotnet tool install --tool-path . coverlet.console
```

#### Запуск тестов с измерением покрытия

**Вариант 1: Coverlet как глобальный инструмент**
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

**Вариант 2: Использование Coverlet.MSBuild (ссылка на пакет)**
Добавьте в тестовый проект (.csproj):
```xml
<PackageReference Include="coverlet.collector" Version="6.0.0">
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Затем выполните:
```bash
dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
```

**Вариант 3: Простой локальный отчёт**
```bash
# Build the project
dotnet build -c Release

# Run tests with coverage
coverlet ./tests/bin/Release/net10.0/MqlLanguageServer.Tests.dll --target "dotnet" --targetargs "test ./tests/MqlLanguageServer.Tests.csproj --configuration Release --no-build"
```

#### Отчёты о покрытии

Coverlet поддерживает несколько форматов вывода:

1. **Консоль** (по умолчанию): выводит сводку в терминал
2. **JSON**: структурированные данные для интеграции с CI/CD
   ```bash
   --format json --output ./coverage/coverage.json
   ```
3. **OpenCover**: отраслевой стандарт
   ```bash
   --format opencover --output ./coverage/coverage.xml
   ```
4. **Cobertura**: ещё один распространённый формат
   ```bash
   --format cobertura --output ./coverage/cobertura.xml
   ```
5. **LCov**: для интеграции с системами CI
   ```bash
   --format lcov --output ./coverage/lcov.info
   ```

#### Пороговые значения покрытия

Настройте минимальные пороговые значения покрытия, чтобы обеспечить качество кода:
```bash
# Fail if total line coverage is below 80%
--threshold 80 --threshold-type line --threshold-stat total

# Fail if any assembly falls below 70%
--threshold 70 --threshold-type line --threshold-stat assembly

# Fail if any class falls below 60%
--threshold 60 --threshold-type line --threshold-stat class
```

Комбинированные пороговые значения:
```bash
--threshold 80 --threshold-type line --threshold-stat total
--threshold 90 --threshold-type method --threshold-stat total
```

#### Интеграция с CI/CD

Добавьте в свой workflow GitHub Actions:
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

#### Просмотр отчётов о покрытии

1. **Терминал**: мгновенная обратная связь после запуска тестов
2. **Visual Studio**: откройте `coverage.json` или `coverage.xml` в Visual Studio
3. **Веб**: используйте такие инструменты, как [ReportGenerator](https://github.com/danielpalme/ReportGenerator), для генерации HTML-отчётов:
   ```bash
   dotnet tool install --global dotnet-reportgenerator-globaltool
   reportgenerator -reports:./coverage/coverage.xml -targetdir:./coverage/html -reporttypes:Html
   open ./coverage/html/index.html
   ```

#### Лучшие практики измерения покрытия

- **Цель**: стремитесь к покрытию строк 80% и выше для критических путей выполнения
- **Качество важнее количества**: содержательные тесты лучше высокого покрытия тривиального кода
- **Интеграционные тесты**: покрывайте взаимодействие между компонентами
- **Граничные случаи**: тестируйте обработку ошибок и граничные условия
- **Исключения**: исключайте сгенерированный код и тестовые утилиты:
  ```bash
  --exclude-by-file "**/Generated/**" \
  --exclude-by-attribute "*GeneratedCodeAttribute*"
  ```

## CI/CD

Автоматическая сборка и выпуск через GitHub Actions:

### Триггеры workflow:

**Ветка main** (быстрый CI):
- ✅ Сборка + модульные тесты при каждом push в `main` и в каждом PR
- ✅ Только Ubuntu (мультиплатформенная проверка выполняется при выпуске релиза)
- ⚡ Без генерации артефактов (быстрее)
- ⚡ Тесты производительности и FpMeasurement исключены (чувствительны ко времени; запускаются локально)

**Теги v\*** (релизы):
- ✅ Мультиплатформенные сборки
- ✅ Автоматическое тестирование
- ✅ Выпуск бинарных файлов (GitHub Releases)
- ✅ Упаковка NuGet
- ✅ Контрольные суммы (SHA256)
- ✅ Тесты проверки бинарных файлов

### Процесс выпуска:

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

Подробнее см. в [.github/workflows/build.yml](.github/workflows/build.yml).

## Установка

### Автономные бинарные файлы (рекомендуется)

Скачайте готовый бинарный файл со страницы [GitHub Releases](https://github.com/davalillo/mql-language-server/releases):

- **Linux**: x64 и ARM64 (`mql-lsp-server-linux-*`, самодостаточный)
- **macOS**: Intel и Apple Silicon (`mql-lsp-server-osx-*`, самодостаточный)
- **Windows**: x64 и ARM64 (`mql-lsp-server-win-*.exe`, самодостаточный)

Сделайте файл исполняемым (Linux/macOS):
```bash
chmod +x mql-lsp-server
```

### Через инструмент .NET (nuget.org)

Пакет опубликован на nuget.org (стабильный канал); релиз-кандидаты устанавливаются с `--prerelease`.

```bash
dotnet tool install -g mql-language-server
```

Либо установите из локальной сборки:
```bash
./pack.ps1
dotnet tool install -g mql-language-server --add-source ./nupkg
```

### Из исходного кода

**Предварительные требования**: .NET 10 SDK

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

### Результаты сборки

После сборки бинарные файлы находятся здесь:
- `src/bin/linux-x64/mql-lsp-server`
- `src/bin/osx-x64/mql-lsp-server`
- `src/bin/win-x64/mql-lsp-server.exe`
- `src/bin/linux-arm64/mql-lsp-server`
- `src/bin/osx-arm64/mql-lsp-server`
- `src/bin/win-arm64/mql-lsp-server.exe`

## Использование

### Командная строка
```bash
mql-lsp-server --stdio
```

### VSCode

VS Code и другие редакторы настраиваются через универсальное расширение LSP-клиента — см. [Интеграцию с редакторами](docs/guides/EDITOR_INTEGRATION.md) для настройки конкретных редакторов (VS Code, Neovim, Emacs, Vim, Sublime Text).

## Лицензия

MIT — сторонние компоненты и их лицензии перечислены в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).