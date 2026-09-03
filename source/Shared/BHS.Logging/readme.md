# BHS.Logging

Логирование фреймворка. Половина, которая едет внутрь Revit, — поэтому **без единой ссылки на
пакеты**.

## Почему свой движок

Не «not invented here», а замер. В процессе Revit уже загружена каждая ходовая библиотека
логирования, причём в несовместимых версиях одновременно:

| Revit | `log4net` | `Microsoft.Extensions.Logging` | `Serilog` | `NLog` |
|---|---|---|---|---|
| 2024 | 2.0.12, 3.0.3 | 2.1.1, 2.2.0 | 2.0.0 | — |
| 2025 | 2.0.12, 3.0.3 | 6.0, 7.0, 8.0 | 2.0.0, 4.2.0 | — |
| 2026 | 2.0.12 | 6.0 | 2.0.0 | — |
| 2027 | 2.0.12 | 3.1.6, 6.0, 8.0 | 2.0.0, 4.2.0 | 6.0 |

Часть лежит **в каталоге самой Autodesk**, то есть грузится до любого add-in. Serilog при этом
опаснее прочих: он морозит `AssemblyVersion` на `2.0.0.0` по всей ветке 2.x, и в Revit 2024 лежат
два файла разных сборок (2.3.0 и 2.10.0) с одинаковой идентичностью — случай `Newtonsoft.Json`,
где компилятор молчит, а `MissingMethodException` приезжает на первом обращении.

Запрет проверяется сборкой: `RVTREF005` в `build/RefCheck`, список `denied` в `watchlist.json`.

## Как пользоваться

```csharp
private readonly ILog _log = Log.For<MyThing>();   // или инжекцией ILog

_log.Info("registered as {0} after {1:F1} s", instanceId, elapsed.TotalSeconds);
_log.Error(error, "pipe {0} closed while streaming", pipeName);
```

`ILog` — два члена, `IsEnabled` и `Write`; всё удобное в extension-методах `LogWriting`. Так
устроено, чтобы правило «форматируем только после проверки уровня» нельзя было нарушить в
реализации. Перегрузки на 0–3 аргумента вместо `params`: массив аллоцируется до входа в метод,
то есть выключенный `Trace` иначе стоил бы аллокации всем add-in в общем AppDomain.

Инжектируйте `ILog`. Статический `Log` — для двух мест: первой строки `OnStartup`, где контейнера
ещё нет, и статических утилит.

## Поднятие в host'е

```csharp
LogRouter.PrimaryThreadId = Environment.CurrentManagedThreadId;   // первой строкой OnStartup
LogSetup.Start(LogRouter.Default, "revit2026", settings, console: false, facts: facts);
LogRouter.Default.Add(new JournalLogSink(application.ControlledApplication));
```

`LogRouter.Default` открывает файл сам при первой записи, ещё до настроек, — иначе самые
интересные отказы (`OnStartup` не дошёл до конца) не попали бы никуда.

**`Default` аддитивен и идемпотентен.** На Revit 2024 все add-in в одном AppDomain, и два наших
издания делят один `Default`. `Add` добавляет, `Apply` пересчитывает уровни, ни один ничего не
убирает; выключенный приёмник — это `Minimum = None`, а не удаление.

## Настройки, секция `Log`

| Ключ | Умолчание | На живую |
|---|---|---|
| `Log:Level` | `Information` | да |
| `Log:Levels:<категория>` | — | да |
| `Log:Directory` | `%LocalAppData%\BHS\Logs` | нет |
| `Log:File:Enabled` / `MaxSize` | `true` / 8 МиБ | да |
| `Log:File:Retain` / `RetainDays` | 200 / 14 | нет, уборка на старте |
| `Log:Journal:Enabled` / `Level` | `true` / `Warning` | да |
| `Log:Trace:Enabled` | `auto` (= есть отладчик) | да |
| `Log:Console:Enabled` | `true`, Win-side | да |

Опечатка в имени уровня даёт предупреждение и умолчание, а не отказ — единственное названное
исключение из правила «нечитаемое значение отвергается», принятое владельцем.

## Что не делается в v1

Scopes, message templates, мост `ILoggerProvider`, приёмник поверх канала, фоновый писатель с
очередью, Windows Event Log. Разбор с причинами и условиями возврата — в проектном документе.
