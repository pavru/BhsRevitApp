# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Статус репозитория

Каталог **пустой** — кода, решения и git-репозитория ещё нет. Это greenfield-проект:
набор плагинов (add-ins) для Autodesk Revit **плюс** собственный framework для них.

Пока каркас не заложен, разделы «Команды» и «Архитектура» ниже описывают *целевую*
модель, унаследованную от соседних решений автора. Актуализируйте этот файл по мере
появления реального кода — не оставляйте расхождений между документом и деревом проекта.

## Экосистема на диске

Проект живёт внутри `E:\Development\Revit\` и опирается на соседние каталоги. Прежде чем
изобретать решение, посмотрите, как задача уже решена рядом:

| Путь | Роль |
|---|---|
| `..\BimHouse.Revit.Sdk` | **Актуальный** кастомный MSBuild SDK для Revit. Исходники + `readme.md` с полным описанием свойств |
| `..\BimHouseApp` | Ближайший предшественник: слоистая структура `source/`, `.slnx`, CPM. Собран на *старом* подходе `Directory.Build.props`/`.targets` |
| `..\BHS` | Большое legacy-решение (`BHS.sln`): Revit-side/Win-side разделение, gRPC-сервисы, тесты |
| `E:\Development\NuGetPackages` | Локальный NuGet-фид (источник `Local` в глобальном `NuGet.Config`), куда `BimHouse.Revit.Sdk` пушит пакет таргетом `PushToLocal` |
| `..\Revit SDK 20xx`, `..\autodesk.revit.api` | Официальные Revit SDK и сборки API по версиям |

## Сборочная модель

Использовать **`BimHouse.Revit.Sdk`**, а не ручные `Directory.Build.props`-хаки из
`BimHouseApp`/`BHS`. SDK подключается в заголовке проекта и берётся из локального фида:

```xml
<Project Sdk="BimHouse.Revit.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0-revit2025;net8.0-revit2026;net10.0-revit2027</TargetFrameworks>
  </PropertyGroup>
