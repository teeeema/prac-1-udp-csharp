# UDP Game Protocol — практические работы №1 и №2

## Что это за проект

Консольный проект C# / .NET 10: игровой UDP-протокол с authoritative Dedicated Server и эксперимент по измерению задержки, её изменчивости и потерь. ПР №2 развивает ПР №1, сохраняя обычный игровой режим.

## ПР №1

Клиент чередует MOVEMENT и SHOOT, сервер проверяет команды и возвращает STATE_UPDATE с тем же SequenceNumber. Сервер определяет итоговые координаты и счётчик выстрелов. [Демонстрация со снимками консоли](docs/Демонстрация_работы_протокола.md) сохранена.

## ПР №2

Режим --experiment использует PING/PONG и собирает RTT, SRTT, Jitter, Loss. Шесть профилей по 50 PING позволяют сравнить базовый обмен, delay, jitter и loss. Программный Network Emulator работает на исходящих PING клиента; повторных отправок нет.

## Архитектура

Protocol задаёт wire format и validation, Transport передаёт дейтаграммы и применяет эмулятор, Telemetry хранит inFlight и рассчитывает метрики. Client организует сценарии, Server хранит авторитетное состояние и отвечает на PING.

Путь эксперимента: Client → UdpTransport → UDP → серверный UdpTransport → Server. Обычный клиент использует UdpClient напрямую. Сервер использует UdpTransport в обоих сценариях. [Подробные зависимости и диаграммы](docs/Architecture_Design.md).

## Структура solution

```text
UdpGame.sln
src/
├── UdpGame.Protocol/       # header, payload, serializer, validation
├── UdpGame.Transport/      # UdpTransport и NetworkEmulator
├── UdpGame.Telemetry/      # inFlight, RTT/SRTT/jitter/loss
├── UdpGame.Server/         # состояние игры и PONG
└── UdpGame.Client/         # обычный режим и ExperimentRunner
tests/
├── UdpGame.Protocol.Tests/
└── UdpGame.Telemetry.Tests/
docs/                      # спецификация, архитектура, CSV, графики, фото
```

## Packet types

| Пакет | Направление | Назначение | Полный размер, bytes |
|---|---|---|---:|
| MOVEMENT | Client → Server | Координаты | 19 |
| SHOOT | Client → Server | Выстрел | 8 |
| STATE_UPDATE | Server → Client | Авторитетное состояние | 26 |
| PING | Client → Server | Начало измерения | 15 |
| PONG | Server → Client | Ответ для измерения RTT | 31 |

Header — 7 байт, ProtocolVersion = 1, многобайтовые числа — big-endian. [Wire specification](docs/Protocol_Specification.md).

## Build

Требуется .NET SDK 10.0 и возможность восстановления NuGet-пакетов. Все проекты нацелены на net10.0. Команды выполняются из корня проекта.

```bash
dotnet build UdpGame.sln
```

## Tests

После сборки:

```bash
dotnet test UdpGame.sln --no-build
```

В исходниках 21 тестовый метод: 11 Protocol.Tests и 10 Telemetry.Tests, MSTest 4.0.2. Проверяются wire format, ошибки пакетов, формулы метрик и классификация PONG. При обновлении документации эксперимент не запускался.

## Запуск сервера

В первом терминале, с доступным UDP-портом 27015:

```bash
dotnet run --project src/UdpGame.Server -- --port 27015 --log server.log
```

Без --max-packets сервер работает до остановки. Для короткой демонстрации можно добавить --max-packets 6. Для эксперимента этот лимит использовать не следует: требуется обработать несколько серий. Лог дописывается в server.log.

## Демонстрация ПР №1

Во втором терминале:

```bash
dotnet run --project src/UdpGame.Client -- --host 127.0.0.1 --port 27015 --count 6 --interval-ms 500 --timeout-ms 2000
```

Обычный клиент работает с MOVEMENT / SHOOT / STATE_UPDATE. При успешной демонстрации ожидается Completed: 6/6 responses received. [Описание обмена и photos](docs/Демонстрация_работы_протокола.md).

## Запуск эксперимента ПР №2

При работающем сервере без ограничения пакетов:

```bash
dotnet run --project src/UdpGame.Client -- --host 127.0.0.1 --port 27015 --experiment --csv docs/latency_samples_new.csv
```

Это команда для будущего запуска, а не шаг проверки документации. Новый путь выбран для сохранения исходного CSV: WriteCsv перезаписывает файл назначения. Значение --csv по умолчанию — docs/latency_samples.csv.

Experiment mode работает с PING / PONG / RTT / SRTT / Jitter / Loss: 6 × 50 PING, плановый интервал 200 ms, timeout 1000 ms, seed 20260923. Параметры --count, --interval-ms, --timeout-ms не меняют константы эксперимента. [Профили и методика](docs/Experiment_Config.md).

## Результаты

[Сохранённый CSV](docs/latency_samples.csv) содержит 300 строк: 295 received и 5 timeout. В loss_5 — 2 timeout (4%), в combined — 3 (6%). Остальные четыре серии завершились без timeout. [Аналитический отчёт](docs/Latency_Report.md) содержит независимо пересчитанную таблицу, анализ каждой серии и объяснение ограничений результатов.

Шесть PNG построены через Plotly, экспортированы Kaleido и находятся в `docs/charts/`:

- [Динамика RTT: шесть отдельных графиков](docs/Charts_Analysis.md#2-динамика-rtt-по-измерениям).
- [Средний RTT](docs/charts/chart_mean_rtt.png).
- [Final SRTT](docs/charts/chart_srtt.png).
- [Jitter](docs/charts/chart_jitter.png).
- [Потери пакетов](docs/charts/chart_packet_loss.png).
- [Распределения RTT с увеличением baseline](docs/charts/chart_rtt_distribution.png).

## Документация

- [Architecture Design](docs/Architecture_Design.md): развитие ПР №1 → ПР №2, модули, обмен, inFlight.
- [Protocol Specification](docs/Protocol_Specification.md): header, offsets, payload, validation.
- [Experiment_Config.md](docs/Experiment_Config.md) — конфигурация эксперимента, параметры эмуляции и воспроизводимость.
- [Latency_Report.md](docs/Latency_Report.md) — результаты эксперимента, итоговая статистика и выводы.
- [Charts_Analysis.md](docs/Charts_Analysis.md) — подробный анализ всех визуализаций и пояснения для защиты.
- [Демонстрация ПР №1](docs/Демонстрация_работы_протокола.md): сохранённый игровой сценарий.


- [Демонстрация ПР №1](docs/Демонстрация_работы_ПР2.md): работоспобность ПР2.

