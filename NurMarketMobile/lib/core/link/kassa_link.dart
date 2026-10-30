import 'dart:async';
import 'dart:collection';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

// 2026-10-27, владелец: «при сканировании мобилки камерой товар попадает прямо в кассу десктопа». Связь телефона с кассой
// по Wi-Fi магазина: протокол v1 (ТЗ «Мобилка и касса», вариант B, пакеты М-П1 и М-П4).
// Транспорт: TCP, одно соединение = один запрос. Телефон шлёт ОДНУ строку JSON (UTF-8, до \n, не больше 64 КБ), касса
// отвечает ОДНОЙ строкой JSON и закрывает соединение. Порт кассы 47830 (занят — 47831…47839, настоящий порт — в QR).
// Запрос: {"v":1,"op":…,"phone":…,"ts":…,"nonce":…,"body":"<JSON-строка>","sig":…}. Подпись —
// HMAC-SHA256(ключ из QR, "1|op|phone|ts|nonce|sha256hex(UTF8(body))"). Код магазина LAN в телефон не попадает.
// Секреты (ключ кассы) — только во flutter_secure_storage, в журнал и в текст ошибок не попадают.
//
// ТОЧКА РАСШИРЕНИЯ ПО op (М-П2 «print», М-П3 «cart», «uploaded»): новая операция — это вызов
// [KassaLink.call] с именем op и телом (Map) — подпись, время, nonce, таймауты, поиск кассы и разбор ответа общие.
// Результат — [KassaReply] (ok, code, msg и все остальные поля ответа в [KassaReply.raw]). Состояние связи
// ([KassaLink.status]) обновляется по кодам любого ответа. Очередь с автоповтором сейчас только у «scan».

/// Состояние связи с кассой (для индикатора и экрана «Связь с кассой»).
enum KassaLinkStatus {
  /// Касса не подключена (QR не сканировали).
  unpaired,

  /// Подключена, но связь ещё не проверялась.
  unknown,

  /// Касса ответила.
  online,

  /// Касса не отвечает по сохранённому адресу и не найдена в подсети телефона.
  notFound,

  /// Касса отключила этот телефон (`revoked`).
  revoked,

  /// Время телефона и кассы расходится больше чем на 10 минут (`bad_time`).
  badTime,

  /// На кассе выключен приём с телефонов (`disabled`).
  disabled,

  /// Ключ не подходит или телефон кассе неизвестен (`bad_sig`, `unknown_phone`) — нужно заново сканировать QR.
  rePair,
}

extension KassaLinkStatusRules on KassaLinkStatus {
  /// 2026-10-27 (аудит мобилки, С-4): касса точно не примет код — сканы не отправляем, а говорим почему. `notFound` сюда не
  /// входит: коды ждут в очереди (до 3 минут) и уйдут сами, когда касса снова ответит.
  bool get refusesScans => this == KassaLinkStatus.revoked || this == KassaLinkStatus.disabled || this == KassaLinkStatus.rePair || this == KassaLinkStatus.badTime;
}

/// QR сопряжения не разобран.
class KassaQrException implements Exception {
  /// 'format' — не QR кассы NurMarket; 'version' — другая версия протокола.
  final String reason;
  const KassaQrException(this.reason);
  @override
  String toString() => 'KassaQrException($reason)';
}

/// Сеть: касса не ответила, соединение оборвано или ответ не по протоколу.
class KassaNetException implements Exception {
  final String message;
  const KassaNetException(this.message);
  @override
  String toString() => 'KassaNetException($message)';
}

/// Данные сопряжения: что в QR кассы.
class KassaPairing {
  final String deviceId;
  final String host;
  final int port;
  final Uint8List key; // 32 байта; наружу не выводится
  final String name;
  final String company;
  const KassaPairing({required this.deviceId, required this.host, required this.port, required this.key, required this.name, required this.company});

  KassaPairing withAddress(String host, int port) => KassaPairing(deviceId: deviceId, host: host, port: port, key: key, name: name, company: company);

  KassaPairing withName(String n) => KassaPairing(deviceId: deviceId, host: host, port: port, key: key, name: n, company: company);

  String toJsonString() => jsonEncode({'d': deviceId, 'h': host, 'p': port, 'k': KassaProtocol.b64u(key), 'n': name, 'c': company});

  static KassaPairing? fromJsonString(String? s) {
    if (s == null || s.isEmpty) return null;
    try {
      final m = jsonDecode(s) as Map<String, dynamic>;
      final key = KassaProtocol.fromB64u(m['k'] as String);
      if (key.length != 32) return null;
      return KassaPairing(deviceId: m['d'] as String, host: m['h'] as String, port: m['p'] as int, key: key, name: (m['n'] as String?) ?? '', company: (m['c'] as String?) ?? '');
    } catch (_) {
      return null;
    }
  }

