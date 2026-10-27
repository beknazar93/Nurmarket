// 2026-10-26 (голос-управление, В4): стенд для проверок звонка на фейках — без сети, микрофона, камеры и боевой базы.
// Фейковые сокет Gemini Live, звук, камера, навигация, каталог и корзина; роутер go_router в памяти; база drift в памяти.
import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:camera/camera.dart' show CameraController, CameraLensDirection, ResolutionPreset;
import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/nav_tools.dart';
import 'package:nurmarket/core/ai/assistant/product_wizard.dart' show WizardBackend;
import 'package:nurmarket/core/ai/assistant/read_tools.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart';
import 'package:nurmarket/core/ai/live_call.dart';
import 'package:nurmarket/core/ai/live_video.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/admin_repository.dart' show DeletionRow;
import 'package:nurmarket/features/assistant/call_controller.dart';
import 'package:nurmarket/features/assistant/call_overlay.dart';
import 'package:nurmarket/features/admin/advisor_call.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/shift/shift_repository.dart' show ShiftInfo;
import 'package:nurmarket/l10n/app_localizations.dart';

const callDelegates = <LocalizationsDelegate<dynamic>>[
  AppLocalizations.delegate,
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];

/// Телефон 360×740 dp (720×1480 при плотности 2), шрифт [scale].
void callPhone(WidgetTester t, {double scale = 1.0, Size size = const Size(720, 1480)}) {
  t.view.physicalSize = size;
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

Product testProduct(String id, String name, {double price = 100, double qty = 10, String unit = 'шт', String? barcode}) => Product(
  id: id,
  name: name,
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: qty,
  unit: unit,
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
  barcode: barcode,
);

const allPerms = [
  Session.viewProducts, Session.viewAnalytics, Session.viewClients, Session.viewOrders, Session.viewShifts,
  Session.deleteLine, Session.discount, Session.employeeReturn, Session.editPrice, Session.procurement, Session.sale,
];

Session testOwner() => const Session(email: 'o', access: '', refresh: '', userId: 'u1', userName: 'Владелец', role: 'owner', companyName: 'NBS', isOwner: true, perms: allPerms, cashboxId: 'cb1');
Session testCashier({List<String> perms = const [Session.viewProducts, Session.sale]}) =>
    Session(email: 'c', access: '', refresh: '', userId: 'u2', userName: 'Кассир', role: 'cashier', companyName: 'NBS', perms: perms, cashboxId: 'cb1');

// ---------------------------------------------------------------------------------------------------------------------

class FakeSocket implements LiveSocket {
  final ctrl = StreamController<Object?>();
  final sent = <Map<String, dynamic>>[];
  @override
  int? closeCode;
  @override
  String? closeReason;
  bool closed = false;
  @override
  Stream<Object?> get frames => ctrl.stream;
  @override
  void send(String text) => sent.add(jsonDecode(text) as Map<String, dynamic>);
  @override
  Future<void> close() async {
    closed = true;
    if (!ctrl.isClosed) await ctrl.close();
  }

  void push(Map<String, dynamic> m) => ctrl.add(jsonEncode(m));

  /// Расшифровка речи пользователя (как присылает Gemini).
  void said(String text) => push({
    'serverContent': {
      'inputTranscription': {'text': text},
    },
  });

  void aiSaid(String text) => push({
    'serverContent': {
      'outputTranscription': {'text': text},
    },
  });

  void turnComplete() => push({
    'serverContent': {'turnComplete': true},
  });

  void toolCall(String id, String name, [Map<String, dynamic> args = const {}]) => push({
    'toolCall': {
      'functionCalls': [
        {'id': id, 'name': name, 'args': args},
      ],
    },
  });

  List<Map<String, dynamic>> get responses => [
    for (final m in sent)
      if (m['toolResponse'] != null)
        for (final r in (m['toolResponse'] as Map)['functionResponses'] as List) (r as Map).cast<String, dynamic>(),
  ];

  Map<String, dynamic>? responseOf(String id) {
    for (final r in responses) {
      if (r['id'] == id) return (r['response'] as Map).cast<String, dynamic>();
    }
    return null;
  }

  List<String> get texts => [
    for (final m in sent)
      if (m['clientContent'] != null) (((m['clientContent'] as Map)['turns'] as List).first as Map)['parts'][0]['text'] as String,
  ];
}

class FakeAudio implements LiveAudio {
  final ctrl = StreamController<Object?>.broadcast();
  bool started = false;
  bool stopped = false;
  bool muted = false;
  @override
  Stream<Object?> get events => ctrl.stream;
  @override
  Future<bool> start({required bool speaker}) async => started = true;
  @override
  Future<void> play(Uint8List pcm) async {}
  @override
  Future<void> flush() async {}
  @override
  Future<void> setMuted(bool m) async => muted = m;
  @override
  Future<void> setSpeaker(bool on) async {}
  @override
  Future<void> stop() async => stopped = true;
}

class FakeFrames implements LiveFrames {
  int starts = 0;
  int stops = 0;
  CameraLensDirection lens = CameraLensDirection.back;
  bool torch = false;
  bool open = false;
  Object? failStart;
  Uint8List? still0;
  String? stillBarcode;
  @override
  bool hasFront = true;
  @override
  bool hasBack = true;
  @override
  CameraController? get controller => null;
  @override
  bool get isOpen => open;
  @override
  Uint8List? get lastFrame => null;
  @override
  Future<void> start({required CameraLensDirection lens}) async {
    if (failStart != null) throw failStart!;
    starts++;
    this.lens = lens;
    open = true;
  }

  @override
  Future<Uint8List?> grab() async => Uint8List.fromList([0xFF, 0xD8, 1, 2, 3, 4]);
  @override
  Future<Uint8List?> still(ResolutionPreset preset) async => still0;
  @override
  Future<String?> barcodeOfLastStill() async => stillBarcode;
  @override
  Future<void> setTorch(bool on) async => torch = on;
  @override
  Future<void> stop() async {
    stops++;
    open = false;
  }

  @override
  Future<void> purge() async {}
}

/// Навигация в памяти (без go_router): только запоминает.
class FakeNav implements NavHost {
  @override
  String location = '/home';
  @override
  bool canGoBack = true;
  final routes = <String>[];
  final products = <String>[];
  @override
  Future<void> openRoute(String l) async {
    routes.add(l);
    location = l;
  }

  @override
  Future<void> openAux(String key) async {}
  @override
  Future<void> goHome() async => location = '/home';
  @override
  Future<void> goBack() async {}
  @override
  Future<List<NavProduct>> findProducts({String? query, String? barcode}) async => const [];
  @override
  Future<void> openProduct(NavProduct p) async => products.add(p.id);
  @override
  Future<bool> openCheck({String? number, bool last = false}) async => true;
}

/// Мини-приложение: каталог, корзина, тема. Всё остальное — «не реализовано в стенде» (в этих проверках не нужно).
class FakeActions implements ActionHost {
  var products = <Product>[testProduct('p1', 'Сникерс', price: 110), testProduct('p2', 'Кола 0,5 л', price: 60), testProduct('p3', 'Кола 1,5 л', price: 120)];
  Cart _cart = const Cart();
  String theme = 'light';
  String lang = 'ru';
  final calls = <String>[];

  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');

  @override
  Future<List<Product>> findProducts(String query, {int limit = 30}) async {
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty);
    return [for (final p in products) if (words.every(p.searchKey.contains)) p].take(limit).toList();
  }

  @override
  Future<List<Product>> allProducts() async => products;
  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async => (product: products.where((p) => p.barcode == code).firstOrNull, unavailable: false);
  @override
  Future<Product?> productById(String id) async => products.where((p) => p.id == id).firstOrNull;
  @override
  Cart get cart => _cart;
  @override
  void restoreCart(Cart cart) => _cart = cart;
  @override
  void addToCart(Product p, double qty) {
    calls.add('add:${p.id}:$qty');
    _cart = _cart.add(CartLine(productId: p.id, name: p.name, price: p.price, qty: qty, isWeight: p.isWeight, unit: p.unit, promo: PromoRule.parse(p.promoRules)));
  }

  @override
  void setCartQty(int index, double qty) => _cart = _cart.setQty(index, qty);
  @override
  void removeCartLine(int index) => _cart = _cart.remove(index);
  @override
  void clearCart() => _cart = const Cart();
  @override
  Future<void> logDeletion(DeletionRow row) async {}
  var shiftInfo = const ShiftInfo(id: 's1', cashboxId: 'cb1', expectedCash: 5000);
  final cashMoves = <({bool out, double amount})>[];
  @override
  ShiftInfo? get shift => shiftInfo;
  @override
  Future<void> refreshShift() async {}
  @override
  Future<void> cashFlow({required String shiftId, required String cashboxId, required bool withdrawal, required double amount, required String reason}) async =>
      cashMoves.add((out: withdrawal, amount: amount));
  @override
  String get themeMode => theme;
  @override
  String get language => lang;
  @override
  Future<void> setThemeMode(String mode) async => theme = mode;
  @override
  Future<void> setLanguage(String code) async => lang = code;
}

