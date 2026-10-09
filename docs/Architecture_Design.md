# Architecture Design

## 1. Назначение системы

Проект на C# / .NET 10 моделирует UDP-обмен клиента с authoritative Dedicated Server и измеряет влияние сетевых условий на задержку и потери. Сервер хранит игровое состояние, клиент отправляет команды. Экспериментальный режим собирает телеметрию без изменения игрового состояния.

Это консольный учебный прототип. Источники описания — [исходные проекты](../src), [тесты](../tests) и [CSV](latency_samples.csv). Байтовый формат описан в [Protocol Specification](Protocol_Specification.md), результаты — в [Latency Report](Latency_Report.md).

## 2. Развитие архитектуры

### Практическая работа №1

UdpGame.Protocol задаёт MOVEMENT, SHOOT и STATE_UPDATE. UdpGame.Client отправляет движение и выстрел, UdpGame.Server проверяет команду и возвращает своё состояние. Авторитетность означает, что именно сервер решает, принять ли координаты или оружие.

### Практическая работа №2

Добавлены UdpGame.Transport, UdpGame.Telemetry, PING/PONG, Network Emulator и режим --experiment. Игровой сценарий сохранён. Header ПР №2 всех пяти типов содержит ProtocolVersion; сохранение сценария не означает побайтовую совместимость с прежним заголовком без версии.

| Аспект | ПР №1 | ПР №2 — развитие |
|---|---|---|
| Сценарий | Игровая команда → состояние | Дополнительно PING → PONG → измерение |
| Проекты | Protocol, Client, Server | Дополнительно Transport и Telemetry |
| Пакеты | MOVEMENT, SHOOT, STATE_UPDATE | Дополнительно PING, PONG |
| UDP | Базовый обмен | Сервер и эксперимент используют UdpTransport |
| Клиент | Демонстрация команд | Дополнительно шесть экспериментальных серий |
| Контроль | STATE_UPDATE и SequenceNumber | inFlight, RTT, SRTT, jitter, timeout, loss |
| Артефакты | Демонстрация и снимки консоли | CSV, шесть графиков в charts/, аналитический отчёт и разбор визуализаций |

## 3. Общая архитектура

Сплошные стрелки показывают передачу данных; пунктир — использование компонента. NetworkEmulator находится в проекте Transport. Обычный клиент по-прежнему вызывает System.Net.Sockets.UdpClient напрямую.

```mermaid
flowchart LR
    C["UdpGame.Client"] -->|experiment| CT["UdpTransport: Connect / Send / Receive"]
    C -->|обычный режим| Native["System.Net.Sockets.UdpClient"]
    CT <-->|datagrams| UDP["UDP"]
    Native <-->|datagrams| UDP
    UDP <-->|datagrams| ST["UdpTransport: Bind / Receive / SendTo"]
    ST <--> S["UdpGame.Server"]
    CT -.-> E["NetworkEmulator: delay / jitter / loss"]
    C -.-> P["UdpGame.Protocol: wire format"]
    S -.-> P
    C -.-> T["UdpGame.Telemetry: измерения"]
```

Фактические ProjectReference:

| Проект | Прямые зависимости на проекты solution |
|---|---|
| UdpGame.Client | UdpGame.Protocol, UdpGame.Transport, UdpGame.Telemetry |
| UdpGame.Server | UdpGame.Protocol, UdpGame.Transport |
| UdpGame.Protocol | Нет |
| UdpGame.Transport | Нет |
| UdpGame.Telemetry | Нет |
| UdpGame.Reliability | Нет |
| UdpGame.Reliability.Tests | UdpGame.Reliability |
| UdpGame.Protocol.Tests | UdpGame.Protocol |
| UdpGame.Telemetry.Tests | UdpGame.Telemetry |

Transport получает сериализованные байты и не зависит от Protocol. Telemetry не зависит от Protocol или Transport. Все девять проектов используют net10.0; тестовые проекты подключают MSTest 4.0.2.

## 4. UdpGame.Protocol

Packet объединяет PacketHeader и объект payload. Header содержит PacketType, SequenceNumber, PayloadSize, ProtocolVersion и RequiresAck. PacketType определяет шесть допустимых сообщений; payload представлены record struct Movement, Shoot, StateUpdate, Ping, Pong, AckPayload.

