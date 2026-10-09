# Отчёт по надёжной доставке поверх UDP

## Архитектура

UdpGame.Reliability — отдельная библиотека net10.0 без ProjectReference, Socket, UdpClient или сетевого IO. Она отвечает за состояние доставки, а существующий Protocol — за байты, Transport — за передачу, Client/Server — за сценарии и игровую логику. TelemetryTracker ПР №2 сохраняет прежние расчёты RTT/SRTT/Jitter/Loss; он не становится менеджером надёжных команд.

Надёжным планируется сделать SHOOT: потеря команды теряет игровой выстрел, а повторное исполнение меняет ShotsFired дважды. MOVEMENT можно оставить ненадёжным: более свежая позиция обычно заменяет старую, и повтор устаревшей команды может ухудшить актуальность. STATE_UPDATE допускает RequiresAck в протоколе, но политика его использования ещё не интегрирована.

Wire v2 добавляет RequiresAck (один байт, 0/1) и ACK=6 с AckPayload.AcknowledgedSequence (UInt16 big-endian). ACK никогда не требует ACK. Он подтверждает доставку по payload sequence; STATE_UPDATE по-прежнему сообщает принятие или отказ игровой логики. Размеры и validation описаны в [Protocol Specification](Protocol_Specification.md). Формат v1 несовместим: обе стороны обновляются вместе.

ReliableChannel принадлежит одному peer и одному последовательному циклу обработки; он не является потокобезопасным. Вызывающий слой должен проверять источник ACK и передавать именно AcknowledgedSequence, а не header sequence ACK. Время задаётся в микросекундах на одной монотонной шкале; можно переиспользовать MonotonicClock из Telemetry на уровне приложения, не связывая библиотеки.

- `OnSent(ushort sequence, byte[] rawBytes, ulong nowUs)` регистрирует первоначальную попытку: Attempts=1, FirstSentAtUs=LastSentAtUs=nowUs. Bytes копируются. Null/пустой массив отклоняется. Дубликат pending sequence даёт InvalidOperationException, сохраняя прежнюю запись. Канал не разбирает protocol bytes: согласованность sequence и RequiresAck обеспечивает вызывающий слой.
- `OnAckReceived(ushort sequence)` снимает pending и возвращает true; неизвестный, повторный или пришедший после failed ACK даёт false.
- Перегрузка `OnAckReceived(ushort sequence, out PendingPacket? acknowledgedPacket)` возвращает снятую запись; при false — null. Это позволяет соблюдать Karn без перестройки канала.
- `CollectForRetransmission(ulong nowUs, ulong adaptiveTimeoutUs)` выбирает pending с elapsed >= RTO, увеличивает Attempts и обновляет LastSentAtUs. RTO должен быть больше нуля; время раньше LastSentAtUs не вызывает повтора и не приводит к unsigned underflow. Используется текущий переданный RTO, поэтому его изменение действует при следующем вызове.
- `maxAttempts=5` включает первоначальную отправку: максимум четыре retransmission. Пакет с Attempts=5 остаётся pending на весь последний RTO; только после его истечения переходит в failed, без шестой отправки. Значение 0 запрещено; 1 означает отсутствие повторов.
- `PendingCount`, `FailedCount`, `FailedPackets` доступны для наблюдения. FailedPacket сохраняет SequenceNumber, Attempts, FirstSentAtUs, LastSentAtUs. FailedPackets — read-only snapshot. Возвращаемые PendingPacket immutable; RawBytes отдаёт копию, поэтому вызывающий код не может изменить сохранённую дейтаграмму.

Collect резервирует повтор и считает попытку при выборе, а не при фактическом socket send. Вызывающий слой обязан отправить все возвращённые пакеты и обработать ошибки transport; эмулированный drop также является попыткой. При одинаковом nowUs и положительном RTO пакет второй раз не возвращается. Замена pending, автоматическое удаление failed и бесконечные повторы отсутствуют.

Sequence можно снова зарегистрировать после снятия pending; failed-история остаётся. Поэтому поздний ACK старого пакета после переиспользования sequence может ошибочно снять новую запись. Следующий этап должен определить время безопасного переиспользования UInt16, rollover и границы сессии. Дедупликация серверных команд обязательна до включения retries SHOOT: ACK сам не обеспечивает exactly-once выполнение.

## Adaptive RTO

AdaptiveTimeout принимает `OnSample(double rttMs)` в миллисекундах. Samples должны быть конечными и неотрицательными; ноль допустим. Ошибочные значения отклоняются до изменения состояния. Публичные свойства: SrttMs, RttVarMs, RtoMs, IsInitialized.

Константы: alpha=0.125, beta=0.25, min=100 ms, max=3000 ms.

Первый sample:

```text
SRTT = RTTsample
RTTVAR = RTTsample / 2
```

Последующие samples (RTTVAR использует SRTT до обновления):

```text
RTTVAR = (1 - beta) * RTTVAR + beta * abs(SRTT - RTTsample)
SRTT   = (1 - alpha) * SRTT + alpha * RTTsample
RTO    = clamp(SRTT + 4 * RTTVAR, 100, 3000) ms
```

До первого sample IsInitialized=false, SrttMs=RttVarMs=0 (ещё не оценки), RtoMs=1000 ms. Это определённый стартовый timeout, совпадающий с timeout измерений по умолчанию в Telemetry. Формула основана на Jacobson/Karels в объёме задания; TCP backoff и полноценный TCP RTO алгоритм здесь не реализуются.

Karn: после retransmission неизвестно, какую отправку подтверждает ACK. ACK RTT можно использовать только при Attempts==1. Интеграция сможет взять `(ackReceiveUs - FirstSentAtUs) / 1000d` из возвращённой OnAckReceived записи, предварительно проверив порядок времени. При Attempts>1 ACK снимает pending, но его RTT sample нужно пропустить. Альтернативный источник — успешный RTT PING/PONG без повторной передачи. На текущем этапе автоматической связи с AdaptiveTimeout нет.

При будущем вызове CollectForRetransmission перевод RTO ms → us: `checked((ulong)Math.Ceiling(timeout.RtoMs * 1000d))`. Округление вверх не назначает повтор раньше вычисленного RTO. Сеть, часы, приём и очередность ACK/timeout остаются ответственностью цикла приложения.

## Реализовано на текущем этапе

- Wire v2: header 8 байт, RequiresAck, PacketType.Ack=6, AckPayload 2 байта и строгая validation.
- UdpGame.Reliability в solution: pending, ACK handling, retransmission, maxAttempts, failed и защищённые snapshots.
- AdaptiveTimeout: SRTT, RTTVAR, clamp RTO, initial RTO=1000 ms, validation samples.
- Karn-ready ACK API без интеграции с сетью.
- 27 Reliability-тестов и 10 новых Protocol-тестов; сохранены 21 прежних тестов. Всего 58 тестовых методов, MSTest 4.0.2.
- Обновлены спецификация, README и описание архитектуры.

Client reliable SHOOT integration, Server ACK sending, Server duplicate-command protection, reliability_samples.csv, графики и экспериментальный анализ пока отсутствуют. Failed-история сохраняется на срок жизни канала без ограничения размера; жизненный цикл/экспорт необходимо определить для длительных сессий следующего этапа.

## Эксперимент

Будет выполнен на следующем этапе.
