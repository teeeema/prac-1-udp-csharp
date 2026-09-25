# Protocol Specification

## 1. Назначение протокола

Бинарный протокол передаёт игровые команды ПР №1 и измерительные сообщения ПР №2. Спецификация описывает текущие [ProtocolSerializer](../src/UdpGame.Protocol/ProtocolSerializer.cs), [типы](../src/UdpGame.Protocol/Packets.cs) и [константы](../src/UdpGame.Protocol/ProtocolConstants.cs). Каждое поле имеет фиксированное положение; объекты C# напрямую в сеть не копируются.

## 2. Transport: UDP

Одна UDP-дейтаграмма содержит один пакет приложения: header + payload. Размеры ниже относятся к данным UDP, без IP- и UDP-заголовков. Склейка сообщений, padding и фрагментация на уровне приложения не предусмотрены. Максимальный размер пакета приложения — MaxPacketSize = 1024 байта.

UDP не даёт подтверждений, упорядочивания или повторной передачи. STATE_UPDATE подтверждает игровую команду, PONG отвечает на PING, но эти ответы сами по себе не превращают реализацию в надёжный транспорт.

## 3. Network Byte Order

Многобайтовые числа записываются в big-endian (Network Byte Order): старший байт первым. UInt16 0x1234 — байты 12 34; UInt64 0x0102030405060708 — 01 02 03 04 05 06 07 08.

WriteU16/ReadU16 работают с 2 байтами, WriteU64/ReadU64 — с 8. Они проверяют границы через EnsureRange и используют BinaryPrimitives. Float32 — IEEE 754, записывается WriteSingleBigEndian и читается ReadSingleBigEndian. ShotsFired — UInt32 big-endian. Однобайтовым полям порядок байтов не нужен. Выравнивания и padding нет.

## 4. Общий Header

HeaderSize = 7 байт. Во всех таблицах **Offset отсчитывается от начала дейтаграммы**, с нуля; payload начинается на offset 7.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 0 | 1 | PacketType | UInt8 | Код сообщения 1–5 |
| 1 | 2 | SequenceNumber | UInt16 | Номер запроса, возвращаемый в ответе |
| 3 | 2 | PayloadSize | UInt16 | Число байтов после header |
| 5 | 2 | ProtocolVersion | UInt16 | Текущая версия 1 |

Именно в этом порядке поля записываются CreatePacket и читаются Deserialize. Полная длина равна 7 + PayloadSize.

## 5. PacketType

| Wire name | C# enum | Value | Направление | Назначение |
|---|---|---:|---|---|
| MOVEMENT | Movement | 1 | Client → Server | Предлагаемые координаты |
| SHOOT | Shoot | 2 | Client → Server | Команда выстрела |
| STATE_UPDATE | StateUpdate | 3 | Server → Client | Результат команды и авторитетное состояние |
| PING | Ping | 4 | Client → Server | Запрос измерения задержки |
| PONG | Pong | 5 | Server → Client | Ответ с клиентской и серверными метками |

Другие коды отвергаются сериализатором. Направление проверяет приложение: сервер отвергает полученные от клиента STATE_UPDATE/PONG, даже если wire format корректен.

## 6. MOVEMENT

Client → Server. Payload — 12 байт, дейтаграмма — 19 байт.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 7 | 4 | X | Float32 | Координата X |
| 11 | 4 | Y | Float32 | Координата Y |
| 15 | 4 | Z | Float32 | Координата Z |

Deserialize отвергает NaN/Infinity. Сервер дополнительно требует каждую координату в [-1000; 1000]. Конечная координата вне мира — валидный пакет, но отклонённая команда (OUT_OF_RANGE). Позиция изменяется только при принятии всех трёх координат.

Пример: sequence 0x1234, координаты (1.0, -2.5, 0.25):

```text
01 12 34 00 0C 00 01 | 3F 80 00 00 | C0 20 00 00 | 3E 80 00 00
header (7 bytes)    | X           | Y           | Z
```

## 7. SHOOT