ProtocolSerializer явно записывает поля в big-endian, а не копирует память C#-объектов. При чтении проверяются длина, тип, версия, размер payload и значения отдельных полей. ProtocolConstants задаёт header 8 байт, версию 2 и MaxPacketSize = 1024. ProtocolException сигнализирует об ошибке wire format. Ограничения мира и оружия принадлежат серверу, а не сериализатору.

## 5. UdpGame.Transport

UdpTransport оборачивает UdpClient. Connect создаёт клиентский сокет и при наличии профиля — эмулятор. Bind создаёт серверный сокет без эмулятора. Receive возвращает дейтаграмму и endpoint отправителя; SendTo отвечает конкретному клиенту.

Клиентский Send копирует байты и запрашивает NetworkEmulator.Next(). При удалении возвращает false. Без задержки отправляет сразу; при положительной задержке Task.Run и Task.Delay откладывают отправку, не блокируя цикл эксперимента. Отправки защищены lock; Dispose ожидает отложенные задачи и закрывает сокет.

Эмулятор действует только на исходящие PING экспериментального клиента. Он проверяет вероятность loss, выбирает целочисленный jitter во включительном диапазоне и возвращает BaseDelayMs + jitter. PONG идёт без эмуляции. Поэтому +50 ms добавляет примерно 50 ms к RTT, а не 100 ms.

Для каждой серии создаётся новый Random с seed 20260923. Это воспроизводит решения при том же профиле и порядке вызовов, но не время планирования ОС. В combined генератор расходуется и на loss, и на jitter, поэтому удаления не обязаны совпадать с loss_5. Полная таблица — в [Experiment Config](Experiment_Config.md).

## 6. UdpGame.Telemetry

TelemetryTracker хранит словарь измерений по UInt16 SequenceNumber и очередь порядка добавления. InFlightMeasurement содержит SendTimeUs, ExperimentId, Status, Attempts, RTT и SRTT. В эксперименте Attempts = 1: повторных отправок нет.

Ёмкость inFlight по умолчанию 1024, в ExperimentRunner — 128 на серию. При заполнении удаляются старейшие завершённые записи. Если старейшая запись Pending, новая регистрация вызывает ошибку: ожидающая запись не теряется. Повторно зарегистрировать номер, пока он есть в словаре, нельзя. Счётчики и список успешных RTT сохраняются независимо от вытеснения записей.

RegisterPing увеличивает sent до решения эмулятора: удалённый PING тоже считается попыткой. Первый RecordPong для Pending вычисляет (receiveUs − sendUs) / 1000, обновляет SRTT с коэффициентом 0.125 и jitter — среднее абсолютных разностей последовательных успешных RTT. GetStatistics возвращает sent, received, timeout, min/max/mean/median RTT, текущий SRTT, jitter и долю timeout от sent.

Expire(nowUs) переводит Pending в Timeout при nowUs − SendTimeUs >= timeoutUs. RecordPong сам дедлайн не проверяет: он смотрит на сохранённый статус. В цикле клиента Expire вызывается перед очередной отправкой/приёмом, поэтому обнаружение timeout дискретно. Ожидание сокета 1–20 ms — механизм опроса, отдельный от timeout измерения 1000 ms.

## 7. UdpGame.Server

Сервер слушает порт 27015 по умолчанию, хранит ClientState по строке endpoint. MOVEMENT принимает только конечные координаты в [-1000; 1000] и меняет позицию. SHOOT принимает WeaponId 1–3, увеличивает ShotsFired и обновляет LastWeaponId. Отказ даёт OUT_OF_RANGE и прежнее состояние в STATE_UPDATE.

PING обрабатывается до поиска игрового состояния: не создаёт ClientState и не меняет игру. Сервер возвращает номер и клиентскую метку запроса. ServerReceiveTimeUs снимается после Receive, ServerSendTimeUs — до сериализации PONG, а не в момент физического выхода пакета в сеть.