</Project>
```

Что SDK делает за вас (детали — в `..\BimHouse.Revit.Sdk\readme.md`, он поддерживается в актуальном состоянии):

- **Кастомные TFM** `net8.0-revit2025`, `net8.0-revit2026`, `net10.0-revit2027`. **`net48-revit2024` пока НЕ поддержан** — см. «Матрица версий». Несоответствие
  версии .NET и Revit — ошибка сборки, а не тихая деградация.
- **Константы препроцессора** `REVIT`, `REVIT2026`, `REVIT2025_OR_GREATER` и т.д. генерируются
  автоматически задачей `GenerateRevitDefineConstants`. Условная компиляция под версии Revit
  делается через них.
- **Пакеты Revit API** (`Nice3point.Revit.Api.*`) подключаются неявно по свойствам
  `UseRevitApi` (по умолчанию `true`), `UseRevitApiUi`, `UseRevitAdWindows`, `UseRevitApiIfc`,
  `UseRevitUiFramework`, `UseRevitAddInUtility`, `UserRevitTUnit`. Версии выравниваются по TFM —
  **не** прописывайте их вручную.
- **WPF/WinForms/WinUI** работают через `UseWPF`/`UseWindowsForms`/`UseWinUI` без `NETSDK1136`:
  SDK временно маскирует платформу как `windows` на этапах валидации.
- **Манифест `.addin`** генерируется задачей `GenerateRevitAddIn` из item-группы `RevitAddIn`
  (`FullClassName`, `AddInId`, `VendorId`, для 2025+ — `AllowLoadIntoExistingSession`, для 2026+ —
  `UnifyInAddInManager`, `UseRevitContext`, `ContextName`).
- **Публикация и деплой**: `PublishRevitAddIn=true` включает упаковку. Наличие `RevitVersion`
  переключает режим: version-specific пакет (с `.addin`) против common-пакета
  (`RevitVendorId`/`RevitPackageName`, без `.addin`). `RevitDeploy` = `Local` (`%AppData%`)
  или `System` (`%ProgramData%`/`%ProgramFiles%`).

### Обязательные глобальные настройки

- `global.json` — пин .NET SDK. Для TFM `net10.0-revit2027` нужна версия 10.x
  (на машине установлена 10.0.400); см. образец в `..\BimHouseApp\global.json`.
- **Только x64**: `<Platform>x64</Platform>`, `<PlatformTarget>x64</PlatformTarget>`,
  `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`. Revit других разрядностей не бывает.
- **Central Package Management**: `Directory.Packages.props` с
  `ManagePackageVersionsCentrally=true` и `CentralPackageVersionOverrideEnabled=false` —
  версии пакетов правятся только централизованно.
- Формат решения — **`.slnx`** (как в `BimHouseApp`/`BHS`), с виртуальными папками по слоям.
- `Configurations` = `Debug;Release`, `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=true`.

## Правила проекта

- **Язык кода — английский.** Имена файлов, типов, членов, переменных, веток и коммитов — только на английском.
  Русский допустим в пользовательских строках (через ресурсы) и в документации для владельца проекта.
- **Стек** — .NET + C#. Решение по UI принято:
  - **Revit-side (в процессе Revit) — WPF, пакет `WPF-UI`.** Единая линия от Revit 2024 до последней версии.
    Сопутствующие пакеты берутся из набора BimHouseApp: `WPF-UI.DependencyInjection`, `WPF-UI.Tray`,
    `CommunityToolkit.Mvvm`, `SharpVectors.Wpf`, `Lepo.i18n.Wpf`, `Microsoft.Xaml.Behaviors.Wpf`.
  - **Win-side (нативные вспомогательные приложения, отдельный процесс) — `net10.0-windows` + WinUI 3.**
    Не мультитаргетится по Revit и не привязан к диапазону поддержки Revit.
  - WinUI 3 **не применяется** в Revit-side сборках, пока в поддержке остаётся Revit 2024 — см. ограничение ниже.
- **Диапазон поддержки Revit — с 2024 до последней версии**, с добавлением новых по мере выхода.
  Отказ от старой версии — отдельное осознанное решение, а не побочный эффект правки TFM.

### Матрица версий Revit → .NET

Соответствие фиксировано Autodesk, свободного произведения двух осей не существует.
Проверено по `ref/`-папкам пакетов `Nice3point.Revit.Api.RevitAPI` в кэше NuGet:

| Revit | Рантайм | TFM проекта | Статус в BimHouse.Revit.Sdk |
|---|---|---|---|
| 2024 | .NET Framework 4.8 | `net48-revit2024` | **не поддержан** |
| 2025 | .NET 8 | `net8.0-revit2025` | поддержан |
| 2026 | .NET 8 | `net8.0-revit2026` | поддержан |
| 2027 | .NET 10 | `net10.0-revit2027` | поддержан |

Реальная вторая ось мультитаргетинга — **сторона процесса**, а не версия .NET:
Revit-side сборки собираются по Revit-TFM, а общий код и UI — по чистой оси `net48;net8.0;net10.0`.

> **Предусловие для 2024.** `SupportedTargetFrameworks` в
> `..\BimHouse.Revit.Sdk\Sdk\props\Before.Microsoft.NET.Sdk.props` закрыт версиями 2025–2027.
> Механика для .NET Framework уже отлажена в `..\BHS\Directory.Build.props` (подмена
> `TargetFrameworkProfile`, `FrameworkPathOverride` через `ToolLocationHelper`, `AssetTargetFallback`) —
> её нужно перенести в SDK, а не изобретать заново.

> **Ограничение WinUI 3.** Windows App SDK требует .NET 6+, а Revit 2024 — это `net48`.
> Общий UI-слой, обязанный собираться под 2024, на WinUI 3 не соберётся. Рабочий прототип
> WinUI-окна внутри Revit есть в `..\RevitWinUiTest` (ручной P/Invoke для владения окном и модальности,
> `DispatcherFrame` для цикла сообщений, ~10 свойств csproj для отключения MSIX/PRI/RID-графа).
> Отсюда и принятое разделение: Revit-side — WPF, Win-side — WinUI 3 на `net10.0-windows`.
> Прототип полезен как справка по интеропу окон, если Win-side приложению понадобится
> показываться поверх окна Revit.

## Справочники Revit API

Локальные CHM (проверены, все на месте):

| Версия | Путь |
|---|---|
| 2024 | `E:\Development\Revit\Revit SDK 2024\RevitAPI.chm` |
| 2024.2 | `E:\Development\Revit\Revit SDK 2024.2\RevitAPI.chm` |
| 2025 | `E:\Development\Revit\Revit SDK 2025\RevitAPI.chm` |
| 2025.1 | `E:\Development\Revit\Revit SDK 2025.1\RevitAPI.chm` |
| 2026 | `E:\Development\Revit\Revit SDK 2026\RevitAPI.chm` |
| 2026.3 | `E:\Development\Revit\Revit SDK 2026.3\RevitAPI.chm` |
| 2027.2 | `E:\Development\Revit\Revit 2027.2 SDK\RevitAPI.chm` (каталог назван иначе — без «SDK» в середине) |

Revit API Developers Guide (2027):
https://help.autodesk.com/view/RVT/2027/ENU/?guid=Revit_API_Revit_API_Developers_Guide_html

## Команды

```powershell
# восстановление и сборка всего решения
dotnet restore
dotnet build -c Release