  /// Разбор QR: `nurmarket-link:v1?d=<id>&h=<IPv4>&p=<порт>&k=<base64url 32 байта>&n=<имя>&c=<8 hex>`.
  /// Не QR кассы / сломан → [KassaQrException].
  static KassaPairing parseQr(String raw) {
    final text = raw.trim();
    const prefix = 'nurmarket-link:';
    if (!text.toLowerCase().startsWith(prefix)) throw const KassaQrException('format');
    final rest = text.substring(prefix.length);
    final q = rest.indexOf('?');
    final ver = (q < 0 ? rest : rest.substring(0, q)).toLowerCase();
    if (ver != 'v1') throw KassaQrException(RegExp(r'^v\d+$').hasMatch(ver) ? 'version' : 'format');
    if (q < 0) throw const KassaQrException('format');
    final Map<String, String> p;
    try {
      p = Uri.splitQueryString(rest.substring(q + 1));
    } catch (_) {
      throw const KassaQrException('format');
    }
    final d = (p['d'] ?? '').trim();
    final h = (p['h'] ?? '').trim();
    final port = int.tryParse((p['p'] ?? '').trim());
    if (d.isEmpty || d.length > 64 || !isIPv4(h) || port == null || port < 1 || port > 65535) throw const KassaQrException('format');
    final Uint8List key;
    try {
      key = KassaProtocol.fromB64u(p['k'] ?? '');
    } catch (_) {
      throw const KassaQrException('format');
    }
    if (key.length != 32) throw const KassaQrException('format');
    return KassaPairing(deviceId: d, host: h, port: port, key: key, name: (p['n'] ?? '').trim(), company: (p['c'] ?? '').trim());
  }

  static bool isIPv4(String s) {
    final m = RegExp(r'^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$').firstMatch(s);
    if (m == null) return false;
    return [for (var i = 1; i <= 4; i++) int.parse(m.group(i)!)].every((o) => o <= 255);
  }
}

/// Ответ кассы: `{"ok":bool,"code":…,"msg":…, …}`.
class KassaReply {
  final bool ok;
  final String code;
  final String msg;
  final Map<String, dynamic> raw;
  const KassaReply({required this.ok, required this.code, required this.msg, required this.raw});

  /// Ответ не по протоколу (не JSON-объект, нет `ok`) → [FormatException].
  factory KassaReply.parse(String line) {
    final m = jsonDecode(line);
    if (m is! Map<String, dynamic> || m['ok'] is! bool) throw const FormatException('reply');
    return KassaReply(ok: m['ok'] as bool, code: (m['code'] as String?) ?? (m['ok'] as bool ? 'ok' : ''), msg: (m['msg'] as String?) ?? '', raw: m);
  }

  String? get kassa => raw['kassa'] as String?;
  String? get company => raw['company'] as String?;
  bool? get shiftOpen => raw['shift_open'] as bool?;
  String? get productName => raw['name'] as String?;
}

/// Чистые функции протокола v1 (без сети и хранилища) — проверяются тестом на фиксированных векторах.
class KassaProtocol {
  KassaProtocol._();
  static const version = 1;

  /// Допустимое число байт запроса.
  static const maxLine = 64 * 1024;

  static String b64u(List<int> bytes) => base64Url.encode(bytes).replaceAll('=', '');

  static Uint8List fromB64u(String s) {
    var t = s.trim();
    if (t.isEmpty || !RegExp(r'^[A-Za-z0-9_\-]+={0,2}$').hasMatch(t)) throw const FormatException('base64url');
    t = t.replaceAll('=', '');
    t += '=' * ((4 - t.length % 4) % 4);
    return Uint8List.fromList(base64Url.decode(t));
  }

  /// sha256 в нижнем регистре hex — по UTF-8 байтам строки тела как есть.
  static String bodyHash(String body) => sha256.convert(utf8.encode(body)).toString();

  /// Подписываемая строка: `1|op|phone|ts|nonce|sha256hex(body)`.
  static String signedText({required String op, required String phone, required int ts, required String nonce, required String body}) =>
      '$version|$op|$phone|$ts|$nonce|${bodyHash(body)}';

  /// base64url (без «=») от HMAC-SHA256.
  static String sign({required List<int> key, required String op, required String phone, required int ts, required String nonce, required String body}) =>
      b64u(Hmac(sha256, key).convert(utf8.encode(signedText(op: op, phone: phone, ts: ts, nonce: nonce, body: body))).bytes);

