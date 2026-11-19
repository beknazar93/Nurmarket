import 'dart:async';

import '../api/api_client.dart';
import '../api/server_capabilities.dart';
import '../db/database.dart';

/// 2026-11-13 (Л-7, push-уведомления): КАРКАС без ключей. Firebase/APNs в приложение не подключены (в pubspec нет firebase_*;
/// нужны google-services.json, ключ APNs и серверная отправка по событиям ленты) — пока работает [NoPushTransport]: ничего не
/// запрашивает, ничего не отправляет. Когда ключи и сервер будут готовы, достаточно написать один класс-транспорт (`FcmTransport implements
/// PushTransport`) и передать его в [PushService] — разбор сообщений, переходы по экранам и настройки видов уже готовы и покрыты тестами.
///
/// Что нужно от сервера (Умар): см. docs/Отчеты/Мобилка — аудит UX (13.11.2026).md, раздел «Push».

/// Вид уведомления (из плана Л-7: «продажа, недостача, заказ с сайта, низкий остаток» + закрытие смены).
enum PushKind {
  sale('sale'),
  shortage('shortage'),
  siteOrder('site_order'),
  lowStock('low_stock'),
  shiftClosed('shift_closed'),
  unknown('');

  final String code;
  const PushKind(this.code);

  /// Код вида из поля `type` сообщения (регистр и дефисы не важны); незнакомый — [unknown].
  static PushKind fromCode(Object? raw) {
    final t = '${raw ?? ''}'.trim().toLowerCase().replaceAll('-', '_');
    for (final k in PushKind.values) {
      if (k.code.isNotEmpty && k.code == t) return k;
    }
    return PushKind.unknown;
  }

  /// Экран приложения, который открывается по нажатию на уведомление (маршруты go_router из app.dart; доступ проверяет redirect).
  String? get route => switch (this) {
    PushKind.sale => '/checks',
    PushKind.shortage => '/admin/shifts',
    PushKind.siteOrder => '/admin/orders',
    PushKind.lowStock => '/stock?filter=low',
    PushKind.shiftClosed => '/admin/shifts',
    PushKind.unknown => null,
  };
}

/// Разобранное сообщение: что показать и куда перейти.
class PushMessage {
  final PushKind kind;
  final String title;
  final String body;
  final String? route;
  const PushMessage({required this.kind, required this.title, required this.body, this.route});

  /// Сообщение из `data` push-уведомления. Поля: `type`, `title`, `body`, необязательная `route` (только внутренний путь «/…» — чужие
  /// ссылки и схемы отбрасываются, чтобы уведомление не открывало постороннее).
  factory PushMessage.parse(Map<dynamic, dynamic>? data) {
    final d = data ?? const {};
    final kind = PushKind.fromCode(d['type'] ?? d['kind']);
    final custom = '${d['route'] ?? ''}'.trim();
    final safeCustom = custom.startsWith('/') && !custom.startsWith('//') && !custom.contains('://') ? custom : null;
    return PushMessage(kind: kind, title: '${d['title'] ?? ''}'.trim(), body: '${d['body'] ?? ''}'.trim(), route: safeCustom ?? kind.route);
  }
}

/// Какие виды уведомлений человек выключил на этом телефоне (по умолчанию включены все). Хранится в настройках телефона.
class PushPrefs {
  static const _key = 'push.disabled';

  /// Выключенные виды (коды через запятую).
  static Future<Set<PushKind>> disabled(AppDatabase db) async {
    final raw = await db.getSetting(_key) ?? '';
    return {for (final c in raw.split(',')) if (c.trim().isNotEmpty) PushKind.fromCode(c)}..remove(PushKind.unknown);
  }

  static Future<bool> enabled(AppDatabase db, PushKind kind) async => !(await disabled(db)).contains(kind);