# сборка под одну версию Revit (мультитаргет-проект)
dotnet build -c Debug -f net8.0-revit2026

# сборка + установка add-in локально (свойства проекта, а не CLI-режим)
#   PublishRevitAddIn=true, RevitDeploy=Local  →  %AppData%\Autodesk\Revit\Addins\<version>\
dotnet build -c Debug -f net8.0-revit2026 -p:RevitDeploy=Local

# диагностика MSBuild при проблемах с кастомными TFM
dotnet build -bl            # msbuild.binlog, открывать в MSBuild Structured Log Viewer
```

Тесты (когда появятся; в экосистеме используется TUnit через `Nice3point.TUnit.Revit`,
включается свойством `UserRevitTUnit`):

```powershell
dotnet test
dotnet test --filter "FullyQualifiedName~ИмяТеста"
dotnet test path\to\Project.Tests.csproj -f net8.0-revit2026
```

## Архитектурные соглашения

### Разделение Revit-side / Win-side

Ключевое разделение, тянущееся через `BHS` и `BimHouseApp`: сборки, загружаемые *в процесс
Revit* (жёстко привязаны к версии API), и внешние процессы/утилиты, которые версии Revit не
знают. Не смешивайте их в одном проекте — у них разные TFM и разные жизненные циклы.
Общение между ними в `BHS` организовано через gRPC.

### Слоистость framework'а

Проверенная в `BimHouseApp` раскладка `source/`, которую стоит воспроизвести:

- `Revit/Revit.Abstractions` — контракты, свободные от конкретной версии API
- `Revit/Revit.Base`, `Revit/Revit.Common` — базовые реализации и утилиты поверх Revit API
- `Revit/Revit.Intermediate` — прослойка, сглаживающая различия версий Revit
- `Frontend/WPF/*` — `UI.Abstractions`, `UI.Framework`, `UI`, `UI.Translations`
- `Features/<Домен>/` — плагины как feature-модули (реализация, `*.UI`, `*.Playground`)
- корневой host-проект (`*.FullEdition`) — точка входа `IExternalApplication`, собирающая features

Framework — это `Revit.*` + `Frontend/*`; плагины — это `Features/*`. Зависимость идёт только
в одну сторону: features → framework, никогда наоборот.

### Мультиверсионность Revit

Два механизма, применять в этом порядке предпочтения:

1. **Константы препроцессора** от SDK (`#if REVIT2026_OR_GREATER`) — для точечных различий API.
2. **Version-suffixed файлы** (`Foo.2026.cs`) — приём из `BimHouseApp/Directory.Build.targets`,
   где по `RevitVersion` из компиляции исключаются все версии, кроме целевой. Применять,
   когда различия слишком велики для `#if`.

### Локализация

`SatelliteResourceLanguages` в экосистеме = `en-US;en-GB;ru-RU`, `NeutralLanguage=en`.
Строки UI — в ресурсах (`UI.Translations`), не в коде.

## Идентификация вендора

Значения, используемые в соседних решениях для `.addin` и путей установки:
`VendorId` = `BimHouseSoftware`, каталог вендора = `BHS`,
описание = `BimHouse Software by alex@pototskiy.net`. Каждый add-in требует **собственного
уникального `AddInId`** (GUID) — не копируйте GUID из соседних проектов.
