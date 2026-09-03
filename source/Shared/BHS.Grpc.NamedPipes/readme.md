# BHS.Grpc.NamedPipes

Форк [GrpcDotNetNamedPipes](https://github.com/cyanfish/grpc-dotnet-namedpipes) 3.1.0,
Apache License 2.0, Copyright 2020 Google LLC. Исходники взяты на коммите
`da307b551a9d56b62f5780e91a2d95a699f32430` и **не изменены ни в одном файле** — вся разница
в `BHS.Grpc.NamedPipes.csproj`.

## Зачем форк

Одна причина: `System.Memory` на Revit 2024.

Опубликованная сборка `net462` скомпилирована против `System.Memory` **4.0.2.0**, а
`Grpc.Core.Api` и `Google.Protobuf` — против **4.0.1.1**. Revit 2024 возит собственную
`System.Memory` 4.0.1.1 в каталоге установки, то есть в application base, поэтому ссылка на
4.0.1.1 связывается туда, а ссылка на 4.0.2.0 — на нашу копию рядом с add-in.
`System.Buffers.IBufferWriter<byte>` оказывается двумя разными типами, сигнатура
`SerializationContext.GetBufferWriter()` перестаёт совпадать, и первый же вызов падает:

```
MissingMethodException: Method not found:
  'System.Buffers.IBufferWriter`1<Byte> Grpc.Core.SerializationContext.GetBufferWriter()'
```

Штатное лекарство — `bindingRedirect` — add-in'у недоступно: своего `.config` у него нет,
конфигурацией правит `Revit.exe.config`, а редиректов для `System.Memory` там нет. Ровно этот
редирект SDK генерирует в `.exe.config` консольных приложений, и только поэтому тот же
`BHS.Transport` на том же `net48` зелёный вне Revit. `AppDomain.AssemblyResolve` тоже не
помогает — он вызывается лишь когда связывание провалилось, а здесь оно успешно, просто не туда.

Пересборка из исходников с `System.Memory` **4.5.4** (это и есть сборочная версия 4.0.1.1)
приводит все три ссылки к одной идентичности. Понижением это не является: и `Grpc.Core.Api`
2.67, и `Google.Protobuf` 3.29.3 требуют `System.Memory` не ниже 4.5.3.

## Что изменено против оригинала

| | оригинал | здесь |
|---|---|---|
| `System.Memory` | 4.6.0 (сборка 4.0.2.0) | 4.5.4 (сборка **4.0.1.1**), только на `net48` |
| TFM | `net462;netstandard2.0;net6;net8` | `net48;net8.0-windows;net10.0-windows` — как у `BHS.Transport` |
| имя сборки | `GrpcDotNetNamedPipes` | **`BHS.Grpc.NamedPipes`** |
| подпись | strong name публичным ключом | нет |
| `InternalsVisibleTo` | для тестов апстрима | нет |
| версии пакетов | в файле проекта | через CPM, `Directory.Packages.props` |

Пространство имён осталось `GrpcDotNetNamedPipes` — чтобы исходники совпадали с апстримом
дословно и обновление сводилось к копированию каталога.

**Имя сборки сменено намеренно.** Revit 2024 грузит все add-in в один AppDomain: сборка с именем
`GrpcDotNetNamedPipes` столкнулась бы с любым другим вендором, взявшим готовый пакет, — и
победил бы загрузившийся первым. Наша копия от стандартной отличается, так что подмена была бы
именно тем отказом, ради которого форк и делался.

## Как обновлять

1. `git clone https://github.com/cyanfish/grpc-dotnet-namedpipes` на нужный тег;
2. скопировать `GrpcDotNetNamedPipes/*.cs`, `Internal/` и `Internal/Protocol/transport.proto`
   поверх этого каталога, `public_signing_key.snk` не брать;
3. проверить, не сменилась ли ссылка на `System.Memory` в апстримном `csproj` — если апстрим
   выровнялся с `Grpc.Core.Api`, форк больше не нужен;
4. прогнать `BHS.Transport.Probe` на трёх рантаймах и `BHS.Revit.Probe.Runner` на всех версиях
   Revit.

Правило прежнее: **только `syntax = "proto3"`**. `transport.proto` апстрима ему удовлетворяет.