  static Future<void> set(AppDatabase db, PushKind kind, bool on) async {
    if (kind == PushKind.unknown) return;
    final off = await disabled(db);
    on ? off.remove(kind) : off.add(kind);
    await db.setSetting(_key, (off.map((k) => k.code).toList()..sort()).join(','));
  }
}

/// Связь с push-сетью (FCM / APNs). Настоящая реализация появится вместе с ключами.
abstract class PushTransport {
  /// Транспорт подключён (есть ключи и разрешение платформы).
  bool get available;

  /// Спросить у человека разрешение на уведомления (Android 13+, iPhone). false — отказ или не поддерживается.
  Future<bool> requestPermission();

  /// Токен этого телефона; null — нет.
  Future<String?> token();

  /// Токен сменился.
  Stream<String> get onTokenRefresh;

  /// Человек нажал на уведомление (приложение было закрыто или свёрнуто) — `data` сообщения.
  Stream<Map<String, dynamic>> get onOpened;
}

/// Заглушка: push не подключён.
class NoPushTransport implements PushTransport {
  const NoPushTransport();
  @override
  bool get available => false;
  @override
  Future<bool> requestPermission() async => false;
  @override
  Future<String?> token() async => null;
  @override
  Stream<String> get onTokenRefresh => const Stream.empty();
  @override
  Stream<Map<String, dynamic>> get onOpened => const Stream.empty();
}

/// Регистрация токена на сервере и переходы по нажатию. Ничего не делает, пока нет ТРЁХ условий: транспорт подключён, сервер объявил
/// признак [capability], человек разрешил уведомления.
class PushService {
  /// Признак сервера (`api/main/capabilities/`): «умею принимать токены и слать push». Имя предлагается Умару — согласовать.
  static const capability = 'push_notifications';

  /// Адрес регистрации токена (ПРЕДЛОЖЕНИЕ, сервером не реализовано): POST {token, platform, app, lang}, DELETE при выходе.
  static const registerPath = 'api/main/push/devices/';

  final PushTransport transport;
  final ApiClient api;
  final void Function(String route) open;
  StreamSubscription<String>? _refreshSub;
  StreamSubscription<Map<String, dynamic>>? _openedSub;
  String? _sent;

  PushService({required this.transport, required this.api, required this.open});

  /// Можно ли вообще работать с push у этого аккаунта.
  bool get ready => transport.available && ServerCapabilities.has(capability);

  /// Запуск после входа: запрос разрешения, отправка токена, подписка на нажатия. false — push не включён.
  Future<bool> start({required String platform, required String lang}) async {
    if (!ready) return false;
    if (!await transport.requestPermission()) return false;
    await _register(platform, lang);
    _refreshSub ??= transport.onTokenRefresh.listen((t) => unawaited(_register(platform, lang, token: t)));
    _openedSub ??= transport.onOpened.listen(handleOpened);
    return true;
  }

  Future<void> _register(String platform, String lang, {String? token}) async {
    try {
      final t = token ?? await transport.token();
      if (t == null || t.isEmpty || t == _sent) return;
      await api.post(registerPath, body: {'token': t, 'platform': platform, 'app': 'mobile', 'lang': lang});
      _sent = t;
    } catch (_) {
      // нет связи или сервер не принял — токен уйдёт при следующем запуске; на работу кассы это не влияет
    }
  }

  /// Нажатие на уведомление: переход на экран вида (если он известен).
  void handleOpened(Map<String, dynamic> data) {
    final m = PushMessage.parse(data);
    final r = m.route;
    if (r != null) open(r);
  }

  /// Выход из аккаунта: токен снимается с этого аккаунта на сервере (сбой не мешает выходу).
  Future<void> stop() async {
    await _refreshSub?.cancel();
    await _openedSub?.cancel();
    _refreshSub = _openedSub = null;
    final had = _sent != null;
    _sent = null;
    if (!had || !ready) return;
    try {
      await api.delete(registerPath);
    } catch (_) {}
  }
}