class FakeReads implements ReadSource {
  var products = <Product>[testProduct('p1', 'Сникерс', price: 110, qty: 2), testProduct('p2', 'Кола 0,5 л', price: 60, qty: 1), testProduct('p3', 'Хлеб', price: 35, qty: 0)];
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');
  @override
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000}) async => products;
  @override
  Cart get cart => const Cart();
}

class FakeStaff implements StaffSource {
  @override
  Set<StaffCap> get caps => const {};
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('стенд: ${i.memberName}');
}

/// Журнал действий ИИ без записи в файл (настоящая запись — реальный ввод-вывод, в виджет-тестах он не завершается).
class MemoryJournal extends AiJournal {
  final records = <AiActionRecord>[];
  @override
  Future<void> record(AiActionRecord r) async => records.add(r);
}

class KeyVision extends VisionService {
  KeyVision(super.api, super.db);
  @override
  Future<String?> key() async => 'test-key';
}

class _TestSession extends SessionController {
  final Session? v;
  _TestSession(this.v);
  @override
  Session? build() => v;
}

class _TestPrefs extends PrefsController {
  final String lang;
  final bool dark;
  _TestPrefs(this.lang, this.dark);
  @override
  Prefs build() => Prefs(locale: Locale(lang), dark: dark);
}