Client → Server. Payload — 1 байт, дейтаграмма — 8 байт.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 7 | 1 | WeaponId | UInt8 | Идентификатор оружия |

Wire format допускает любой UInt8; сервер принимает 1–3. При принятии увеличивает ShotsFired и обновляет LastWeaponId, не меняя позицию. При отказе возвращает прежнее состояние и OUT_OF_RANGE.

Пример sequence 7, WeaponId 3: `02 00 07 00 01 00 01 03`.

## 8. STATE_UPDATE

Server → Client, ответ на MOVEMENT/SHOOT с тем же sequence. Payload — 19 байт, дейтаграмма — 26 байт.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 7 | 1 | AcknowledgedType | UInt8 / PacketType | Только MOVEMENT (1) или SHOOT (2) |
| 8 | 1 | Status | UInt8 / StatusCode | ACCEPTED (0), OUT_OF_RANGE (1) |
| 9 | 4 | X | Float32 | Серверная координата X |
| 13 | 4 | Y | Float32 | Серверная координата Y |
| 17 | 4 | Z | Float32 | Серверная координата Z |
| 21 | 4 | ShotsFired | UInt32 | Счётчик принятых выстрелов |
| 25 | 1 | LastWeaponId | UInt8 | Последнее принятое оружие; до выстрела 0 |

Неверный AcknowledgedType, неизвестный Status и неконечные координаты отвергаются при чтении. Проверка AcknowledgedType есть также в SerializeStateUpdate. STATE_UPDATE не используется как ответ на PING.

## 9. PING

Client → Server. Payload — 8 байт, дейтаграмма — 15 байт. Запрос не меняет игровое состояние.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 7 | 8 | ClientSendTimeUs | UInt64 | Монотонное клиентское время до сериализации и эмуляции |

```text
Bytes:  0       1..2       3..4       5..6       7..14
       +-------+----------+----------+----------+------------------+
       | 04    | sequence | 00 08    | 00 01    | ClientSendTimeUs |
       +-------+----------+----------+----------+------------------+
       |<------------ Header: 7 bytes -------->| Payload: 8 bytes |
```

Пример из wire-format теста: sequence 0x1234, timestamp 0x0102030405060708:

```text
04 12 34 00 08 00 01 | 01 02 03 04 05 06 07 08
```

Метка выражена в микросекундах и основана на Stopwatch.GetTimestamp()/Frequency; это не UTC/Unix time. PING регистрируется в TelemetryTracker до Send, поэтому задержка Network Emulator входит в RTT.

## 10. PONG

Server → Client. Payload — 24 байта, дейтаграмма — 31 байт.

| Offset | Size, bytes | Field | Type | Description |
|---:|---:|---|---|---|
| 7 | 8 | ClientSendTimeUs | UInt64 | Точная копия метки PING |
| 15 | 8 | ServerReceiveTimeUs | UInt64 | Серверное время после Receive, до Deserialize |
| 23 | 8 | ServerSendTimeUs | UInt64 | Серверное время до SerializePong и SendTo |

```text
Bytes: 0..6          7..14               15..22                23..30
      +-------------+-------------------+---------------------+------------------+
      | Header      | ClientSendTimeUs  | ServerReceiveTimeUs | ServerSendTimeUs |
      +-------------+-------------------+---------------------+------------------+
Size: 7 bytes       |<---------------- Payload: 24 bytes ---------------------->|
```

Пример sequence 9 и трёх UInt64 из Pong round-trip теста:

```text
offset  0: 05 00 09 00 18 00 01
offset  7: 01 02 03 04 05 06 07 08
offset 15: 11 12 13 14 15 16 17 18
offset 23: 21 22 23 24 25 26 27 28
```

Клиент сопоставляет sequence с inFlight и проверяет ClientSendTimeUs. RTT равен (ClientReceiveTimeUs − ClientSendTimeUs) / 1000 в миллисекундах. Обе метки сняты на одной монотонной шкале клиента.