  /// 16 случайных байт в base64url без «=».
  static String newNonce([Random? random]) {
    final r = random ?? Random.secure();
    return b64u(List<int>.generate(16, (_) => r.nextInt(256)));
  }

  /// Строка запроса (JSON + «\n»). [body] — строка (JSON-в-строке), хэш считается по ней как есть.
  static String buildRequest({required String op, required String phone, required List<int> key, required int ts, required String nonce, required String body}) {
    final sig = sign(key: key, op: op, phone: phone, ts: ts, nonce: nonce, body: body);
    return '${jsonEncode({'v': version, 'op': op, 'phone': phone, 'ts': ts, 'nonce': nonce, 'body': body, 'sig': sig})}\n';
  }

  // ---------------------------------------------------------------- подпись ответа (2026-10-27, ревью Ф-4)
  // Формула — та же, что у кассы (NurMarketKassa.Infrastructure/Services/PhoneLink/PhoneLinkProtocol.cs: ReplySigningString, ExtraDigest):
  //   текст = "1r|" + op + "|" + phone + "|" + nonce + "|" + (ok ? "1" : "0") + "|" + code + "|" + sha256hex(extra)
  //   extra = поля ответа, кроме ok, code, msg, sig, sv; по ключам в порядке code unit; «ключ=значение» через "\n"
  //   (строка как есть, true/false, целое десятичным; пусто — sha256 пустой строки);
  //   sig = base64url(HMAC-SHA256(ключ из QR, UTF8(текст))); поле sv = 1 — версия формулы.
  // nonce и op — из НАШЕГО запроса, поэтому подпись чужого ответа или повтор старого не подойдут. Касса подписывает только ответы на
  // запросы с верной подписью (на bad_sig / unknown_phone / revoked подписи может не быть).

  static const _replyUnsigned = {'ok', 'code', 'msg', 'sig', 'sv'};

  static String _canonValue(Object? v) {
    if (v == null) return '';
    if (v is String) return v;
    if (v is bool) return v ? 'true' : 'false';
    if (v is int) return '$v';
    if (v is double && v == v.truncateToDouble() && v.abs() < 9e15) return '${v.truncate()}';
    return jsonEncode(v);
  }

  /// sha256hex от лишних полей ответа (см. выше).
  static String replyExtraDigest(Map<String, dynamic> raw) {
    final keys = raw.keys.where((k) => !_replyUnsigned.contains(k)).toList()..sort();
    return sha256.convert(utf8.encode(keys.map((k) => '$k=${_canonValue(raw[k])}').join('\n'))).toString();
  }

  static String replySigningText({required String op, required String phone, required String nonce, required bool ok, required String code, required Map<String, dynamic> raw}) =>
      '1r|$op|$phone|$nonce|${ok ? '1' : '0'}|$code|${replyExtraDigest(raw)}';

  static String signReply({required List<int> key, required String op, required String phone, required String nonce, required bool ok, required String code, required Map<String, dynamic> raw}) =>
      b64u(Hmac(sha256, key).convert(utf8.encode(replySigningText(op: op, phone: phone, nonce: nonce, ok: ok, code: code, raw: raw))).bytes);

  /// Проверка подписи ответа: true — верна; false — подпись есть, но неверна (ответ чужой или подделан); null — подписи нет
  /// (старая касса или ответ без подписи).
  static bool? verifyReply({required List<int> key, required String op, required String phone, required String nonce, required KassaReply reply}) {
    final sig = reply.raw['sig'];
    if (sig is! String || sig.isEmpty) return null;
    try {
      final given = fromB64u(sig);
      final expected = Hmac(sha256, key)
          .convert(utf8.encode(replySigningText(op: op, phone: phone, nonce: nonce, ok: reply.ok, code: reply.code, raw: reply.raw)))
          .bytes;
      if (given.length != expected.length) return false;
      var diff = 0;
      for (var i = 0; i < given.length; i++) {
        diff |= given[i] ^ expected[i];
      }
      return diff == 0;
    } catch (_) {
      return false;
    }
  }

  /// Состояние связи по коду ответа. null — код не меняет состояние (busy, not_found и т. п. — касса при этом отвечает).
  static KassaLinkStatus? statusForCode(String code) {
    switch (code) {
      case 'bad_time':
        return KassaLinkStatus.badTime;
      case 'revoked':
        return KassaLinkStatus.revoked;
      case 'disabled':
        return KassaLinkStatus.disabled;
      case 'bad_sig':
      case 'unknown_phone':
        return KassaLinkStatus.rePair;
      default:
        return null;
    }
  }
}

/// Хранилище связи: ключ кассы, id телефона, счётчик сканов. На телефоне — flutter_secure_storage.
abstract class KassaLinkStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

