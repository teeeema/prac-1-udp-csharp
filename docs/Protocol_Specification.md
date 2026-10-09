# Protocol Specification

## 1. Назначение и транспорт

Бинарный протокол C# / .NET 10 передаёт игровые команды ПР №1, измерения ПР №2 и ACK фундамента ПР №3. Источники: [ProtocolSerializer](../src/UdpGame.Protocol/ProtocolSerializer.cs), [модели](../src/UdpGame.Protocol/Packets.cs), [константы](../src/UdpGame.Protocol/ProtocolConstants.cs).

Одна UDP-дейтаграмма содержит один header и один payload. Размеры указаны без UDP/IP-заголовков. MaxPacketSize = 1024 байта; padding, склейка и фрагментация на уровне приложения отсутствуют. UDP сам не гарантирует доставку или порядок. STATE_UPDATE сообщает результат игровой команды; ACK подтверждает доставку по sequence, а не принятие команды игровой логикой.

## 2. Network byte order

Сериализация ручная, через BinaryPrimitives; память C#-объектов и платформенная сериализация bool не используются. UInt16, UInt32, UInt64 и IEEE 754 Float32 передаются big-endian. UInt16 0x1234 = `12 34`, UInt64 0x0102030405060708 = `01 02 03 04 05 06 07 08`. ReadU16/WriteU16/ReadU64/WriteU64 проверяют границы и бросают ProtocolException. Однобайтовые поля не имеют порядка байтов.

## 3. Общий header

HeaderSize = **8 байт**, ProtocolVersion = **2**. Все offsets ниже отсчитываются от начала дейтаграммы с нуля. Payload начинается на offset 8. Полная длина = 8 + PayloadSize.

| Offset | Size, bytes | Field | Type | Значение |
|---:|---:|---|---|---|
| 0 | 1 | PacketType | UInt8 | Код 1–6 |
| 1 | 2 | SequenceNumber | UInt16 BE | Номер пакета / корреляция ответа |
| 3 | 2 | PayloadSize | UInt16 BE | Длина payload без header |
| 5 | 2 | ProtocolVersion | UInt16 BE | 2 |
| 7 | 1 | RequiresAck | UInt8 | 0 = ACK не требуется, 1 = требуется ACK |

Другие значения RequiresAck недопустимы. В модели PacketHeader поле имеет тип bool; на wire это строго один байт. SerializeMovement, SerializeShoot и SerializeStateUpdate принимают optional `bool requiresAck = false`. SerializePing, SerializePong и SerializeAck всегда записывают false. Десериализация запрещает RequiresAck=true для ACK; для других типов допускает 0/1, а выбор надёжных команд принадлежит будущей интеграции.

**ACK никогда не требует ACK.** Это исключает бесконечную цепочку подтверждений.

## 4. PacketType и размеры

Существующие числовые значения сохранены; ACK получил новое значение 6.

| Wire name | C# enum | Value | Направление | Payload, bytes | Datagram, bytes |
|---|---|---:|---|---:|---:|
| MOVEMENT | Movement | 1 | Client → Server | 12 | 20 |
| SHOOT | Shoot | 2 | Client → Server | 1 | 9 |
| STATE_UPDATE | StateUpdate | 3 | Server → Client | 19 | 27 |
| PING | Ping | 4 | Client → Server | 8 | 16 |
| PONG | Pong | 5 | Server → Client | 24 | 32 |
| ACK | Ack | 6 | Получатель → отправитель, после интеграции | 2 | 10 |

Неизвестные коды отвергаются. Направление контролирует приложение: текущий сервер отвергает STATE_UPDATE/PONG от клиента и пока не обрабатывает ACK как подтверждение доставки.

## 5. MOVEMENT

| Offset | Size | Field | Type |
|---:|---:|---|---|
| 8 | 4 | X | Float32 BE |
| 12 | 4 | Y | Float32 BE |
| 16 | 4 | Z | Float32 BE |

Deserialize отклоняет NaN/Infinity. Сервер принимает координаты в [-1000; 1000], меняя позицию только при принятии всех трёх; иначе возвращает OUT_OF_RANGE и прежнее состояние.

Sequence 0x1234, координаты (1, -2.5, 0.25), RequiresAck=false:

```text
01 12 34 00 0C 00 02 00 | 3F 80 00 00 | C0 20 00 00 | 3E 80 00 00
```

## 6. SHOOT

| Offset | Size | Field | Type |
|---:|---:|---|---|
| 8 | 1 | WeaponId | UInt8 |

Wire допускает любой UInt8; сервер принимает 1–3. Принятие увеличивает ShotsFired и обновляет LastWeaponId без изменения позиции. Отказ даёт OUT_OF_RANGE и прежнее состояние.

Sequence 7, WeaponId 3, RequiresAck=false: `02 00 07 00 01 00 02 00 03`.
При RequiresAck=true: `02 00 07 00 01 00 02 01 03`. Текущий игровой клиент использует false; автоматической надёжной отправки пока нет.

## 7. STATE_UPDATE

Ответ на MOVEMENT/SHOOT с тем же header SequenceNumber; не используется как ответ на PING.

| Offset | Size | Field | Type |
|---:|---:|---|---|
| 8 | 1 | AcknowledgedType | MOVEMENT=1 или SHOOT=2 |
| 9 | 1 | Status | ACCEPTED=0, OUT_OF_RANGE=1 |
| 10 | 4 | X | Float32 BE |
| 14 | 4 | Y | Float32 BE |
| 18 | 4 | Z | Float32 BE |
| 22 | 4 | ShotsFired | UInt32 BE |
| 26 | 1 | LastWeaponId | UInt8; до выстрела 0 |

Deserialize проверяет AcknowledgedType, Status и конечность координат. SerializeStateUpdate также проверяет AcknowledgedType.