**ServerReceiveTimeUs − ClientSendTimeUs не является RTT**: часы сторон не обязаны иметь общую точку отсчёта и синхронизацию; кроме того, разность не включает обратный путь. Даже при синхронизации она оценивала бы одностороннюю задержку, а не round trip. Серверные метки обеспечивают наблюдаемость; код не вычитает серверную обработку из RTT и не сохраняет эти две метки в CSV.

## 11. ProtocolVersion

ProtocolVersion = 1 записывается во все пакеты. Deserialize требует точного совпадения, автоматического согласования версий нет. Пакеты прежнего формата без этого поля текущим Deserialize не поддерживаются. Номер практической работы не равен ProtocolVersion.

## 12. SequenceNumber

UInt16 в диапазоне 0–65535. Сериализатор не запрещает ноль; обычный клиент генерирует 1..count, эксперимент — 1..300. Сервер копирует sequence в STATE_UPDATE/PONG.

Номер обеспечивает сопоставление, но сам не гарантирует доставку и не устраняет дубли команд. TelemetryTracker запрещает повторную регистрацию номера, пока он есть в inFlight. Надёжное переиспользование номеров с защитой от старых пакетов и retransmission не реализованы.

## 13. PayloadSize

Поле не включает header. Deserialize проверяет PayloadSize == data.Length − 7, затем точный размер выбранного типа. Лишние байты так же недопустимы, как недостающие. Вместимость UInt16 не отменяет MaxPacketSize и фиксированные размеры payload.

## 14. Validation

Порядок проверок соответствует коду:

1. Длина дейтаграммы не меньше HeaderSize = 7 и не больше MaxPacketSize = 1024.
2. PacketType известен; UInt16 header читаются на offsets 1, 3, 5.
3. ProtocolVersion равна 1.
4. PayloadSize совпадает с фактическим остатком дейтаграммы.
5. Read-функция выбранного типа проверяет точный размер payload.
6. Читаются поля; проверяются конечность float, enum Status и AcknowledgedType там, где это требуется.

Публичные ReadU16/ReadU64/WriteU16/WriteU64 проверяют диапазон буфера, включая отрицательный offset. UInt64 timestamps проверяются как байты нужной длины, а не как синхронизированные часы. Смысл возвращённой клиентской метки PONG проверяет ExperimentRunner.

Сериализация и десериализация не симметричны по всем проверкам: SerializeMovement записывает float, запрет NaN/Infinity действует при Deserialize. Границы мира и WeaponId проверяются сервером после wire-validation.

## 15. Ошибочные datagrams

| Пример | Причина отказа |
|---|---|
| 6 байт вместо header | Недостаточная длина |
| PacketType = 255 | Неизвестный тип |
| ProtocolVersion = 2 | Неподдерживаемая версия |
| SHOOT объявляет 2 байта payload, но передан 1 | Несовпадение PayloadSize и длины |
| PING с согласованной общей длиной, но payload не 8 | Неверная длина payload типа |
| PONG обрезан до 30 байт | Truncated packet: ожидается 31 |
| Дейтаграмма длиной 1025 | Превышение MaxPacketSize |
| MOVEMENT с NaN | Неконечная координата |

Ошибки wire format дают ProtocolException. Сервер ловит его внутри цикла, логирует MALFORMED, отбрасывает пакет и продолжает работу. Валидный MOVEMENT вне границ мира не malformed: сервер отвечает OUT_OF_RANGE. Валидный server-only пакет от клиента логируется как REJECTED без ответа.

## 16. Таблица всех размеров пакетов

| Тип | Header, bytes | Payload, bytes | Datagram, bytes |
|---|---:|---:|---:|
| MOVEMENT | 7 | 12 | 19 |
| SHOOT | 7 | 1 | 8 |
| STATE_UPDATE | 7 | 19 | 26 |
| PING | 7 | 8 | 15 |
| PONG | 7 | 24 | 31 |

Размеры, порядок и примеры сверены с текущим ProtocolSerializer. В Protocol.Tests 11 методов: игровые round-trip и wire format, PING/PONG, UInt16/UInt64 big-endian и отказы при неправильных размере, версии, типе и усечении. Не каждый приведённый отрицательный пример имеет отдельный тест.