class SecureKassaLinkStore implements KassaLinkStore {
  static const _storage = FlutterSecureStorage();
  const SecureKassaLinkStore();
  @override
  Future<String?> read(String key) async {
    try {
      return await _storage.read(key: key);
    } catch (_) {
      return null;
    }
  }

  @override
  Future<void> write(String key, String value) async {
    try {
      await _storage.write(key: key, value: value);
    } catch (_) {
      // хранилище недоступно — связь работает до перезапуска приложения
    }
  }

  @override
  Future<void> delete(String key) async {
    try {
      await _storage.delete(key: key);
    } catch (_) {}
  }
}

/// Для тестов: хранилище в памяти.
class MemoryKassaLinkStore implements KassaLinkStore {
  final Map<String, String> data = {};
  @override
  Future<String?> read(String key) async => data[key];
  @override
  Future<void> write(String key, String value) async => data[key] = value;
  @override
  Future<void> delete(String key) async => data.remove(key);
}

/// Обмен одной строкой: соединиться, отправить [line], прочитать одну строку ответа. Ошибки сети → [KassaNetException].
typedef KassaTransport = Future<String> Function(String host, int port, String line, Duration connectTimeout, Duration replyTimeout);

Future<String> socketExchange(String host, int port, String line, Duration connectTimeout, Duration replyTimeout) async {
  Socket? s;
  try {
    s = await Socket.connect(host, port, timeout: connectTimeout);
    s.setOption(SocketOption.tcpNoDelay, true);
    s.add(utf8.encode(line));
    await s.flush();
    final reply = await s.cast<List<int>>().transform(utf8.decoder).transform(const LineSplitter()).first.timeout(replyTimeout);
    return reply;
  } on TimeoutException {
    throw const KassaNetException('timeout');
  } on SocketException {
    throw const KassaNetException('socket');
  } on StateError {
    throw const KassaNetException('closed'); // касса закрыла соединение без ответа
  } on HandshakeException {
    throw const KassaNetException('socket');
  } finally {
    s?.destroy();
  }
}

/// Чем закончилась отправка кода кассе.
enum KassaScanOutcome {
  /// Товар добавлен в чек на кассе (или код уже был принят — `duplicate`).
  added,

  /// Касса не нашла товар.
  notFound,

  /// Окно кассы не активно.
  tillInactive,

  /// Нет связи — код в очереди, касса получит его при возвращении связи.
  queued,

  /// Касса не ответила за время жизни кода в очереди.
  expired,

  /// 2026-10-27 (протокол, раздел 8): касса ответила `stale` — скан старше 30 с, в чек не попал; отсканировать снова. Не повторяется.
  stale,

  /// 2026-10-27 (протокол, раздел 8): касса занята (идёт оплата) — подсказка «сканируйте после оплаты»; код остаётся в очереди и
  /// повторится сам (до конца срока жизни). Не итог.
  busy,

  /// Касса отказала (отключён, выключен приём, время, подпись…) — код не принят; причина — в [KassaLink.status].
  failed,
}

class KassaScanEvent {
  final String code;
  final int seq;
  final KassaScanOutcome outcome;
  final String? productName;
  final int pending;
  const KassaScanEvent(this.code, this.seq, this.outcome, {this.productName, this.pending = 0});
}

/// Принят ли код к отправке.
enum KassaSubmit { accepted, notPaired, queueFull }

class _Pending {
  final String code;
  final int seq;
  final DateTime at;
  bool announced = false;
  bool busyShown = false;
  _Pending(this.code, this.seq, this.at);
}

/// Клиент связи с кассой (один на приложение — [instance]; в тестах создаётся отдельный с подменой хранилища и сети).
class KassaLink extends ChangeNotifier {
  static final KassaLink instance = KassaLink();

  /// Связь с кассой — только в мобилке (Android/iOS). На web и компьютере пункты не показываем.
  static bool get supported => !kIsWeb && (Platform.isAndroid || Platform.isIOS);

  static const defaultPort = 47830;
  static const lastPort = 47839;

  /// Сканов в очереди при потере связи — не больше; дальше отказ.
  static const maxQueue = 50;

  /// Код в очереди старше этого уже не отправляем (чтобы он не попал в чужой чек через минуты).
  /// 2026-10-27 (ревью С-1 протокола): было 3 минуты — скан мог дойти в чек уже следующего покупателя; теперь 30 секунд, дальше
  /// событие `expired` («не успел, отсканируйте ещё раз»).
  static const scanTtl = Duration(seconds: 30);
  static const _rediscoverEvery = Duration(seconds: 30);