// ---------------------------------------------------------------------------------------------------------------------

class CallRig {
  final String lang;
  final bool dark;
  final Session session;
  final bool camera;
  final Duration maxDuration;

  /// 2026-10-27 (М2): подставной справочник / ИИ по фото / создание для мастера «добавить товар».
  final WizardBackend? wizardBackend;
  late AppDatabase db;
  late ProviderContainer container;
  late AssistantCallController c;
  final sockets = <FakeSocket>[];
  final audio = FakeAudio();
  final frames = FakeFrames();
  final actions = FakeActions();
  FakeNav? fakeNav;
  GoRouter? router;
  final navKey = GlobalKey<NavigatorState>();
  CallRequest request = CallRequest(prepare: (_) async => 'instruction', askTable: (_) async => '', reveal: (t) => t);
  final prepared = <CallPrep>[];
  final journal = MemoryJournal();

  CallRig({this.lang = 'ru', this.dark = false, Session? session, this.camera = false, this.maxDuration = const Duration(minutes: 10), bool useFakeNav = true, this.wizardBackend})
    : session = session ?? testOwner() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    LiveCall.resetForTest();
    db = AppDatabase(NativeDatabase.memory());
    if (useFakeNav) fakeNav = FakeNav();
    request = CallRequest(
      prepare: (p) async {
        prepared.add(p);
        return 'instruction';
      },
      askTable: (_) async => '',
      reveal: (t) => t,
    );
    final deps = CallDeps(
      audio: () => audio,
      connect: (uri, headers) async {
        final s = FakeSocket();
        sockets.add(s);
        return s;
      },
      frames: camera ? () => frames : null,
      hasCamera: () async => camera,
      android: true,
      maxDuration: maxDuration,
      nav: fakeNav,
      actions: actions,
      reads: FakeReads(),
      staff: FakeStaff(),
      journal: journal,
      wizardBackend: wizardBackend,
    );
    container = ProviderContainer(
      overrides: [
        databaseProvider.overrideWithValue(db),
        sessionProvider.overrideWith(() => _TestSession(this.session)),
        prefsProvider.overrideWith(() => _TestPrefs(lang, dark)),
        visionProvider.overrideWith((ref) => KeyVision(ref.watch(apiProvider), db)),
        callControllerProvider.overrideWith((ref) {
          final x = AssistantCallController(ref.read, deps: deps);
          ref.onDispose(x.dispose);
          return x;
        }),
      ],
    );
    c = container.read(callControllerProvider);
  }

  /// Приложение: переданный экран внутри слоя звонка.
  Widget app(Widget home) => UncontrolledProviderScope(
    container: container,
    child: MaterialApp(
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: callDelegates,
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      navigatorKey: navKey,
      builder: (context, child) => AssistantOverlay(navigatorKey: navKey, child: child!),
      home: home,
    ),
  );

  /// Приложение на go_router: разделы /home, /stock, /sale (фиксированные заглушки).
  Widget routerApp({Map<String, Widget>? screens}) {
    final pages = screens ?? {'/home': const _Page('Главная'), '/stock': const _Page('Склад'), '/sale': const _Page('Продажа')};
    router = GoRouter(
      initialLocation: '/home',
      routes: [for (final e in pages.entries) GoRoute(path: e.key, builder: (_, _) => e.value)],
    );
    c.attachRouter(router!);
    return UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: callDelegates,
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        routerConfig: router,
        builder: (context, child) => AssistantOverlay(navigatorKey: router!.routerDelegate.navigatorKey, child: child!),
      ),
    );
  }

  /// Начать звонок и дождаться «setupComplete».
  Future<void> startCall(WidgetTester t) async {
    final f = c.start(request);
    await t.pump(const Duration(milliseconds: 20));
    for (var i = 0; i < 20 && sockets.isEmpty; i++) {
      await t.pump(const Duration(milliseconds: 20));
    }
    sockets.last.push({'setupComplete': {}});
    await t.pump(const Duration(milliseconds: 20));
    await f;
    await t.pump(const Duration(milliseconds: 20));
  }

  FakeSocket get socket => sockets.last;

  /// Показать полный экран звонка (как advisor.dart: маршрут в навигаторе приложения).
  Future<void> showFull(WidgetTester t, {NavigatorState? nav}) async {
    final n = nav ?? Navigator.of(t.element(find.byType(StubScreen)));
    unawaited(c.showFull(n, (_) => callScreen()));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
  }

  /// Прогнать цикл событий несколько раз (ответы инструментов приходят через очередь).
  Future<void> settle(WidgetTester t, [int n = 6]) async {
    for (var i = 0; i < n; i++) {
      await t.pump(const Duration(milliseconds: 30));
    }
  }

  Future<void> dispose(WidgetTester t) async {
    c.endCall();
    await t.pump(const Duration(milliseconds: 50));
    container.dispose();
    // базу в памяти не закрываем: close() не открытой базы в виджет-тесте зависает (FakeAsync); память освободится с тестом
  }
}

class _Page extends StatelessWidget {
  final String title;
  const _Page(this.title);
  @override
  Widget build(BuildContext context) => Scaffold(appBar: AppBar(title: Text(title)), body: Center(child: Text('экран $title')));
}

/// Экран-пустышка для `home`.
class StubScreen extends StatelessWidget {
  final String title;
  const StubScreen(this.title, {super.key});
  @override
  Widget build(BuildContext context) => Scaffold(appBar: AppBar(title: Text(title)), body: Center(child: Text('экран $title')));
}

/// Полный экран звонка (для проверок состояний).
Widget callScreen() => const AdvisorCallScreen();

/// Элемент с подписью для экранного диктора (без включения семантики: у `bySemanticsLabel` она нужна, а дерево семантики в
/// виджет-тестах надо закрывать до конца теста).
Finder bySem(String label) => find.byWidgetPredicate((w) => w is Semantics && w.properties.label == label);

/// Текст с обычными пробелами вместо неразрывных («95 сом»).
Finder textN(String text) => find.byWidgetPredicate((w) => w is Text && (w.data ?? w.textSpan?.toPlainText() ?? '').replaceAll(RegExp('[  ]'), ' ') == text);