```mermaid
flowchart TD
    A["PING: UDP Receive"] --> B["ServerReceiveTimeUs"]
    B --> C["Deserialize"]
    C --> D["Validate внутри Deserialize"]
    D --> E["ServerSendTimeUs и создание PONG"]
    E --> F["SerializePong"]
    F --> G["SendTo исходному endpoint"]
    D -->|ProtocolException| H["MALFORMED: лог и следующий пакет"]
```

Входящие STATE_UPDATE/PONG отвергаются как server-only. Обработанные игровые команды и PING увеличивают счётчик --max-packets; malformed и server-only — не увеличивают.

## 8. UdpGame.Client

Обычный режим ПР №1 чередует MOVEMENT и SHOOT, после каждой команды ждёт STATE_UPDATE с тем же SequenceNumber. По умолчанию: 6 команд, интервал 500 ms, socket timeout 2000 ms. Thread.Sleep выполняется между командами после ожидания ответа, поэтому interval здесь не строгий период отправки.

Режим --experiment запускает ExperimentRunner: шесть профилей по 50 PING с плановым периодом 200 ms. На серию создаются новые transport, tracker, словарь sample и множество completed. SequenceNumber сквозной: 1–300. Серия завершается после отправки 50 PING и завершения всех Pending.

Клиент проверяет ClientSendTimeUs в PONG по inFlight и игнорирует несовпадение. Только первый Received добавляет успешную CSV-строку; Expire добавляет timeout. completed исключает повторную строку для того же sequence. Все строки сортируются по SequenceNumber и записываются в конце эксперимента.

В experiment mode --count, --interval-ms, --timeout-ms не задают параметры серий: используются константы ExperimentRunner. Применяются --host, --port, --csv. Планирование привязано к старту серии, но фактические времена зависят от выполнения цикла.

## 9. Обычный игровой обмен

```mermaid
sequenceDiagram
    participant C as Client
    participant S as Dedicated Server
    C->>S: MOVEMENT(seq, X, Y, Z)
    S->>S: Deserialize и проверка границ мира
    S-->>C: STATE_UPDATE(seq, MOVEMENT, status, state)
    C->>S: SHOOT(nextSeq, WeaponId)
    S->>S: Проверка оружия и обновление ShotsFired
    S-->>C: STATE_UPDATE(nextSeq, SHOOT, status, state)
```

Снимки реального обмена сохранены в [демонстрации ПР №1](Демонстрация_работы_протокола.md).

## 10. Telemetry exchange

```mermaid
sequenceDiagram
    participant C as Client / ExperimentRunner
    participant T as TelemetryTracker
    participant N as UdpTransport / NetworkEmulator
    participant S as Server
    C->>C: t0 = ClientSendTimeUs
    C->>T: RegisterPing(seq, t0, profile)
    C->>N: SerializePing и Send
    alt Эмулятор удалил PING
        C->>T: Expire(now), elapsed >= timeout
        T-->>C: Timeout
    else PING доставлен после delay + jitter
        N->>S: PING(seq, t0)
        S->>S: t1 = ServerReceiveTimeUs
        S->>S: Deserialize, t2 = ServerSendTimeUs
        S-->>N: PONG(seq, t0, t1, t2)
        N-->>C: Receive bytes
        C->>C: t3 = ClientReceiveTimeUs, Deserialize и проверка t0
        C->>T: RecordPong(seq, t3)
        T-->>C: Received, RTT = (t3 - t0) / 1000 ms
    end
```

Метки выражены в микросекундах. RTT включает задержку эмулятора, обработку сервера и обратную доставку. t0/t3 получены по монотонным часам клиента; t1/t2 не участвуют в RTT. Синхронизация часов не требуется.

## 11. inFlight state machine

```mermaid
stateDiagram-v2
    [*] --> Pending: RegisterPing
    Pending --> Received: первый PONG
    Pending --> Timeout: Expire при elapsed >= timeout
    Timeout --> Late: PONG после Expire
    Received --> Duplicate: повторный PONG
    [*] --> Unknown: PONG с неизвестным sequence
    note right of Late
        Результат LateResponse;
        сохранённый статус остаётся Timeout
    end note
    note right of Duplicate
        Результат DuplicateResponse;
        сохранённый статус остаётся Received
    end note
```