  /// 2026-10-27 (аудит мобилки, С-7): пауза между фоновыми поисками кассы в подсети растёт, пока поиск безуспешен:
  /// 30 с → 2 мин → 5 мин (перебор — это 2 540 подключений, батарея и шум в Wi-Fi магазина). Кнопка «Проверить связь» ищет всегда.
  static Duration rediscoverPause(int failedSearches) => switch (failedSearches) {
    <= 0 => _rediscoverEvery,
    1 => const Duration(minutes: 2),
    _ => const Duration(minutes: 5),
  };

  static const _kPairing = 'kassa.link.pairing';
  static const _kPhone = 'kassa.link.phone';
  static const _kSeq = 'kassa.link.seq';

  final KassaLinkStore _store;
  final KassaTransport _transport;
  final DateTime Function() _clock;
  final Random _random;
  final Future<List<String>> Function() _subnets;
  final Duration _retryEvery;

  KassaLink({
    KassaLinkStore? store,
    KassaTransport? transport,
    DateTime Function()? clock,
    Random? random,
    Future<List<String>> Function()? subnets,
    this._retryEvery = const Duration(seconds: 3),
  }) : _store = store ?? const SecureKassaLinkStore(),
       _transport = transport ?? socketExchange,
       _clock = clock ?? DateTime.now,
       _random = random ?? Random.secure(),
       _subnets = subnets ?? localSubnets;

  KassaPairing? _pairing;
  KassaPairing? get pairing => _pairing;
  bool get isPaired => _pairing != null;

  /// Сохранённое прочитано (до этого [isPaired] ещё не говорит о подключении).
  bool get loaded => _loaded;
  bool _loaded = false;

  KassaLinkStatus _status = KassaLinkStatus.unpaired;
  KassaLinkStatus get status => _status;

  /// Имя кассы: из ответа hello, иначе из QR.
  String get kassaName => _kassaName.isNotEmpty ? _kassaName : (_pairing?.name ?? '');
  String _kassaName = '';

  /// Смена на кассе открыта (из ответа hello); null — не известно.
  bool? shiftOpen;

  /// Идёт проверка связи / поиск кассы.
  bool checking = false;

  /// Имя для hello (сотрудник из профиля). Ставят экраны.
  String deviceName = '';

  String _phone = '';
  int _seq = 0;
  Future<void>? _loading;
  Future<KassaLinkStatus>? _checkFuture;
  DateTime _lastDiscover = DateTime(2000);
  bool _discovering = false;
  int _failedSearches = 0;

  /// Идёт поиск кассы в подсети (экран пишет «Ищу кассу в сети Wi-Fi…»).
  bool get discovering => _discovering;

  final _queue = Queue<_Pending>();
  bool _pumping = false;
  Timer? _retryTimer;
  int _netFailures = 0;
  final _events = StreamController<KassaScanEvent>.broadcast();
  Stream<KassaScanEvent> get events => _events.stream;
  int get pendingCount => _queue.length;

  /// Прочитать сохранённое: сопряжение, id телефона, счётчик. Можно вызывать сколько угодно раз.
  Future<void> ensureLoaded() => _loading ??= _load();

  Future<void> _load() async {
    _pairing = KassaPairing.fromJsonString(await _store.read(_kPairing));
    var phone = await _store.read(_kPhone);
    if (phone == null || phone.length < 8 || phone.length > 64) {
      // id телефона создаётся один раз и хранится в защищённом хранилище
      phone = KassaProtocol.b64u(List<int>.generate(16, (_) => _random.nextInt(256)));
      await _store.write(_kPhone, phone);
    }
    _phone = phone;
    _seq = int.tryParse(await _store.read(_kSeq) ?? '') ?? 0;
    _status = _pairing == null ? KassaLinkStatus.unpaired : KassaLinkStatus.unknown;
    _loaded = true;
    notifyListeners();
  }

  /// Следующий seq: растёт всегда, в том числе после переустановки (тогда счётчик начинается с текущего времени, иначе
  /// касса сочла бы первые сканы повторами старых).
  int _nextSeq() {
    final byClock = _clock().millisecondsSinceEpoch ~/ 1000;
    _seq = max(_seq + 1, byClock);
    unawaited(_store.write(_kSeq, '$_seq'));
    return _seq;
  }

  void _setStatus(KassaLinkStatus s) {
    if (_status == s) return;
    _status = s;
    notifyListeners();
  }

  // ---------------------------------------------------------------- запросы

  /// Подписанный запрос любой операции [op] к кассе. Тело — Map (станет JSON-строкой). Исключения: [KassaNetException].
  /// Состояние связи обновляется по коду ответа. Точка расширения для «print», «cart», «uploaded».
  Future<KassaReply> call(String op, Map<String, dynamic> body, {Duration connectTimeout = const Duration(seconds: 2), Duration replyTimeout = const Duration(seconds: 4)}) async {
    await ensureLoaded();
    final p = _pairing;
    if (p == null) throw const KassaNetException('not paired');
    final r = await _callAt(p, p.host, p.port, op, body, connectTimeout, replyTimeout);
    _applyReply(r);
    return r;
  }

