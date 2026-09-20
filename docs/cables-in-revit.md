# Кабели в Revit: что есть, чего нет, и что из этого следует для нас

Исследование 2026-09-21. Источники трёх родов, и они разделены намеренно:
**метаданные** референсных сборок всех четырёх версий (то, что можно проверить компилятором),
**примеры и справка Autodesk** (то, что заявлено), **открытые публикации** (то, что рассказывают).
Где источники расходятся — сказано, чей ответ взят и почему.

---

## 1. Устройство кабеля в Revit 2026+

### Продуктовая сторона

`Manage → MEP Settings → Electrical Conductor and Cable Settings`, вынуто из прежних
`Electrical Settings → Wiring`. Четыре вкладки:

| Вкладка | Что в ней |
|---|---|
| Conductor Details | материалы жилы, температурные рейтинги, изоляция |
| Conductor Sizes | имя и **диаметр** каждого сечения |
| Cable Sizes | конфигурация жил: сколько фазных, нулевых, земляных, прочих и какого сечения каждая |
| Cable Types | марка: материал, рейтинг, изоляция, одно/многожильный, и какие Cable Sizes допустимы |

Цепь получает два свойства — тип кабеля и размер; второе фильтруется первым.

**Про вкладку Conductor Sizes Autodesk пишет дословно:**

> «This data is not used by Revit in any way. However, since the tabulated data was user definable,
> we left it in place for potential external dependencies or future development»

**И про смысл работы целиком:**

> «This opens the door for users to create their own logic, automation, and other means of tailoring
> the software to their needs using Dynamo or the Revit API. It also allows 3rd parties with regional
> expertise to provide add-in capabilities»

Источник: Autodesk AEC Tech Drop, «New Conductor Capabilities in Revit 2026», 23.06.2025.

**Вывод.** Autodesk построила структуру данных и **сознательно не построила логику**. Автоподбор
сечения убран: проектировщик назначает кабель руками. Мы не конкурируем с ревитовским расчётом и не
рискуем разойтись с ним в числах — расчёта нет.

### Четыре ограничения старой модели, которые Autodesk называет сама

1. сечения предполагались по американскому AWG;
2. размер назначался автоматически, без гибкости;
3. **работало только для силовых цепей** — «non-power circuits could not be assigned a wire type
   (e.g., Cat-6 for ethernet cable connection)»;
4. каждый проводник считался одножильным проводом — «didn't support multi-core cables».

Третий пункт закрывает наш давний вопрос про слаботочку: до 2026 назначить тип провода цепи СКС или
пожарной сигнализации было нельзя в принципе.

---

## 2. Что измерено по метаданным

### Наличие типов по версиям

| Тип `Autodesk.Revit.DB.Electrical.*` | 2024 | 2025 | 2026 | 2027 | база |
|---|---|---|---|---|---|
| `CableType` | — | — | есть | есть | **`ElementType`** |
| `CableSize` | — | — | есть | есть | **`System.Object`** |
| `ConductorSize`, `ConductorMaterial`, `InsulationMaterial`, `TemperatureRating` | — | — | есть | есть | `Object` |
| `CoreType` | — | — | есть | есть | `enum` |
| `WireMaterialType`, `WireSize`, `InsulationType`, `TemperatureRatingType`, `CorrectionFactor`, `GroundConductorSize` и все их `*Set`/`*Iterator` | есть | есть | есть | **удалены** | — |
| `WireType`, `Wire`, `ElectricalSetting`, `WireConduitType`, `VoltageType` | есть | есть | есть | есть | — |

`BuiltInCategory.OST_Cable` = **−2001119**, есть в 2026 и 2027, нет в 2024 и 2025.
Полей в перечислении: 1197/1198 (2024), 1206/1207 (2025), 1212/1213 (2026), 1222/1223 (2027) —
разница в единицу между двумя замерами от того, читались референсная сборка пакета или установленная.

Новые категории 2026 против 2025 — семь: `OST_Cable`, `OST_PanelSchedules`, `OST_Subdivision`,
`OST_ViewPosition`, `OST_MEPSystemZoneColorFill`, `OST_RebarCrankType`,
`OST_ElectricalInternalCircuits_Obsolete`.

### Что умеет цепь