| Обозначение | Смысл | Влияние на статистику |
|---|---|---|
| Pending | PING зарегистрирован, исход ещё не определён | Sent уже увеличен |
| Received | Первый PONG для Pending принят | Received +1, обновляются RTT/SRTT/jitter |
| Timeout | Expire обнаружил истечение ожидания | Timeout +1; RTT отсутствует |
| Late | PONG для записи Timeout | LateResponse; timeout не отменяется |
| Duplicate | PONG для записи Received | DuplicateResponse; метрики не повторяются |
| Unknown | SequenceNumber отсутствует в словаре | UnknownResponse; запись не создаётся |

В MeasurementStatus существуют только Pending, Received, Timeout. Late, Duplicate и Unknown — результаты обработки PONG, не дополнительные хранимые состояния. Unknown возможен и после вытеснения старой записи. Timeout-строка сохраняет текущий SRTT, но не обновляет его.

## 12. Обработка ошибок

| Ситуация | Где обнаруживается | Результат |
|---|---|---|
| Менее 8 байт, truncated packet | Deserialize: длина header/payload | ProtocolException |
| Более MaxPacketSize = 1024 байт | Deserialize до чтения полей | ProtocolException |
| Invalid PacketType | DecodePacketType | ProtocolException |
| Invalid ProtocolVersion: не 2 | Deserialize | ProtocolException |
| Invalid PayloadSize: не совпадает с длиной или типом | Deserialize / RequirePayloadSize | ProtocolException |
| NaN/Infinity, неверный enum ответа | ReadMovement / ReadStateUpdate | ProtocolException |

Сервер ловит ProtocolException внутри цикла, пишет MALFORMED и продолжает приём. ExperimentRunner игнорирует malformed-ответы и короткие socket timeout. Обычный клиент не имеет внутреннего перехвата ProtocolException: ошибка ответа достигает внешнего обработчика и завершает клиент с кодом 1. Прочие сетевые ошибки вне специальных обработчиков также могут завершить процесс; устойчивость к malformed datagrams не означает перехват всех возможных ошибок.

В исходниках 58 тестовых методов: 21 в Protocol.Tests, 10 в Telemetry.Tests, 27 в Reliability.Tests. Новые тесты проверяют ACK/RequiresAck, retransmission, maxAttempts, failed, Karn-метаданные и AdaptiveTimeout. Они проверяют wire format, round-trip, UInt16/UInt64 big-endian, неверные размеры/версию/тип, RTT/SRTT/jitter/loss/median и классификацию PONG. Это не отдельные интеграционные тесты сокетов или эмулятора.

## 13. Почему модули разделены

Protocol определяет смысл байтов, Transport передаёт байты, Telemetry оценивает результаты. Эмулятор изменяет доставку, но не формат PING. Статистика не зависит от открытия сокетов: тесты Telemetry задают время без сети.

Client связывает компоненты в сценарий, выбирает профили, проверяет PONG и экспортирует CSV. Перенос CLI/CSV в Telemetry связал бы расчёты с конкретной демонстрацией. Библиотеки не зависят от Unity и допускают дальнейшее подключение к игровому клиенту; такого подключения сейчас нет.

## 14. Связь с дальнейшими работами

В фундаменте ПР №3 добавлены ACK/RequiresAck (wire v2), UdpGame.Reliability с ReliableChannel и AdaptiveTimeout. Reliability не зависит от Protocol, Transport или Telemetry и не открывает сокеты. Канал хранит копии байтов и immutable metadata; CollectForRetransmission выбирает повторы, а отправку выполняет будущий вызывающий слой. OnAckReceived с out PendingPacket возвращает метаданные для Karn. Подробности — в [Reliability Protocol](Reliability_Protocol.md).

Client/Server пока не используют Reliability: обычные команды и PING/PONG отправляются без ACK. Повторы PING, автоматическая надёжная отправка SHOOT, серверные ACK и защита от повторного выполнения не реализованы. AdaptiveTimeout существует как отдельный компонент и не меняет timeout эксперимента ПР №2. Эксперимент ПР №3 ещё не выполнен.