  /// 2026-10-27 (ревью Ф-4): ответы кассы без подписи пока принимаются (старая касса). Когда все кассы подписывают ответы — включить
  /// `true`: ответ без подписи (кроме отказов `bad_sig` / `unknown_phone` / `revoked` / `bad_time`, которые касса может дать без
  /// подписи) тоже считается чужим. Ответ с НЕверной подписью отбрасывается всегда.
  static bool requireSignedReplies = false;

  /// 2026-10-27 (протокол, раздел 8): касса, хоть раз приславшая ВЕРНУЮ подпись, дальше обязана подписывать (кроме отказов без ключа):
  /// поддельный узел с `ok:true` без подписи после этого не принимается. Старая касса подписей не шлёт — принимается как раньше.
  bool _kassaSigns = false;

  static const _mayBeUnsigned = {'bad_sig', 'unknown_phone', 'revoked', 'bad_time', 'bad_request'};

  Future<KassaReply> _callAt(KassaPairing p, String host, int port, String op, Map<String, dynamic> body, Duration connect, Duration reply) async {
    final bodyText = jsonEncode(body);
    final nonce = KassaProtocol.newNonce(_random);
    final line = KassaProtocol.buildRequest(op: op, phone: _phone, key: p.key, ts: _clock().millisecondsSinceEpoch ~/ 1000, nonce: nonce, body: bodyText);
    if (utf8.encode(line).length > KassaProtocol.maxLine) throw const KassaNetException('too big');
    final text = await _transport(host, port, line, connect, reply);
    final KassaReply r;
    try {
      r = KassaReply.parse(text);
    } catch (_) {
      throw const KassaNetException('bad reply'); // на порту отвечает не касса
    }
    // 2026-10-27 (ревью Ф-4): подпись ответа. Чужой узел в Wi-Fi с `ok:true` без верной подписи кассой не считается.
    final signed = KassaProtocol.verifyReply(key: p.key, op: op, phone: _phone, nonce: nonce, reply: r);
    if (signed == false) throw const KassaNetException('bad reply signature');
    if (signed == true) _kassaSigns = true;
    if (signed == null && (requireSignedReplies || _kassaSigns) && !_mayBeUnsigned.contains(r.code)) throw const KassaNetException('unsigned reply');
    return r;
  }

  /// Любой ответ кассы говорит, что она «на связи»; код уточняет причину отказа.
  void _applyReply(KassaReply r) {
    _failedSearches = 0; // касса ответила — следующий поиск снова через 30 с
    final s = KassaProtocol.statusForCode(r.code);
    if (s != null) {
      _setStatus(s);
    } else if (r.ok || r.code == 'not_found' || r.code == 'till_inactive' || r.code == 'duplicate' || r.code == 'busy') {
      _setStatus(KassaLinkStatus.online);
    }
    if (r.code == 'ok' && r.ok && r.raw.containsKey('shift_open')) {
      shiftOpen = r.shiftOpen;
      _kassaName = r.kassa ?? _kassaName;
      notifyListeners();
    }
  }

  // ------------------------------------------------------- сопряжение, проверка

  /// Сканирование QR: разобрать, сохранить, сразу hello. Неверный QR → [KassaQrException]. Возвращает состояние связи.
  Future<KassaLinkStatus> pairFromQr(String raw) async {
    final p = KassaPairing.parseQr(raw);
    await ensureLoaded();
    _queue.clear();
    _retryTimer?.cancel();
    _pairing = p;
    _kassaSigns = false;
    _kassaName = '';
    shiftOpen = null;
    _status = KassaLinkStatus.unknown;
    await _store.write(_kPairing, p.toJsonString());
    notifyListeners();
    return check();
  }

  /// hello: проверить связь. Адрес из QR не ответил за 3 с — перебор подсети /24 телефона по портам 47830…47839.
  /// [background] — проверка «по таймеру»: поиск по подсети не чаще раза в 30 с (кнопка «Проверить связь» ищет всегда).
  Future<KassaLinkStatus> check({bool discover = true, bool background = false}) {
    final running = _checkFuture;
    if (running != null) return running;
    final f = _doCheck(discover, background).whenComplete(() => _checkFuture = null);
    return _checkFuture = f;
  }

  Map<String, dynamic> get _helloBody => {'name': deviceName};