```
2025: Int32 GroundConductorsNumber {get} | Int32 NeutralConductorsNumber {get;set}
      Int32 HotConductorsNumber {get}    | String WireSizeString {get}
      WireType WireType {get;set}

2026: ElementId CableSize {get;set}      | ElementId CableType {get;set}
      ElementId HotConductorSize {get}   | NeutralConductorSize {get}
      ElementId GroundConductorSize {get}| OtherConductorSize {get}
      Int32 HotConductorsNumber {get}    | NeutralConductorsNumber {get}
      Int32 GroundConductorsNumber {get} | OtherConductorsNumber {get}
      String WireSizeString {get}        | WireType WireType {get;set}
```

**Цепь сама называет сечения своих жил** — напрямую, не через кабель. Записываются только
`CableSize` и `CableType`; сечения выводятся из назначенного размера.

**А вот полная картина по четырём версиям, и 2026 в ней — единственное окно, где есть оба набора:**

```
2024 : WireType, WireSizeString, VoltageDrop
2025 : WireType, WireSizeString, VoltageDrop
2026 : WireType, WireSizeString, VoltageDrop, HotConductorSize, CableSize, CableType
2027 : HotConductorSize, CableSize, CableType
```

**В 2027 цепь не называет свой провод вовсе.** `WireType`, `WireSizeString` и `VoltageDrop` у неё
удалены. Значит единственный естественный путь «от цепи к её проводу» обрывается, и опирать аналог
на `WireType` нельзя: механизм понадобился бы ради 2024/2025, а компилироваться ему пришлось бы и на
2027, где от провода ничего не осталось.

Сечение цепи читается так, и граница ровно по `REVIT2026_OR_GREATER` — **один `#if`, две ветки**:

| версии | откуда | что это |
|---|---|---|
| 2024, 2025 | `ElectricalSystem.WireSizeString` | строка, результат автоподбора |
| 2026, 2027 | `HotConductorSize` → `ConductorSize.Diameter` | число во внутренних единицах |

**Расхождение со справкой.** Страница «API Changes 2026» пишет, что новые свойства цепи «provide
**read and write** access». Метаданные: сечения только на чтение. Четвёртый случай правила
«справочный XML называет члена, а решает компилятор».

### Переезд `WireType` в 2026

| член | 2024 / 2025 | 2026 / 2027 |
|---|---|---|
| `WireMaterial` | `WireMaterialType` | `ElementId` |
| `Insulation` | `InsulationType` | `ElementId` |
| `TemperatureRating` | `TemperatureRatingType` | `ElementId` |
| `MaxSize` | `WireSize` | `String` |

Справка 2026 называет, чьи это идентификаторы: *«The id is not a **conductor material** id»*,
*«a **conductor insulation material** id»*, *«a **conductor temperature rating** id»*.
**Провод и кабель делят один справочник проводников.**

### Что потерял `ElectricalSetting` в 2027

Удалены `WireMaterialTypes`, `AddWireMaterialType`, `RemoveWireMaterialType` и **`AddWireType`**.
Статического `Create` у `WireType` нет. **Создать тип провода через API в 2027 нечем**, кроме
`ElementType.Duplicate()` существующего.

---

## 3. Рабочие идиомы — из примера Autodesk

`ElectricalConductors`, единственный кабельный пример во всём SDK. Есть в 2026, 2026.3 и 2027.2,
файлы побайтно идентичны; в 2024 и 2025 его нет.

```csharp
ConductorSize conductorSize12 = ConductorSize.Create(doc);
conductorSize12.Name = "12";
conductorSize12.Diameter = 0.0067341666666666678;   // = 2,0525 мм, то есть AWG 12

CableSize cableSize = CableSize.Create(doc);
cableSize.NumberOfHotConductors = 1;
cableSize.HotConductorSize = conductorSizeId;

CableType cableType = CableType.Create(doc);
cableType.SetCableSizeUsable(cableSizeId, true);

// Порядок жёсткий, и пример предупреждает о нём единственным комментарием во всём файле:
circuit.CableType = targetCableTypeId;
doc.Regenerate();                    // regeneration is necessary after setting CableType
circuit.CableSize = targetCableSizeId;
```

**Два разных идиома поиска, и разница показательна:**

```csharp
CableSize cableSize = CableSize.GetCableSize(doc, cableSizeId);    // НЕ doc.GetElement
CableType cableType = doc.GetElement(cableTypeId) as CableType;     // а здесь GetElement
```

**И удаление — обоих как обычных элементов по id:**

```csharp
doc.Delete(cableSizeId);
doc.Delete(conductorSizeId);
```

