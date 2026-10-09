# UDP Game Protocol — практические работы №1–№3

## Что это за проект

Консольный проект C# / .NET 10: игровой UDP-протокол с authoritative Dedicated Server и эксперимент по измерению задержки, её изменчивости и потерь. ПР №2 развивает ПР №1, сохраняя обычный игровой режим.

## ПР №1

Клиент чередует MOVEMENT и SHOOT, сервер проверяет команды и возвращает STATE_UPDATE с тем же SequenceNumber. Сервер определяет итоговые координаты и счётчик выстрелов. [Демонстрация со снимками консоли](docs/Демонстрация_работы_протокола.md) сохранена.

## ПР №2

Режим --experiment использует PING/PONG и собирает RTT, SRTT, Jitter, Loss. Шесть профилей по 50 PING позволяют сравнить базовый обмен, delay, jitter и loss. Программный Network Emulator работает на исходящих PING клиента; повторных отправок нет.

## ПР №3

SHOOT теперь reliable: RequiresAck=true, отдельный UDP ACK, повтор исходных bytes с тем же sequence, maxAttempts=5 и failed без аварии. Adaptive RTO рассчитывает SRTT/RTTVAR в диапазоне 100–3000 ms. Karn исключает RTT ACK после retransmission; успешные PING/PONG также обучают RTO. Server использует RecentCommandWindow на 1024 команды каждого endpoint: duplicate получает ACK и сохранённый STATE_UPDATE, а игровой эффект не повторяется. MOVEMENT, PING/PONG, ACK и STATE_UPDATE остаются ненадёжными.

Фактически выполнены шесть серий по 50 SHOOT: **300 исходных команд, 299 подтверждены, 59 retransmissions, 1 failed**. Failed-команда была применена сервером один раз, но не получила подтверждения за пять попыток. [Реальный CSV](docs/reliability_samples.csv), [отчёт](docs/Reliability_Protocol.md), [график попыток](docs/graphs/avg_attempts_vs_loss.png), [график adaptive RTO](docs/graphs/rto_jitter_loss_10.png). Seeds: 20261009 на отправку, 20261010 на приём. Эмуляция действует на запросы и ответы, включая ACK; полный эксперимент описан в отчёте.

Wire version=2, header=8 байт. Client и Server обновляются вместе; v1 отклоняется. Ограничения UInt16 rollover, late ACK, отсутствия session ID, bounded dedup window и failed retention описаны в отчёте. Безусловная exactly-once доставка не заявляется.

## Архитектура

Protocol задаёт wire format и validation, Transport передаёт дейтаграммы и применяет эмулятор, Telemetry хранит inFlight и рассчитывает метрики. Reliability хранит состояние доставки и адаптивный timeout без сетевого IO; Client связывает его с Protocol, Transport и Telemetry. Client организует сценарии, Server хранит авторитетное состояние и отвечает на PING.

Путь эксперимента: Client → UdpTransport → UDP → серверный UdpTransport → Server. Обычный клиент использует тот же UdpTransport без эмуляции. Сервер использует UdpTransport в обоих сценариях. [Подробные зависимости и диаграммы](docs/Architecture_Design.md).

## Структура solution

```text
UdpGame.sln
src/
├── UdpGame.Protocol/       # header, payload, serializer, validation
├── UdpGame.Transport/      # UdpTransport и NetworkEmulator
├── UdpGame.Reliability/    # pending/failed, retransmission, adaptive RTO
├── UdpGame.Telemetry/      # inFlight, RTT/SRTT/jitter/loss
├── UdpGame.Server/         # состояние игры и PONG
└── UdpGame.Client/         # обычный режим и ExperimentRunner
tests/
├── UdpGame.Protocol.Tests/
├── UdpGame.Telemetry.Tests/
├── UdpGame.Reliability.Tests/
└── UdpGame.Integration.Tests/
docs/                      # спецификация, архитектура, CSV, графики, фото
```

## Packet types

| Пакет | Направление | Назначение | Полный размер, bytes |
|---|---|---|---:|
| MOVEMENT | Client → Server | Координаты | 20 |
| SHOOT | Client → Server | Выстрел | 9 |
| STATE_UPDATE | Server → Client | Авторитетное состояние | 27 |
| PING | Client → Server | Начало измерения | 16 |
| PONG | Server → Client | Ответ для измерения RTT | 32 |
| ACK | Server → Client для reliable SHOOT | Подтверждение sequence | 10 |

Header — 8 байт, ProtocolVersion = 2, многобайтовые числа — big-endian. [Wire specification](docs/Protocol_Specification.md).

## Build

Требуется .NET SDK 10.0 и возможность восстановления NuGet-пакетов. Все проекты нацелены на net10.0. Команды выполняются из корня проекта.

```bash
dotnet restore
dotnet build UdpGame.sln
```

## Tests