  Future<KassaLinkStatus> _doCheck(bool discover, bool background) async {
    await ensureLoaded();
    final p = _pairing;
    if (p == null) {
      _setStatus(KassaLinkStatus.unpaired);
      return _status;
    }
    checking = true;
    notifyListeners();
    try {
      KassaReply? r;
      try {
        r = await _callAt(p, p.host, p.port, 'hello', _helloBody, const Duration(seconds: 3), const Duration(seconds: 4));
      } on KassaNetException {
        r = null;
      }
      if (r == null && discover && (!background || _clock().difference(_lastDiscover) >= rediscoverPause(_failedSearches))) r = await _discover(p);
      if (r == null) {
        _setStatus(KassaLinkStatus.notFound);
      } else {
        _applyReply(r);
      }
    } finally {
      checking = false;
      notifyListeners();
    }
    return _status;
  }

  /// Ответ принадлежит нашей кассе (а не чужой на том же порту): ключ принят либо касса прямо говорит, почему отказывает.
  static bool _isOurs(KassaReply r) => r.ok || r.code == 'revoked' || r.code == 'disabled' || r.code == 'bad_time';

  /// Перебор подсети /24 телефона: 254 адреса × 10 портов, параллельно, соединение — 400 мс. Найденный адрес запоминается.
  Future<KassaReply?> _discover(KassaPairing p) async {
    if (_discovering) return null;
    _discovering = true;
    _lastDiscover = _clock();
    notifyListeners();
    try {
      final prefixes = await _subnets(); // только Wi-Fi: на мобильном интернете подсеть оператора не перебирается
      for (final prefix in prefixes) {
        final targets = [
          for (var port = defaultPort; port <= lastPort; port++)
            for (var i = 1; i <= 254; i++) ('$prefix.$i', port),
        ];
        const chunk = 128;
        for (var at = 0; at < targets.length; at += chunk) {
          final part = targets.sublist(at, min(at + chunk, targets.length));
          final found = await Future.wait(
            part.map((t) async {
              try {
                final r = await _callAt(p, t.$1, t.$2, 'hello', _helloBody, const Duration(milliseconds: 400), const Duration(seconds: 2));
                return _isOurs(r) ? (t, r) : null;
              } catch (_) {
                return null;
              }
            }),
          );
          for (final f in found) {
            if (f == null) continue;
            _pairing = p.withAddress(f.$1.$1, f.$1.$2);
            await _store.write(_kPairing, _pairing!.toJsonString());
            _failedSearches = 0;
            return f.$2;
          }
        }
      }
      _failedSearches++;
      return null;
    } finally {
      _discovering = false;
      notifyListeners();
    }
  }

  /// 2026-10-29 (полная изоляция аккаунтов): другой аккаунт — сопряжение с кассой, id телефона и счётчик сканов прошлого забыты;
  /// id телефона для нового аккаунта создаётся заново при следующем чтении.
  Future<void> forgetAccount() async {
    _retryTimer?.cancel();
    _queue.clear();
    _pairing = null;
    _kassaSigns = false;
    _kassaName = '';
    shiftOpen = null;
    _status = KassaLinkStatus.unpaired;
    _phone = '';
    _seq = 0;
    _loading = null;
    await _store.delete(_kPairing);
    await _store.delete(_kPhone);
    await _store.delete(_kSeq);
    notifyListeners();
  }

  /// Отключить телефон от кассы (на кассе ключ отзывается отдельно): забыть ключ и очередь.
  Future<void> disconnect() async {
    await ensureLoaded();
    _retryTimer?.cancel();
    _queue.clear();
    _pairing = null;
    _kassaSigns = false;
    _kassaName = '';
    shiftOpen = null;
    _status = KassaLinkStatus.unpaired;
    await _store.delete(_kPairing);
    notifyListeners();
  }

  // ------------------------------------------------------------ сканы (М-П4)

  /// Отправить штрихкод на кассу (op "scan"). Код попадает в очередь (≤ 50) и уходит по порядку; результат каждого —
  /// событие в [events]. Нет связи — код ждёт в очереди и отправляется сам (повтор каждые 3 с, живёт 3 минуты).
  KassaSubmit submitScan(String code) {
    if (_pairing == null) return KassaSubmit.notPaired;
    if (_queue.length >= maxQueue) return KassaSubmit.queueFull;
    _queue.add(_Pending(code, _nextSeq(), _clock()));
    notifyListeners();
    unawaited(_pump());
    return KassaSubmit.accepted;
  }

  void _emit(_Pending p, KassaScanOutcome o, {String? name}) {
    _events.add(KassaScanEvent(p.code, p.seq, o, productName: name, pending: _queue.length));
    notifyListeners();
  }

