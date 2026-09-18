# UDP Game Protocol — практическая работа №1

Учебный проект на **C# / .NET 10** с двумя консольными приложениями:

- `UdpGame.Server` — отдельный авторитетный Dedicated Server;
- `UdpGame.Client` — периодически отправляет `MOVEMENT` и `SHOOT` и получает `STATE_UPDATE`.

Обмен выполняется через стандартный `System.Net.Sockets.UdpClient`. Протокол бинарный, без JSON. Заголовок и payload сериализуются явно, а многобайтовые числа передаются в network byte order (big-endian).

## Структура проекта

```text
.
├── UdpGame.sln
├── src/
│   ├── UdpGame.Protocol/
│   ├── UdpGame.Server/
│   └── UdpGame.Client/
├── tests/
│   └── UdpGame.Protocol.Tests/
└── docs/
    ├── Architecture_Design.md
    └── Protocol_Specification.md
```

`UdpGame.Protocol` — отдельная библиотека, которую используют клиент, сервер и тесты.

## Требования

- .NET SDK 10.0 или новее;
- Windows, Linux или macOS.

Сторонние сетевые библиотеки не нужны.

## Сборка и тесты

В корне проекта:

```bash
dotnet build UdpGame.sln -c Release
dotnet test UdpGame.sln -c Release --no-build
```

## Запуск

Сначала сервер:

```bash
dotnet run --project src/UdpGame.Server -- --port 27015 --max-packets 6 --log server.log
```

Затем во втором терминале клиент:

```bash
dotnet run --project src/UdpGame.Client -- --host 127.0.0.1 --port 27015 --count 6 --interval-ms 500 --timeout-ms 2000
```

Клиент поочерёдно отправит три `MOVEMENT` и три `SHOOT`. Ожидаемый итог:

```text
Completed: 6/6 responses received
```

В выводе клиента будут ответы вида:

```text
Received STATE_UPDATE seq=1 ack=MOVEMENT status=ACCEPTED ...
Received STATE_UPDATE seq=2 ack=SHOOT status=ACCEPTED ...
```

Сервер пишет все корректно распознанные команды в консоль и `server.log`.

## Параметры клиента

```text
--host        адрес сервера, по умолчанию 127.0.0.1
--port        UDP-порт, по умолчанию 27015
--count       число команд, по умолчанию 6
--interval-ms пауза между командами, по умолчанию 500 мс
--timeout-ms  тайм-аут ответа, по умолчанию 2000 мс
```

## Параметры сервера

```text
--port        UDP-порт, по умолчанию 27015
--max-packets завершить сервер после N обработанных команд
--log         файл журнала, по умолчанию server.log
```

Если `--max-packets` не задан, сервер работает до ручной остановки.

## Документация

- [Архитектура и схема взаимодействия](docs/Architecture_Design.md)
- [Спецификация бинарного UDP-протокола](docs/Protocol_Specification.md)