## 8. PING / PONG

PING не меняет игру и передаётся без ACK.

| Пакет | Offset | Size | Field | Type |
|---|---:|---:|---|---|
| PING | 8 | 8 | ClientSendTimeUs | UInt64 BE |
| PONG | 8 | 8 | ClientSendTimeUs | UInt64 BE |
| PONG | 16 | 8 | ServerReceiveTimeUs | UInt64 BE |
| PONG | 24 | 8 | ServerSendTimeUs | UInt64 BE |

PING sequence 0x1234, timestamp 0x0102030405060708:

```text
04 12 34 00 08 00 02 00 | 01 02 03 04 05 06 07 08
```

PONG sequence 9, три метки из round-trip теста:

```text
offset  0: 05 00 09 00 18 00 02 00
offset  8: 01 02 03 04 05 06 07 08
offset 16: 11 12 13 14 15 16 17 18
offset 24: 21 22 23 24 25 26 27 28
```

Метки — монотонное время в микросекундах, основанное на Stopwatch.GetTimestamp()/Frequency, не UTC. Клиент регистрирует PING до Send и эмуляции. Сервер снимает receive time после Receive до Deserialize, send time до сериализации PONG. Sequence и ClientSendTimeUs возвращаются клиенту. RTT = (ClientReceiveTimeUs − ClientSendTimeUs) / 1000 ms; обе метки сняты на часах клиента. Серверные метки не участвуют в вычислении RTT и не сохраняются в CSV ПР №2. Разность серверного и клиентского времени не является RTT: часы не синхронизированы и разность не включает обратный путь.

## 9. ACK / AckPayload

`public readonly record struct AckPayload(ushort AcknowledgedSequence)`.

| Offset | Size | Field | Type |
|---:|---:|---|---|
| 8 | 2 | AcknowledgedSequence | UInt16 BE |

PayloadSize строго 2, дейтаграмма строго **10 байт**, RequiresAck строго **0**. SerializeAck(sequenceNumber, ack) вручную записывает UInt16. Подтверждаемый номер берётся из payload, а не из header SequenceNumber ACK. Два номера могут отличаться; будущий отправитель ACK выбирает header sequence отдельно.

ACK header sequence 0x5678, подтверждение 0x1234:

```text
06 56 78 00 02 00 02 00 | 12 34
```

Повторный или неизвестный ACK не является ошибкой ReliableChannel: OnAckReceived возвращает false. Отправка ACK сервером пока не интегрирована.

## 10. ProtocolVersion и совместимость

Добавление RequiresAck сдвигает все payload на один байт: формат v1 (header 7) и v2 (header 8) несовместимы. Поэтому версия повышена с 1 до 2; Deserialize принимает только 2. Старые v1-пакеты явно отклоняются, автоматического согласования версий и fallback нет. Клиент и сервер необходимо пересобирать и обновлять вместе.

Сохраняются значения PacketType 1–5, форматы payload и сценарии ПР №1/№2. Старые вызовы сериализаторов продолжают компилироваться и отправляют RequiresAck=false. Это совместимость сценариев и исходных вызовов, а не байтовая или ABI-совместимость. PacketHeader получил дополнительное поле, в том числе изменился автоматически генерируемый Deconstruct. Сохранённые CSV, графики и демонстрации ПР №1/№2 относятся к прежнему запуску и не являются измерениями v2.

## 11. SequenceNumber и PayloadSize

SequenceNumber — UInt16 0–65535, ноль разрешён. Обычный клиент генерирует 1..count, эксперимент ПР №2 — 1..300. Сервер копирует номер запроса в STATE_UPDATE/PONG. PayloadSize не включает header и должен совпасть с длиной дейтаграммы минус 8 и фиксированным размером типа.

ReliableChannel запрещает повторную регистрацию pending sequence. Защита от старых ACK при переиспользовании номера после завершения, rollover и серверная дедупликация — задачи следующего этапа; sequence сам не обеспечивает exactly-once выполнение SHOOT. Канал должен принадлежать одному peer; endpoint проверяет интеграционный слой.

## 12. Validation

Порядок в Deserialize:

1. Длина не меньше HeaderSize=8 и не больше MaxPacketSize=1024.
2. DecodePacketType проверяет известный код; header UInt16 читаются на offsets 1, 3, 5.
3. ProtocolVersion должна быть 2.
4. RequiresAck должен быть 0/1; ACK с 1 отклоняется.
5. PayloadSize должен совпасть с фактической длиной остатка.
6. Read-функция проверяет точный размер payload выбранного типа **до чтения его полей**.
7. Проверяются поля payload: конечность float, Status и AcknowledgedType где применимо.

Неизвестный тип/версия, invalid RequiresAck, ACK с размером не 2, ACK требующий ACK, усечение и лишние байты дают ProtocolException. Например: header длиной 7, RequiresAck=2, v1 SHOOT, ACK с согласованным payload размера 0/1/3, PONG длиной 31 вместо 32, datagram длиной 1025.

Сервер ловит ProtocolException, логирует MALFORMED и продолжает. Валидная игровая команда вне допустимых границ даёт OUT_OF_RANGE. Игровая validation не подменяется ACK.

## 13. Проверка

Protocol.Tests содержит 21 тестовый метод: 11 сохранённых (wire fixtures обновлены для v2) и 10 новых. Проверяются ACK round-trip и точные big-endian байты, RequiresAck на игровых пакетах, все прежние типы и их false по умолчанию, запрет ACK-on-ACK, неверный флаг, неверные длины ACK с согласованной длиной datagram, каждое усечение ACK/header, v1 и превышение MaxPacketSize. Метрики ПР №2 проверяются отдельно в Telemetry.Tests.