  void _scheduleRetry() {
    _retryTimer?.cancel();
    if (_queue.isEmpty) return;
    _retryTimer = Timer(_retryEvery, () => unawaited(_pump()));
  }

  Future<void> _pump() async {
    if (_pumping) return;
    _pumping = true;
    try {
      while (_queue.isNotEmpty) {
        final p = _pairing;
        if (p == null) return;
        final item = _queue.first;
        if (_clock().difference(item.at) > scanTtl) {
          _queue.removeFirst();
          _emit(item, KassaScanOutcome.expired);
          continue;
        }
        final KassaReply r;
        try {
          // 2026-10-27: `at` — время скана (unix-секунды UTC): касса с 1.17.63 отвечает `stale`, если скан старше 30 с. Ответ ждём 8 с
          // (старая касса искала товар до 15 с — ложная «потеря связи» и перебор подсети; новая отвечает за ~2,5 с)
          r = await _callAt(p, p.host, p.port, 'scan', {'code': item.code, 'seq': item.seq, 'at': item.at.toUtc().millisecondsSinceEpoch ~/ 1000}, const Duration(seconds: 2), const Duration(seconds: 8));
        } on KassaNetException {
          if (!_queue.contains(item)) continue; // за время запроса очередь очистили (отключение)
          _netFailures++;
          _setStatus(KassaLinkStatus.notFound);
          if (!item.announced) {
            item.announced = true;
            _emit(item, KassaScanOutcome.queued);
          }
          _scheduleRetry();
          // адрес мог смениться (DHCP) — через две неудачи ищем кассу в подсети, не чаще раза в 30 с
          if (_netFailures >= 2 && _clock().difference(_lastDiscover) >= rediscoverPause(_failedSearches) && !_discovering) {
            unawaited(check(background: true));
          }
          return;
        }
        _netFailures = 0;
        if (!_queue.contains(item)) continue;
        _applyReply(r);
        if (r.code == 'busy' || r.code == 'replay') {
          // касса занята (в том числе идёт оплата) — повторим тем же seq, пока не истёк срок кода; кассиру один раз подсказка
          if (r.code == 'busy' && !item.busyShown) {
            item.busyShown = true;
            _emit(item, KassaScanOutcome.busy);
          }
          _scheduleRetry();
          return;
        }
        _queue.removeFirst();
        if (r.ok) {
          _emit(item, KassaScanOutcome.added, name: r.productName);
        } else if (r.code == 'not_found') {
          _emit(item, KassaScanOutcome.notFound);
        } else if (r.code == 'till_inactive') {
          _emit(item, KassaScanOutcome.tillInactive);
        } else if (r.code == 'stale') {
          _emit(item, KassaScanOutcome.stale);
        } else {
          _emit(item, KassaScanOutcome.failed);
        }
      }
    } finally {
      _pumping = false;
    }
  }

  @override
  void dispose() {
    _retryTimer?.cancel();
    _events.close();
    super.dispose();
  }
}

/// 2026-10-27 (аудит мобилки, С-7): Wi-Fi (и проводной) интерфейс по имени: Android wlan0/swlan0/wifi0/eth0, iOS en0 / точка
/// доступа ap0. Мобильные (rmnet*, ccmni*, pdp_ip*, v4-rmnet*), VPN (tun*, ppp*), dummy*, p2p* — нет: подсеть оператора не перебираем.
bool isWifiInterface(String name) => RegExp(r'^(wlan|swlan|wifi|ap|eth|en)\d*$').hasMatch(name.toLowerCase());

/// Префиксы «a.b.c» подсетей /24 из списка (имя интерфейса, IPv4-адреса): только Wi-Fi, без loopback и link-local 169.254.*.
List<String> wifiSubnetPrefixes(Iterable<(String, List<String>)> interfaces) {
  final out = <String>[];
  for (final (name, addresses) in interfaces) {
    if (!isWifiInterface(name)) continue;
    for (final address in addresses) {
      final parts = address.split('.');
      if (parts.length != 4 || address.startsWith('169.254.') || address.startsWith('127.')) continue;
      final prefix = parts.take(3).join('.');
      if (!out.contains(prefix)) out.add(prefix);
    }
  }
  return out;
}

/// Префиксы «a.b.c» подсетей /24 телефона в Wi-Fi. Нет Wi-Fi (мобильный интернет) — пусто, перебора нет.
Future<List<String>> localSubnets() async {
  try {
    final nics = await NetworkInterface.list(type: InternetAddressType.IPv4, includeLoopback: false);
    return wifiSubnetPrefixes([for (final nic in nics) (nic.name, [for (final a in nic.addresses) a.address])]);
  } catch (_) {
    return <String>[];
  }
}