**Вывод, и он сильный:** раз `doc.Delete(cableSizeId)` работает, за идентификатором `CableSize`
стоит настоящий элемент документа. Обёртка не `Element`, а адресуемое ею — похоже, да. Подтверждать
замером `document.GetElement(cableSize.Id)`.

`ConductorSize.Diameter` **читается** — то есть сечение доступно числом, а не только именем.

---

## 4. Куда можно положить наши данные

| Носитель | Element? | общий параметр | Extensible Storage | спецификация |
|---|---|---|---|---|
| `CableType` | **да** (`ElementType`) | зависит от категории — **не измерено** | применим | неизвестно |
| `CableSize` | **нет** (`System.Object`) | **никогда** | **никогда** | нет |
| цепь (`ElectricalSystem`) | да | **да, уже делаем** | применим | да |

У `CableSize` из пользовательских полей — только встроенный `Comments`, одна строка, и `UniqueId`
у него нет вовсе. Адресовать извне можно лишь по `Id` или по `Name`.

**И то и другое ненадёжно.** При апгрейде 2025 → 2026: «Unique Cable Sizes are defined based on the
circuits in the upgraded model» — набор есть **слепок конкретной модели**, а не справочник. Имена
машинные, и Autodesk сама предлагает их менять: «You could, if desired, modify to use a different
convention».

**Переноса между моделями, похоже, нет.** Transfer Project Standards про кабели не расшифровывает;
Design Master (вендор конкурирующего плагина, сторона заинтересованная, но утверждение проверяемое):
«There is no import/export function yet, so any custom setup must be manually recreated or embedded
in templates».

**И довод против `OST_Cable` как категории кабельного типа.** `OST_Cable` = −2001119 лежит в
диапазоне `-2001xxx`, то есть среди **элементов модели**, тогда как `OST_ElectricalCircuit` и
справочные категории провода живут в `-2008xxx`. При этом `OST_WireMaterials`, `OST_WireInsulations`
и `OST_WireTemperatureRatings` **уцелели в 2027**, хотя классы за ними удалены. То есть `OST_Cable`
похоже заведена не под кабельный тип, а подо что-то размещаемое в модели. Догадка, не факт, но она
сдвигает вероятность.

**Про категорию `CableType` не написано нигде** — ни Autodesk, ни форумы, ни базы документации, ни
The Building Coder. Отрицательный результат поиска, названный прямо. Косвенный признак против: про
`WireType` Autodesk пишет, что он переехал в `Project Browser > Families > Wires > Wire Types`
и настройки оставлены ради спецификаций и фильтров видов; **про `CableType` такой фразы нет**.

---

## 5. Прецедент MagiCAD — самый серьёзный сторонний вендор

> «Wire types contain all the cable data in MagiCAD, and cable types in circuits reference the wire
> types in the project. When cable types are used in any MagiCAD functions, the software looks to the
> corresponding wire type for the cable data.»
>
> «A new Sync button in the Wire Type Management tool cross-checks cable type and wire type lists and
> adds missing items to them.»

**MagiCAD не стала класть свои данные в новые кабельные сущности.** Она оставила их на `WireType` —
обычном `ElementType`, который держит параметры, виден в браузере и попадает в спецификации, — а
связь с ревитовским Cable Type делает **по имени**, синхронизируя два списка отдельной командой.

Это цена, которую заплатил вендор с собственной электрической подсистемой. Сходится с догадкой, что
на `CableType` общий параметр штатно не вешается.

---

## 6. Удаления 2027 не объявлены

Раздел «Obsolete API removal» в «What's New in Revit API 2027» перечисляет `MassSurfaceData`,
члены `Mechanical.Zone`, легаси-методы арматуры, `ViewportPositioning` — **ни одного электрического
класса**. Страница `WireMaterialType` существует в документации 2026 и даёт 404 в 2027.

Замены восстанавливаются только из предупреждений 2026:

| удалён в 2027 | чем заменять |
|---|---|
| `WireSize` | `ConductorSize` |
| `GroundConductorSize` | `ConductorSize` |
| `InsulationType` | `InsulationMaterial` |
| `TemperatureRatingType` | `TemperatureRating` |
| `WireMaterialType` | `ConductorMaterial` |
| `CorrectionFactor` | **ничего** — «There is no replacement because Revit no longer supports this» |

`CorrectionFactor` — тот самый поправочный коэффициент, на котором держался автоподбор сечения.

