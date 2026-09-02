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

## Взаимодействие Revit-side и Win-side

### Состав

- **Revit-side** — в процессе Revit: команды, апдейтеры, внешние события, лента, точка входа
  `IExternalApplication`. Мультитаргетится по Revit-TFM.
- **Win-side** — **фоновое приложение** (не служба Windows) для задач, трудно- или невыполнимых
  внутри Revit. Запуск возможен **с обеих сторон**: из Revit-side или из системы. Расширяется
  функциональными модулями. В перспективе — обратный сценарий: Win-side сам поднимает Revit
  с нужной моделью (например, модуль MCP по запросу агента).
- **GUI Win-side** — отдельное приложение на `net10.0-windows` + WinUI 3.

Служба Windows рассмотрена и отклонена: Revit в любом случае требует сессии пользователя
(рабочий стол, лицензия на вошедший аккаунт), поэтому единственное преимущество службы —
работа без залогиненного пользователя — в сценарии «Win-side поднимает Revit» бесполезно.
Плюс LocalSystem не видит сетевые ресурсы пользователя, где лежат модели. Если понадобится
работа совсем без пользователя — правильная форма будет «служба + пользовательский агент»,
и Revit поднимает именно агент.

### Симметричность и обнаружение

Инициатором может быть любая сторона, поэтому **роли симметричны: обеим сторонам нужен
серверный конец**. Revit-side обязан принимать входящие подключения, включая Revit 2024 (`net48`).
Обе стороны обязаны уметь обнаруживать запущенные экземпляры компаньонов.

### Транспорт — named pipes

`System.IO.Pipes` на всех версиях. Отвергнут TCP на loopback.

- Работает от `net48` до `net10` без единого пакета.
- Настоящая аутентификация вызывающего — DACL плюс PID и олицетворение, вместо разбора
  `context.Peer` регуляркой, как в BHS (тот фильтр пропускает любой локальный процесс
  любого пользователя).