После сборки:

```bash
dotnet test UdpGame.sln --no-build
```

В исходниках **78 тестов**: 21 Protocol, 10 Telemetry, 27 Reliability и 20 Integration; MSTest 4.0.2. Все прежние 58 сохранены. Покрыты реальная потеря первого ACK с повтором SHOOT без второго эффекта, Karn, failed при 100% loss, bounded dedup/rollover, двусторонний эмулятор и непрерывный polling во время delayed ACK. Итоговый прогон: 78 passed, 0 failed. Обычный Client/Server сценарий и полный существующий эксперимент ПР №2 проверены.

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

Обычный клиент работает с MOVEMENT / reliable SHOOT / ACK / STATE_UPDATE. При успешной демонстрации ожидается Completed: 6/6 responses received. [Описание обмена и photos](docs/Демонстрация_работы_протокола.md).

## Запуск эксперимента ПР №2

При работающем сервере без ограничения пакетов:

```bash
dotnet run --project src/UdpGame.Client -- --host 127.0.0.1 --port 27015 --experiment --csv docs/latency_samples_new.csv
```

Это команда для будущего запуска, а не шаг проверки документации. Новый путь выбран для сохранения исходного CSV: WriteCsv перезаписывает файл назначения. Значение --csv по умолчанию — docs/latency_samples.csv.

Experiment mode работает с PING / PONG / RTT / SRTT / Jitter / Loss: 6 × 50 PING, плановый интервал 200 ms, timeout 1000 ms, seed 20260923. Параметры --count, --interval-ms, --timeout-ms не меняют константы эксперимента. [Профили и методика](docs/Experiment_Config.md).

## Запуск эксперимента ПР №3

При работающем сервере без --max-packets:

```bash
dotnet run --project src/UdpGame.Client -- --host 127.0.0.1 --port 27015 --reliability-experiment --csv docs/reliability_samples_new.csv
```

Режим выполняет шесть серий по 50 reliable SHOOT. --csv по умолчанию — docs/reliability_samples.csv; для повторного запуска используйте новый путь, поскольку файл перезаписывается. --count, --interval-ms и --timeout-ms относятся к обычному режиму. ПР №3 использует 50 ms паузу между завершёнными операциями, probe timeout=1000 ms и polling=20 ms; параметры профилей фиксированы в NetworkProfiles.ReliabilityProfiles. --experiment и --reliability-experiment взаимоисключающие.

Независимая проверка сохранённого фактического запуска и построение графиков (Python 3, Base R; дополнительных Python-пакетов не требуется):

```bash
python3 scripts/analyze_reliability.py
Rscript scripts/plot_reliability.R
```

Скрипты читают CSV, не генерируют samples. [Вывод ПР №3](docs/reliability_run.log) и [серверные ACK/duplicate эффекты](docs/reliability_server.log) сохранены для аудита.

## Результаты ПР №2

[Сохранённый CSV](docs/latency_samples.csv) содержит 300 строк: 295 received и 5 timeout. В loss_5 — 2 timeout (4%), в combined — 3 (6%). Остальные четыре серии завершились без timeout. [Аналитический отчёт](docs/Latency_Report.md) содержит независимо пересчитанную таблицу, анализ каждой серии и объяснение ограничений результатов.

Шесть PNG построены через Plotly, экспортированы Kaleido и находятся в `docs/charts/`:

- [Динамика RTT: шесть отдельных графиков](docs/Charts_Analysis.md#2-динамика-rtt-по-измерениям).
- [Средний RTT](docs/charts/chart_mean_rtt.png).
- [Final SRTT](docs/charts/chart_srtt.png).
- [Jitter](docs/charts/chart_jitter.png).
- [Потери пакетов](docs/charts/chart_packet_loss.png).
- [Распределения RTT с увеличением baseline](docs/charts/chart_rtt_distribution.png).

## Документация

- [Architecture Design](docs/Architecture_Design.md): развитие ПР №1 → ПР №2 → ПР №3, модули, обмен, inFlight.
- [Reliability Protocol](docs/Reliability_Protocol.md): реализация и фактические результаты ПР №3.
- [Protocol Specification](docs/Protocol_Specification.md): header, offsets, payload, validation.
- [Experiment_Config.md](docs/Experiment_Config.md) — конфигурация эксперимента, параметры эмуляции и воспроизводимость.
- [Latency_Report.md](docs/Latency_Report.md) — результаты эксперимента, итоговая статистика и выводы.
- [Charts_Analysis.md](docs/Charts_Analysis.md) — подробный анализ всех визуализаций и пояснения для защиты.
- [Демонстрация ПР №1](docs/Демонстрация_работы_протокола.md): сохранённый игровой сценарий.


- [Демонстрация ПР №2](docs/Демонстрация_запуска_ПР2.md): запуск и работоспособность ПР №2.