**И расчёт кабеля убран целиком, а не переписан.** `ConductorSize` **потерял ampacity**: у
предшественника `WireSize` было свойство `Ampacity` с документированной единицей «ампер», у новой
модели его нет нигде — ни на `ConductorSize`, ни на `CableSize`, ни на `CableType`. На 2027
допустимый ток недоступен ни по новому пути, ни по старому. Вместе с удалённым у цепи `VoltageDrop`
и отсутствием замены у `CorrectionFactor` это складывается в одно: **Autodesk вынула из Revit расчёт
кабеля и оставила только описание.**

**Нас это не задевает:** гpеп по `source/` и `build/` не находит ни одного из удалённых типов.

---

## 7. Что из этого следует для запаса

**Запас не требует объекта кабеля.**

Цепь сама называет сечение своих жил на всех четырёх версиях — по-разному, но называет: на
2024/2025 строкой `WireSizeString`, на 2026/2027 через `HotConductorSize` и `ConductorSize.Diameter`.
Граница ровно по `REVIT2026_OR_GREATER`, то есть один `#if` и две ветки. Брать сечение надо **у
цепи, а не у кабеля**.

Отсюда разрез работы:

1. **Составной запас на проектных настройках** — процент на трассу плюс три константы (щит,
   терминал, коробка) плюс константа на коммутацию в кабель-канале. Работает на всех четырёх
   версиях, объекта кабеля не требует, машина подсчёта у нас уже есть целиком.
2. **Зависимость констант от сечения** — таблица «сечение → запас» в тех же настройках, сечение
   читается у цепи: на 2026/2027 из `HotConductorSize`, на 2024/2025 из `WireSizeString`.
3. **Собственный каталог кабелей** — только если понадобится хранить на кабеле больше, чем запас.

**Параметры на `CableType` не заводить**, пока не измерена категория. MagiCAD этого не сделала,
Autodesk ничего не обещала, а выпущенный в чужую модель GUID назад не берётся.

---

## 8. Что измерить живым Revit — один читающий случай

По убыванию влияния на решение:

1. **`document.GetElement(cableSize.Id)`** — элемент или `null`, и если элемент, то какого класса и
   какой категории. Пример Autodesk удаляет его через `doc.Delete`, значит ответ скорее «элемент».
2. **`CableType.Category`** — это `OST_Cable`? И **`Category.GetCategory(doc, OST_Cable)?.AllowsBoundParameters`**.
3. **Проходит ли привязка к `OST_Cable`** нашим продакшн-путём, и виден ли параметр в спецификации.
4. **Что стоит в `ElectricalSystem.CableType`** у цепей модели, мигрированной с 2025 — пусто или
   Revit подставил.
5. **`WireSizeString` на 2024/2025 и на 2026** — что в нём стоит у цепей модели, и пусто ли оно
   на 2026, когда автоподбора уже нет. От этого зависит, годится ли он источником сечения на нижнем
   крае диапазона.
6. Есть ли у `CableType` собственные встроенные параметры.

Ловушка, которую надо знать: наш `Install` **молча пропускает** категорию, которой в документе нет.
Отказ был бы тихим, если бы не громкая проверка `Missing` в фазе применения.

---

## 9. Попутные находки в нашем коде

- **`Cabling:LengthExtend` читается без префикса `Model:`** — единственный кабельный ключ в
  пользовательской цепочке, тогда как `Model:Cabling:Connection`, `Model:Cabling:BoxRadiusMm`,
  `Model:Cabling:ExistingBoxesOnly` и семейство индикатора проектные. Два проектировщика одного
  проекта получат из одной модели разные длины.
- Комментарий в `CablingParameters` говорит «Thirteen», параметров четырнадцать.
- Описание `BHS_Cbl_ДлинаЗапас` ссылается на `Cabling:LengthExtend` и станет неверным при смене
  модели запаса. Описание уезжает в файл общих параметров только при первом объявлении — у тех, кто
  файл уже получил, останется старая формулировка.

---

## 10. Правило, которое стоит дописать в CLAUDE.md

У нас записано: «справочный XML называет члена, а решает компилятор» — про членов, которых справка
обещает, а их нет. **Обратная половина того же правила там не записана, а она такая же:** справка
молчит о существующих. В `RevitAPI.xml` 2026 задокументировано **10 полей `BuiltInCategory` из
1213**; `OST_Cable` не упомянут ни разу.

То есть справочный XML не годится для вопроса «есть ли такой член» **ни в одну сторону**.
Отвечают метаданные.