- Проходит границу session 0, не занимает порт, не требует TLS ради локального обмена.
- **Обнаружение почти бесплатно**: пространство `\\.\pipe\` перечисляется как каталог,
  поэтому поиск компаньонов сводится к соглашению об именах. С TCP пришлось бы вести
  реестр портов в файле.

### RPC поверх канала — GrpcDotNetNamedPipes

Сохраняется модель программирования gRPC и контракты `.proto`; заменяется только кадрирование
(вместо HTTP/2) и транспорт (вместо TCP). Меняются две строки: `NamedPipeServer` вместо `Server`,
`NamedPipeChannel` вместо `Channel`.

> **Это не совместимый gRPC.** Протокол на проводе свой, сторонний gRPC-клиент не подключится.
> Обе стороны обязаны быть на этой библиотеке. Точная формулировка: «модель программирования
> gRPC и контракты `.proto`, транспорт приватный».

Почему не обычный gRPC: **сервера gRPC на .NET Framework не существует** (хостинг требует
ASP.NET Core), а клиент на `net48` работает только по TLS, на Windows 11+/Server 2019+,
без client и duplex стриминга. Пока Revit 2024 в поддержке, gRPC поверх HTTP/2 неприменим.
Настоящий gRPC поверх именованных каналов (Kestrel `ListenNamedPipe`, .NET 8+) открывается
только при отказе от 2024.

Запасной вариант, если библиотека будет заброшена: свой тонкий RPC на `System.IO.Pipes` +
MessagePack. Риск ограничен — 76 КБ под Apache 2.0, копирайт Google LLC, форкается без драмы.

### Конфликт сборок с самим Revit

**Revit 2025, 2026 и 2027 возят собственные `Grpc.*` и `Google.Protobuf`**
(`Grpc.Core.Api` 2.59.0.0, `Google.Protobuf` 3.23.1.0), причём это живая подсистема
(`ATFRevitGrpcInterface`, `grpcProtos`), а не забытые файлы. У Revit 2024 их нет.

Измерено на живом процессе Revit 2025: **наши копии подменяются копиями Revit** — из четырёх
сборок add-in две загрузились из `C:\Program Files\Autodesk\Revit 2025`. Побеждает
загрузившийся первым, а Revit грузится раньше add-in.

Окно проблемы — **только Revit 2025**: на 2024 конфликта нет, на 2026+ спасает изоляция
(`UseRevitContext=False` + `ContextName` в манифесте).

Проверено анализом метаданных: `GrpcDotNetNamedPipes` 3.1.0 обращается к 55 типам и 79 членам
ревитовских сборок, расхождений — ноль. Поверхности `Grpc.Core.Api` 2.59 и 2.67 **идентичны**
(499 членов, ноль добавленных) — это намеренно замороженная контрактная сборка, отсюда и
`AssemblyVersion 2.0.0.0` на всю ветку 2.x.

**Пиннинг версий не делаем.** Понижать нечего, а прямая ссылка на 2.59 даст `NU1605`,
подавление которого потом скроет настоящее понижение.

Вместо этого:

- **Правило: только `syntax = "proto3"`. Editions не использовать.** Код для editions ссылается
  на `Reflection.Edition` и `Features`, которых в ревитовской 3.23.1 нет — на Revit 2025 это
  `TypeLoadException` при первом вызове.
- **Проверка в сборке**: прогонять выходные сборки Revit-side против набора, который реально
  возит каждая поддерживаемая версия Revit, и падать при расхождениях. Закрывает весь класс
  проблемы, а не два пакета: Revit расходится с нами и по `System.Memory` (4.0.1.1 против 4.0.1.2),
  `System.Runtime.CompilerServices.Unsafe` (4.0.4.1 против 6.0.0.0), `System.Collections.Immutable`
  (1.2.5.0), причём `Revit.exe.config` для них binding-redirect'ов не содержит.
- **Логировать на старте** фактически загруженные сборки с путём и версией.

### Метрика для Revit-side

На `net48` Revit 2024 грузит все add-in в **один AppDomain** — изоляции нет. Поэтому правильная
метрика веса зависимости — **не мегабайты, а число полифиллов BCL**, вбрасываемых в общий
AppDomain. Ориентиры: named pipes — 4, grpc-dotnet — 7, StreamJsonRpc — ~14 (29 сборок).

### Framework-минимум контракта

Обмен конфигурацией — **framework-level, а не фича**, поэтому транспорт нужен с первого дня,
независимо от прикладных сервисов. Минимум: `GetConfiguration`, `Ask`, `Shutdown` — три
унарных метода.

Из BHS переносится модель, но не реализация: Revit-side объявляет публикуемые типы опций
(`PublishConfiguration<T>()`), Win-side потребляет их как обычный `IConfigurationSource`
наравне с `appsettings.json`. Что чинить при переносе:

- полезная нагрузка — **JSON-строка внутри protobuf**, контракт нетипизирован, и гарантии
  эволюции схемы к содержимому не применяются;
- конфигурация вычисляется **один раз в конструкторе**, без уведомлений об изменении и
  перезагрузки, хотя `IConfiguration` умеет и то и другое;
- используется `Newtonsoft.Json`, копию которого Revit тоже возит;
- схема «дочерний процесс спрашивает конфигурацию у родителя» не работает при запуске
  Win-side из системы — родителя нет.

> **Статус.** Выбор сделан по статическому анализу метаданных. **Живой обмен по каналу внутри
> Revit ещё не проверялся** — этому мешает сломанный `dotnet restore` (см. «Известные проблемы»).

## Известные проблемы окружения

- **`dotnet restore` молча падает** на любом пакете, которого нет в глобальном кэше:
  `RestoreTask returned false but did not log an error`. Воспроизводится вне песочницы,
  с чистым `nuget.config` и с переопределённым `globalPackagesFolder`. Проекты без
  `PackageReference` собираются нормально — проблема строго в скачивании. Блокирует
  добавление любой новой зависимости и живую проверку транспорта. Причина не найдена.

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
