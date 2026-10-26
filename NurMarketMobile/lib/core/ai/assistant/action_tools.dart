// 2026-10-26 (голос-управление, В3а): инструменты-ДЕЙСТВИЯ звонка с Нур Советником — корзина, оплата, возврат, приход, списание,
// ревизия, цены, массовая наценка, штрихкод, минимальный остаток, касса, смена, долги, бонусы, клиенты, заказы, тема и язык,
// «отмени последнее» и «последние действия» (раздел 3.3 ТЗ).
//
// ПОПРАВКА ВЛАДЕЛЬЦА (26.10): просьба в звонке и есть одобрение. ИИ НЕ переспрашивает «вы уверены?» и не просит нажать кнопку.
// Переспрос — только при неразборчивой речи или неоднозначном параметре (две «Колы», число не расслышано), право роли
// проверяется (denied), режим по умолчанию «Выполнять по просьбе». Безопасность: итог озвучивает ПРОГРАММА (`say`), «отмени
// последнее» (undo.dart), журнал `ai_actions.jsonl` (ai_journal.dart), ворота намерения по речи пользователя (intent_gate.dart),
// защита от эха и повтора (tool_registry.dart).
//
// Деньги и остатки идут ТОЛЬКО через существующие репозитории мобилки, с их ключами идемпотентности:
//  - напрямую: OpsRepository (cashFlow, payDebt, hold), StockRepository (postReceiving, applyAudit), ProductEditRepository
//    (save, changeStock), AdminRepository (addBonus, setOrderStatus, deleteProduct), ShiftController (open);
//  - через «ручки» экранов (ai_handles.dart): оплата (`_pay` + доступ к долгу + сверка возвратов), возврат (ReturnScreen),
//    закрытие смены (`_close`: очередь чеков), приёмка с поставщиком, новый товар. Копии их логики здесь нет. Ручки регистрируют
//    экраны (В3б: payment_screen, ops_screens/ReturnScreen, admin_receive, close_shift_screen, new_item), экраны открывает
//    `opener` (screen_opener.dart); нет ручки — инструмент честно отвечает «недоступно» и ничего не делает.
//
// Окружение приложения — интерфейс [ActionHost]: рабочая реализация [RepoActionHost] (провайдеры приложения), в тестах — фейк.
import 'dart:async';

import '../../../features/admin/admin_repository.dart';
import '../../../features/admin/analytics.dart' show ProductSales;
import '../../../features/ops/ops_repository.dart';
import '../../../features/payment/sales_repository.dart' show Client, SalesRepository;
import '../../../features/sale/cart.dart';
import '../../../features/scan/code_lookup.dart';
import '../../../features/shift/shift_repository.dart';
import '../../../features/stock/product_edit_repository.dart';
import '../../../features/stock/stock_repository.dart';
import '../../api/api_client.dart';
import '../../api/session_store.dart';
import '../../db/database.dart';
import '../../providers.dart';
import '../../util/audit.dart';
import '../live_tools.dart';
import 'action_texts.dart';
import 'ai_handles.dart';
import 'ai_journal.dart';
import 'call_table.dart';
import 'intent_gate.dart';
import 'read_tools.dart' show Reader;
import 'recommendations.dart';
import 'screen_registry.dart' show barcodeFromText, foldText, foldTokens, productNameMatchesQuery;
import 'tool_registry.dart';
import 'undo.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Окружение: что нужно действиям от приложения
// ---------------------------------------------------------------------------------------------------------------------

/// Результат открытия экрана с ручкой.
enum OpenResult { ok, notFound, unsupported }

/// Откуда действия берут данные и куда пишут. Рабочая реализация — [RepoActionHost] (те же репозитории и провайдеры, что у
/// экранов), в тестах — фейк. Новых запросов к серверу здесь нет.
abstract class ActionHost {
  // каталог телефона
  Future<List<Product>> findProducts(String query, {int limit = 30});
  Future<List<Product>> allProducts();
  Future<({Product? product, bool unavailable})> findByCode(String code);
  Future<Product?> productById(String id);

  /// Название товара, у которого уже есть этот штрихкод (кроме [productId]); null — свободен.
  Future<String?> barcodeOwner(String productId, String code);

  // корзина
  Cart get cart;
  void restoreCart(Cart cart);
  void addToCart(Product p, double qty);
  void setCartQty(int index, double qty);
  void removeCartLine(int index);
  void setLineDiscount(int index, double sum);
  void setOrderDiscount({double percent = 0, double sum = 0});
  void clearCart();
  Future<void> logDeletion(DeletionRow row);
  Future<List<HeldCart>> heldCarts();
  Future<void> holdCart(Cart cart, String note);
  Future<void> removeHeld(String id);

  // карточка товара (свежая с сервера) и её сохранение — ProductEditRepository
  Future<ProductDraft> loadDraft(Product local);
  Future<void> saveDraft(String id, ProductDraft before, ProductDraft after);

  // склад
  Future<StockChangeResult> changeStock(String id, StockDoc doc, double qty, String note);
  Future<bool> receiveSimple(List<StockLine> lines);
  Future<bool> applyCount(String note, Map<String, double> counted);
  Future<void> deleteProduct(String id);
  Future<List<ProductSales>> productSales(DateTime from, DateTime to);

  // клиенты и заказы
  Future<List<ClientRow>> findClients(String query);
  Future<void> addBonus(String clientId, double delta, String note);
  Future<Client> createClient(String name, String phone);
  Future<List<SiteOrder>> orders();
  Future<void> setOrderStatus(String id, String status);

  // касса и смена
  ShiftInfo? get shift;
  Future<void> openShift(double openingCash);
  Future<void> refreshShift();
  Future<void> cashFlow({required String shiftId, required String cashboxId, required bool withdrawal, required double amount, required String reason});
  Future<List<Debtor>> debtors();
  Future<void> payDebt({required String clientId, required double amount, required String method, String? shiftId});

  // вид приложения
  String get themeMode;
  String get language;
  Future<void> setThemeMode(String mode);
  Future<void> setLanguage(String code);

  /// Открыть экран, у которого есть «ручка» (pay, return, receive, close_shift, new_item). Реализует оболочка приложения (В3б/В4).
  Future<OpenResult> openScreenFor(String handleKey, Map<String, dynamic> args);
}

/// Рабочая реализация: репозитории и провайдеры приложения. Не проверяется юнит-тестами сети (живая проверка — В9).
class RepoActionHost implements ActionHost {
  final AppDatabase db;
  final ApiClient api;
  final AdminRepository admin;
  final OpsRepository ops;
  final StockRepository stock;
  final ProductEditRepository edit;
  final SalesRepository sales;
  final CartController Function() cartController;
  final Cart Function() getCart;
  final ShiftInfo? Function() getShift;
  final ShiftController Function() shiftController;
  final PrefsController Function() prefsController;
  final Prefs Function() getPrefs;
  final Future<List<ProductSales>> Function(DateTime from, DateTime to) salesFn;
  final Future<OpenResult> Function(String handleKey, Map<String, dynamic> args)? opener;

  RepoActionHost({
    required this.db,
    required this.api,
    required this.admin,
    required this.ops,
    required this.stock,
    required this.edit,
    required this.sales,
    required this.cartController,
    required this.getCart,
    required this.getShift,
    required this.shiftController,
    required this.prefsController,
    required this.getPrefs,
    required this.salesFn,
    this.opener,
  });

  /// `RepoActionHost.fromReader(ref.read, productSales: …, opener: …)` — из провайдеров приложения.
  /// [productSales] — отчёт `tab=products` (AnalyticsRepository.products), [opener] — открытие экранов с ручками (В3б).
  factory RepoActionHost.fromReader(
    Reader read, {
    required Future<List<ProductSales>> Function(DateTime from, DateTime to) productSales,
    Future<OpenResult> Function(String handleKey, Map<String, dynamic> args)? opener,
  }) {
    final api = read(apiProvider);
    final db = read(databaseProvider);
    final stockRepo = read(stockRepoProvider);
    return RepoActionHost(
      db: db,
      api: api,
      admin: AdminRepository(api, db),
      ops: read(opsRepoProvider),
      stock: stockRepo,
      edit: ProductEditRepository(api, db, stockRepo),
      sales: read(salesRepoProvider),
      cartController: () => read(cartProvider.notifier),
      getCart: () => read(cartProvider),
      getShift: () => read(shiftProvider).shift,
      shiftController: () => read(shiftProvider.notifier),
      prefsController: () => read(prefsProvider.notifier),
      getPrefs: () => read(prefsProvider),
      salesFn: productSales,
      opener: opener,
    );
  }

  @override
  Future<List<Product>> findProducts(String query, {int limit = 30}) => db.searchProducts(query, limit: limit);
  @override
  Future<List<Product>> allProducts() => db.searchProducts('', limit: 20000);
  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async {
    final r = await CodeLookup.find(db, api, code);
    return (product: r.product, unavailable: r.unavailable);
  }

  @override
  Future<Product?> productById(String id) => db.productById(id);
  @override
  Future<String?> barcodeOwner(String productId, String code) => edit.barcodeOwner(productId, code);

  @override
  Cart get cart => getCart();
  @override
  void restoreCart(Cart cart) => cartController().restore(cart);
  @override
  void addToCart(Product p, double qty) => cartController().add(p, qty: qty);
  @override
  void setCartQty(int index, double qty) => cartController().setQty(index, qty);
  @override
  void removeCartLine(int index) => cartController().remove(index);
  @override
  void setLineDiscount(int index, double sum) => cartController().setLineDiscount(index, sum);
  @override
  void setOrderDiscount({double percent = 0, double sum = 0}) => cartController().setOrderDiscount(percent: percent, sum: sum);
  @override
  void clearCart() => cartController().clear();
  @override
  Future<void> logDeletion(DeletionRow row) => admin.logDeletion(row);
  @override
  Future<List<HeldCart>> heldCarts() => ops.held();
  @override
  Future<void> holdCart(Cart cart, String note) => ops.hold(cart, note);
  @override
  Future<void> removeHeld(String id) => ops.removeHeld(id);

  @override
  Future<ProductDraft> loadDraft(Product local) async => ProductDraft.fromServer(await edit.load(local.id), local);
  @override
  Future<void> saveDraft(String id, ProductDraft before, ProductDraft after) => edit.save(id, before, after, canPrices: true);

  @override
  Future<StockChangeResult> changeStock(String id, StockDoc doc, double qty, String note) => edit.changeStock(id, doc, qty, note);
  @override
  Future<bool> receiveSimple(List<StockLine> lines) => stock.postReceiving(lines);
  @override
  Future<bool> applyCount(String note, Map<String, double> counted) => stock.applyAudit(note, counted);
  @override
  Future<void> deleteProduct(String id) => admin.deleteProduct(id);
  @override
  Future<List<ProductSales>> productSales(DateTime from, DateTime to) => salesFn(from, to);

  @override
  Future<List<ClientRow>> findClients(String query) => admin.clients(query);
  @override
  Future<void> addBonus(String clientId, double delta, String note) => admin.addBonus(clientId, delta, note);
  @override
  Future<Client> createClient(String name, String phone) => sales.createClient(name, phone);
  @override
  Future<List<SiteOrder>> orders() => admin.orders();
  @override
  Future<void> setOrderStatus(String id, String status) => admin.setOrderStatus(id, status);

  @override
  ShiftInfo? get shift => getShift();
  @override
  Future<void> openShift(double openingCash) => shiftController().open(openingCash);
  @override
  Future<void> refreshShift() => shiftController().refresh();
  @override
  Future<void> cashFlow({required String shiftId, required String cashboxId, required bool withdrawal, required double amount, required String reason}) =>
      ops.cashFlow(shiftId: shiftId, cashboxId: cashboxId, withdrawal: withdrawal, amount: amount, reason: reason);
  @override
  Future<List<Debtor>> debtors() => ops.debtors();
  @override
  Future<void> payDebt({required String clientId, required double amount, required String method, String? shiftId}) async {
    await ops.payDebt(clientId: clientId, amount: amount, method: method, shiftId: shiftId);
  }

  @override
  String get themeMode => getPrefs().mode;
  @override
  String get language => getPrefs().locale.languageCode;
  @override
  Future<void> setThemeMode(String mode) => prefsController().setMode(mode);
  @override
  Future<void> setLanguage(String code) => prefsController().setLocale(code);

  @override
  Future<OpenResult> openScreenFor(String handleKey, Map<String, dynamic> args) async =>
      opener == null ? OpenResult.unsupported : await opener!(handleKey, args);
}

// ---------------------------------------------------------------------------------------------------------------------
// Общее для инструментов
// ---------------------------------------------------------------------------------------------------------------------

/// Всё, что нужно инструментам-действиям: окружение, стек отмены, журнал, ручки экранов, таблицы звонка.
class ActionKit {
  final ActionHost host;
  final UndoStack undo;
  final AiJournal? journal;
  final AiScreenHandles handles;
  final TableStore? tables;

  /// Имя модели звонка — в журнал (не ключ).
  final String? model;

  /// Реестр, в который зарегистрированы инструменты: после «отмени» он забывает недавние действия.
  ToolRegistry? registry;
  final DateTime Function() now;

  /// Максимум товаров в одной массовой наценке (за раз; дольше — звонок ждёт ответа инструмента).
  final int bulkMax;

  /// Сколько ждать регистрацию «ручки» открытого экрана (раздел 3.5 ТЗ — до 2 с).
  final Duration handleTimeout;
  ActionKit({
    required this.host,
    UndoStack? undo,
    this.journal,
    AiScreenHandles? handles,
    this.tables,
    this.model,
    DateTime Function()? now,
    this.bulkMax = 40,
    this.handleTimeout = const Duration(seconds: 2),
  }) : undo = undo ?? UndoStack(),
       handles = handles ?? AiScreenHandles(),
       now = now ?? DateTime.now;

  Future<void> _lock = Future.value();

  /// Денежные операции идут по одной: вторая ждёт первую (звонок мог прислать два вызова подряд).
  Future<T> serial<T>(Future<T> Function() f) {
    final r = _lock.then((_) => f());
    _lock = r.then((_) {}, onError: (_) {});
    return r;
  }

  /// «Отменить» с экрана (кнопка под плашкой) — то же, что голосом «отмени последнее».
  Future<String> undoLast(Session session, String lang, {int n = 1}) async {
    final res = await _undoSteps(this, session, lang, n);
    return res.say;
  }

  /// Записать выполненное действие: стек отмены + журнал. Вызывается инструментом после успеха (или неопределённого исхода).
  Future<void> commit(
    ToolRequest r, {
    required String tool,
    required String say,
    required String Function(String lang) label,
    Map<String, dynamic> data = const {},
    Future<UndoResult> Function(String lang)? undoFn,
    String Function(String lang)? whyNot,
    double? amount,
    String status = 'ok',
  }) async {
    final env = r.env;
    final entry = UndoEntry(id: r.call.id, userId: env.session.userId, at: env.now(), tool: tool, label: label, data: data, undo: undoFn, whyNot: whyNot);
    undo.push(entry);
    final j = journal;
    if (j != null) {
      await j.record(
        AiActionRecord(
          at: env.now(),
          user: env.session.userId.isEmpty ? Audit.currentUser : env.session.userId,
          tool: tool,
          callId: r.call.id,
          said: env.speech.last?.text ?? '',
          args: r.args,
          status: status,
          say: say,
          amount: amount,
          reversible: entry.reversible,
          undo: data,
          mode: env.mode.wire,
          model: model,
        ),
      );
    }
  }
}

num? _num(Object? v) => v is num ? v : (v is String ? num.tryParse(v.trim().replaceAll(',', '.').replaceAll(RegExp(r'\s'), '')) : null);
double? _dbl(Object? v) => _num(v)?.toDouble();
String _s(Object? v) => v == null ? '' : '$v'.trim();
int? _int(Object? v) => _num(v)?.toInt();

String _clip(String s, [int n = 40]) => s.length > n ? '${s.substring(0, n - 1)}…' : s;
String _sp(String s) => s.replaceAll(' ', ' ').replaceAll(' ', ' ');
String _m(num v, String lang) => _sp(CallTable.formatCell(v, CellKind.money, lang));
String _q(num v, String lang, [String? unit]) => _sp(CallTable.formatCell(v, CellKind.qty, lang, unit: unit));
String _pct(num v, String lang) => _sp(CallTable.formatCell(v, CellKind.percent, lang, signed: false));

ToolResult _fail(ToolEnv env, String key, [Map<String, String> p = const {}, ToolStatus status = ToolStatus.clarify]) =>
    ToolResult(status, am(key, env.lang, p), screen: env.screen.isEmpty ? null : env.screen);

ToolResult _denied(ToolEnv env, String needs) => ToolResult(ToolStatus.denied, '${toolMsg('denied', env.lang)} ($needs)');

/// Ошибка сервера в ответ инструмента. Сбой связи: деньги — «могло пройти, повтор безопасен (тот же ключ)», прочее — «не выполнено».
ToolResult _apiError(ToolEnv env, Object e, {bool money = false}) {
  final lang = env.lang;
  if (e is StockBelowZeroException) return ToolResult(ToolStatus.error, am('belowZero', lang, {'cur': _q(e.current, lang)}));
  if (e is ApiException) {
    if (e.status == 403) return ToolResult(ToolStatus.denied, toolMsg('denied', lang));
    if (e.isTransient) {
      return ToolResult(
        ToolStatus.offline,
        am(money ? 'unknownRetry' : 'offline', lang),
      );
    }
    return ToolResult(ToolStatus.error, e.message.isEmpty ? toolMsg('error', lang) : e.message);
  }
  return ToolResult(ToolStatus.error, toolMsg('error', lang));
}

Future<ToolResult> _guard(ToolEnv env, Future<ToolResult> Function() f, {bool money = false}) async {
  try {
    return await f();
  } on ApiException catch (e) {
    return _apiError(env, e, money: money);
  }
}

/// Подключить инструменты-действия к реестру звонка. Возвращает [ActionKit] (стек отмены и журнал) — его держит звонок.
ActionKit registerActionTools(ToolRegistry registry, ActionKit kit) {
  kit.registry = registry;
  for (final t in buildActionTools(kit)) {
    registry.register(t);
  }
  return kit;
}

// ---------------------------------------------------------------------------------------------------------------------
// Поиск товара: одно и то же для корзины, цен, списания…
// ---------------------------------------------------------------------------------------------------------------------

typedef _ProductPick = ({Product? product, ToolResult? stop});

Future<_ProductPick> _pickProduct(ActionKit k, ToolRequest r, {String? query, String? code, Object? pick}) async {
  final env = r.env;
  final lang = env.lang;
  var barcode = _s(code);
  var q = _s(query);
  if (barcode.isNotEmpty) barcode = barcodeFromText(barcode) ?? barcode.replaceAll(RegExp(r'\s+'), '');
  if (barcode.isEmpty && q.isNotEmpty) {
    final b = barcodeFromText(q);
    if (b != null) {
      barcode = b;
      q = '';
    }
  }
  if (barcode.isEmpty && q.isEmpty) return (product: null, stop: _fail(env, 'needProduct'));
  if (barcode.isNotEmpty) {
    final f = await k.host.findByCode(barcode);
    if (f.product != null) return (product: f.product, stop: null);
    return (product: null, stop: f.unavailable ? _fail(env, 'offlineCode', const {}, ToolStatus.offline) : _fail(env, 'productNone', const {}, ToolStatus.notFound));
  }
  final raw = await k.host.findProducts(q, limit: 30);
  // Слова запроса — начала слов названия («нан» не находит «Финансы»); если так не осталось ничего (поиск мог сработать по
  // артикулу) — оставляем то, что нашла база.
  final strict = raw.where((p) => productNameMatchesQuery(p.name, q)).toList();
  var hits = strict.isEmpty ? raw : strict;
  final seen = <String>{};
  hits = [
    for (final p in hits)
      if (seen.add(p.id)) p,
  ]..sort((a, b) {
      final c = foldText(a.name).compareTo(foldText(b.name));
      return c != 0 ? c : a.id.compareTo(b.id);
    });
  if (hits.isEmpty) return (product: null, stop: _fail(env, 'productNone', const {}, ToolStatus.notFound));
  final pk = _int(pick);
  if (pk != null && pk >= 1 && pk <= hits.length) return (product: hits[pk - 1], stop: null);
  if (hits.length == 1) return (product: hits.first, stop: null);
  // Название сказано целиком и совпало ровно с одним товаром — вопрос не нужен.
  final qn = foldTokens(q).join(' ');
  final exact = hits.where((p) => foldTokens(p.name).join(' ') == qn).toList();
  if (exact.length == 1) return (product: exact.first, stop: null);
  // Неоднозначно: варианты с номерами — таблицей на экране и коротко моделью.
  final shown = hits.take(8).toList();
  if (k.tables != null) {
    final t = CallTable.build(
      id: k.tables!.nextId(),
      title: tr5(lang, 'Какой товар?', 'Кайсы товар?', 'Qaysi mahsulot?', 'Which product?', 'Hangi ürün?'),
      subtitle: '«${_clip(q, 24)}»',
      columns: [
        CallColumn('n', '№', CellKind.text, numeric: false),
        CallColumn('name', tr5(lang, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'), CellKind.text),
        CallColumn('price', tr5(lang, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'), CellKind.money),
      ],
      rows: [
        for (var i = 0; i < shown.length; i++) <Object?>['${i + 1}', shown[i].name, shown[i].price],
      ],
      refs: [for (final p in shown) p.id],
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'product',
    );
    k.tables!.add(t);
  }
  final list = [for (var i = 0; i < shown.length && i < 5; i++) '${i + 1} — ${_clip(shown[i].name, 30)}, ${_m(shown[i].price, lang)}'].join('; ');
  final tooMany = hits.length > shown.length;
  return (
    product: null,
    stop: ToolResult(
      ToolStatus.ambiguous,
      am(tooMany ? 'productTooMany' : 'productMany', lang, {'list': list, 'n': '${hits.length}'}),
      options: [
        for (var i = 0; i < shown.length && i < 5; i++) {'n': i + 1, 'title': _clip(shown[i].name, 40), 'price': shown[i].price},
      ],
      data: {'total': hits.length},
      tableId: k.tables?.current?.id,
      screen: env.screen.isEmpty ? null : env.screen,
    ),
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Объявления инструментов. Описания короткие (по-английски, с примерами слов): все объявления звонка — ориентир ≤ 8 тыс. симв.
// ---------------------------------------------------------------------------------------------------------------------

Map<String, dynamic> _query() => sStr(desc: 'Product name as said');
Map<String, dynamic> _pick() => sInt(desc: '1-based option number after ambiguous');
Map<String, dynamic> _linesSchema(String qtyKey) => sArr(sObj({'query': sStr(), 'barcode': sStr(), qtyKey: sNum(), 'pick': sInt()}, required: [qtyKey]));

List<double> _lineValues(Object? lines, List<String> keys) {
  final out = <double>[];
  if (lines is! List) return out;
  for (final l in lines) {
    if (l is! Map) continue;
    for (final k in keys) {
      final v = _dbl(l[k]);
      if (v != null) out.add(v);
    }
  }
  return out;
}

/// Все инструменты-действия.
List<AssistantTool> buildActionTools(ActionKit k) {
  bool products(Session s) => s.can(Session.viewProducts);
  bool clients(Session s) => s.can(Session.viewClients);
  return [
    _cartEdit(k),
    _holdCart(k),
    _handleTool(
      k,
      name: 'checkout',
      handleKey: 'pay',
      screenTitle: 'Payment',
      group: ToolGroup.money,
      words: Intents.pay,
      description:
          'Pay the CURRENT cart as on the Payment screen. The amount is the cart total computed by the app; never invent it. ru: оплата, пробей, рассчитай; ky: төлө.',
      params: sObj({
        'method': sStr(values: ['cash', 'card', 'qr', 'debt']),
        'client': sStr(desc: 'Client name; required for debt'),
        'cash_given': sNum(desc: 'Cash the customer gave'),
        'due_date': sStr(desc: 'YYYY-MM-DD, debt due date'),
      }, required: ['method']),
      numeric: const ['cash_given'],
      objects: const ['client'],
      prepare: _prepareCheckout,
      done: _checkoutDone,
    ),
    _handleTool(
      k,
      name: 'return_sale',
      handleKey: 'return',
      screenTitle: 'Return',
      group: ToolGroup.money,
      words: Intents.refund,
      allowed: (s) => s.can(Session.employeeReturn),
      needs: 'Возврат',
      description: 'Refund a sold receipt (whole or some lines) on the Return screen. The receipt number is REQUIRED. ru: верни деньги за чек 1043; ky: кайтар.',
      params: sObj({
        'check_number': sStr(desc: 'Receipt number'),
        'lines': sArr(sObj({'query': sStr(), 'qty': sNum()}), desc: 'Only these lines; omit for the whole receipt'),
        'reason': sStr(),
      }, required: ['check_number']),
      numeric: const ['check_number'],
      prepare: _prepareReturn,
      done: _returnDone,
    ),
    _receiveSimple(k, products),
    _handleTool(
      k,
      name: 'receive_from_supplier',
      handleKey: 'receive',
      screenTitle: 'Supplier receiving',
      group: ToolGroup.money,
      words: Intents.receive,
      allowed: (s) => s.canProcure && s.can(Session.viewProducts),
      needs: 'Закупки',
      description:
          'Post a receipt from a supplier (price never below the current one, min markup 20%). ru: прими от поставщика 5 ящиков по 300; ky: кабыл ал.',
      params: sObj({
        'supplier': sStr(),
        'lines': sArr(sObj({'query': sStr(), 'qty': sNum(), 'cost': sNum(), 'price': sNum()}, required: ['qty'])),
        'pay': sStr(values: ['debt', 'cash']),
      }, required: ['lines']),
      objects: const ['supplier'],
      values: (a) => _lineValues(a['lines'], const ['qty', 'cost', 'price']),
      prepare: _prepareSupplier,
      done: _supplierDone,
    ),
    _writeOff(k, products),
    _stockCount(k, products),
    _setPrice(k),
    _bulkMarkup(k),
    _productEdit(k),
    _handleTool(
      k,
      name: 'create_product',
      handleKey: 'new_item',
      screenTitle: 'New product',
      group: ToolGroup.action,
      words: Intents.productCreate,
      allowed: products,
      needs: 'Склад',
      description: 'Create a new product on the New product screen. ru: создай товар Вода 25 сом; ky: жаңы товар.',
      params: sObj({
        'name': sStr(),
        'price': sNum(),
        'purchase': sNum(),
        'barcode': sStr(),
        'unit': sStr(),
        'category': sStr(),
      }, required: ['name', 'price']),
      numeric: const ['price', 'purchase', 'barcode'],
      objects: const ['name'],
      prepare: _prepareCreateProduct,
      done: _createProductDone,
    ),
    _deleteProduct(k),
    _cashInOut(k),
    _payDebt(k),
    _openShift(k),
    _handleTool(
      k,
      name: 'close_shift',
      handleKey: 'close_shift',
      screenTitle: 'Close shift',
      group: ToolGroup.money,
      words: Intents.shiftClose,
      description: 'Close the shift. counted_cash (cash in the drawer) is REQUIRED; ask if the user did not say it. ru: закрой смену; ky: сменаны жап.',
      params: sObj({'counted_cash': sNum(desc: 'Cash counted in the drawer')}),
      numeric: const ['counted_cash'],
      prepare: _prepareCloseShift,
      done: _closeShiftDone,
    ),
    _addBonus(k, clients),
    _createClient(k),
    _setOrderStatus(k),
    _uiSettings(k),
    _undoLast(k),
    _lastActions(k),
  ];
}

// ---------------------------------------------------------------------------------------------------------------------
// Корзина
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _cartEdit(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'cart_edit',
    "Edit the current sale cart. op: add | set_qty (of a line) | remove (a line) | discount (percent or sum, for a line or the whole cart; 0 removes it) | clear. Only on the user's own request. ru: добавь, убери, скидка, очисти; ky: кош, алып сал, арзандат.",
    parameters: sObj({
      'op': sStr(values: ['add', 'set_qty', 'remove', 'discount', 'clear']),
      'query': _query(),
      'barcode': sStr(),
      'pick': _pick(),
      'line': sInt(desc: '1-based cart line number'),
      'qty': sNum(),
      'percent': sNum(),
      'sum': sNum(desc: 'Discount in som'),
      'reason': sStr(desc: 'Why a line is removed'),
    }, required: ['op']),
  ),
  group: ToolGroup.action,
  perTurnDedupe: true,
  numericDefaults: const {'qty': 1, 'percent': 0, 'sum': 0},
  gateFor: (a) => switch ('${a['op']}') {
    'add' => const [GateReq(words: Intents.cartAdd, numeric: ['qty'])],
    'set_qty' => const [GateReq(words: Intents.cartQty, numeric: ['qty'])],
    'remove' => const [GateReq(words: Intents.cartRemove)],
    'discount' => const [GateReq(words: Intents.cartDiscount, numeric: ['percent', 'sum'])],
    'clear' => const [GateReq(words: Intents.cartClear)],
    _ => const [GateReq()],
  },
  run: (r) => _guard(r.env, () => _cartRun(k, r)),
);

/// Строка чека по номеру (`line`) или названию (`query`). Неоднозначно — ответ с номерами строк.
({int? index, ToolResult? stop}) _cartLine(ActionKit k, ToolRequest r) {
  final env = r.env;
  final cart = k.host.cart;
  if (cart.isEmpty) return (index: null, stop: _fail(env, 'emptyCart'));
  final n = _int(r.args['line']);
  if (n != null) {
    if (n >= 1 && n <= cart.lines.length) return (index: n - 1, stop: null);
    return (index: null, stop: _fail(env, 'noLine'));
  }
  final q = _s(r.args['query']);
  if (q.isEmpty) {
    if (cart.lines.length == 1) return (index: 0, stop: null);
    return (index: null, stop: _fail(env, 'needLine'));
  }
  final hits = [
    for (var i = 0; i < cart.lines.length; i++)
      if (looseNameMatch(cart.lines[i].name, q) || productNameMatchesQuery(cart.lines[i].name, q)) i,
  ];
  if (hits.isEmpty) return (index: null, stop: _fail(env, 'noLine', const {}, ToolStatus.notFound));
  if (hits.length == 1) return (index: hits.first, stop: null);
  final lang = env.lang;
  return (
    index: null,
    stop: ToolResult(
      ToolStatus.ambiguous,
      am('manyLines', lang, {'n': '${hits.length}'}),
      options: [
        for (final i in hits.take(5)) {'n': i + 1, 'title': _clip(cart.lines[i].name, 40), 'qty': cart.lines[i].qty},
      ],
    ),
  );
}

Future<ToolResult> _cartRun(ActionKit k, ToolRequest r) async {
  final env = r.env;
  final lang = env.lang;
  final host = k.host;
  final op = _s(r.args['op']);
  final before = cloneCart(host.cart);

  /// Откат снимком; если чек менялся после ИИ (вручную) — не затираем чужую правку.
  Future<UndoResult> restoreTo(String afterSig, Cart snapshot) async {
    if (cartSignature(host.cart) != afterSig) return UndoResult.conflict;
    host.restoreCart(cloneCart(snapshot));
    return UndoResult.ok;
  }

  Future<ToolResult> finish(String say, String Function(String) label, {double? amount}) async {
    final sig = cartSignature(host.cart);
    await k.commit(
      r,
      tool: 'cart_edit',
      say: say,
      label: label,
      data: {'op': op, 'lines_before': before.count, 'lines_after': host.cart.count, 'total': host.cart.total},
      undoFn: (_) => restoreTo(sig, before),
      amount: amount,
    );
    return ToolResult(ToolStatus.ok, say, undo: true, data: {'count': host.cart.count, 'total': host.cart.total}, screen: env.screen.isEmpty ? null : env.screen);
  }

  switch (op) {
    case 'add':
      {
        final qty = _dbl(r.args['qty']) ?? 1;
        if (qty <= 0) return _fail(env, 'needQty');
        final pp = await _pickProduct(k, r, query: _s(r.args['query']), code: _s(r.args['barcode']), pick: r.args['pick']);
        if (pp.stop != null) return pp.stop!;
        final p = pp.product!;
        if (!p.isWeight && qty != qty.roundToDouble()) return _fail(env, 'qtyWhole');
        host.addToCart(p, qty);
        final line = host.cart.lines.lastWhere((l) => l.productId == p.id, orElse: () => host.cart.lines.last);
        final say = am('added', lang, {
          'name': _clip(p.name, 40),
          'qty': _q(qty, lang, p.unit),
          'sum': _m(qty * line.price, lang),
          'count': '${host.cart.count}',
          'total': _m(host.cart.total, lang),
        });
        return finish(say, (l) => '${_clip(p.name, 30)} × ${_q(qty, l)}', amount: qty * line.price);
      }
    case 'set_qty':
      {
        final qty = _dbl(r.args['qty']);
        if (qty == null) return _fail(env, 'needQty');
        if (qty <= 0) return _fail(env, 'qtyZero');
        final ln = _cartLine(k, r);
        if (ln.stop != null) return ln.stop!;
        final line = host.cart.lines[ln.index!];
        if (!line.isWeight && qty != qty.roundToDouble()) return _fail(env, 'qtyWhole');
        host.setCartQty(ln.index!, qty);
        final say = am('qtySet', lang, {'name': _clip(line.name, 40), 'qty': _q(qty, lang, line.unit), 'total': _m(host.cart.total, lang)});
        return finish(say, (l) => '${_clip(line.name, 30)}: ${_q(qty, l)}');
      }
    case 'remove':
      {
        if (!env.session.can(Session.deleteLine)) return _denied(env, 'Удаление из чека');
        final ln = _cartLine(k, r);
        if (ln.stop != null) return ln.stop!;
        final line = host.cart.lines[ln.index!];
        final reason = _s(r.args['reason']);
        final sum = line.qty * line.price - line.discount;
        // Как ручное удаление строки (sale_screen): запись в «Удалено из чека» с пометкой «ИИ» и в журнал аудита.
        final why = 'ИИ${reason.isEmpty ? '' : ': $reason'}';
        await host.logDeletion(DeletionRow(env.now(), line.name, line.qty, sum, why, env.session.userName));
        Audit.log('delete', '${line.name} × ${line.qty} · $why', amount: sum);
        host.removeCartLine(ln.index!);
        final say = am('removed', lang, {'name': _clip(line.name, 40), 'total': _m(host.cart.total, lang)});
        return finish(say, (l) => '− ${_clip(line.name, 30)}', amount: sum);
      }
    case 'discount':
      {
        if (!env.session.can(Session.discount)) return _denied(env, 'Скидка');
        if (host.cart.isEmpty) return _fail(env, 'emptyCart');
        final percent = _dbl(r.args['percent']) ?? 0;
        final sum = _dbl(r.args['sum']) ?? 0;
        if (percent < 0 || sum < 0 || percent > 100) return _fail(env, 'needDiscount');
        final hasLine = r.args['line'] != null || _s(r.args['query']).isNotEmpty;
        if (hasLine) {
          final ln = _cartLine(k, r);
          if (ln.stop != null) return ln.stop!;
          final line = host.cart.lines[ln.index!];
          if (line.hasPromo) return _fail(env, 'promoNoDiscount', {'name': _clip(line.name, 40)});
          final value = percent > 0 ? roundMoney(line.gross * percent / 100) : sum;
          host.setLineDiscount(ln.index!, value);
          final shown = percent > 0 ? _pct(percent, lang) : _m(sum, lang);
          final say = value <= 0
              ? am('discountOff', lang, {'total': _m(host.cart.total, lang)})
              : am('discountLine', lang, {'name': _clip(line.name, 40), 'what': shown, 'total': _m(host.cart.total, lang)});
          Audit.log('discount', 'ИИ: ${line.name}', amount: value);
          return finish(say, (l) => '${tr5(l, 'скидка', 'арзандатуу', 'chegirma', 'discount', 'indirim')} ${_clip(line.name, 24)}', amount: value);
        }
        host.setOrderDiscount(percent: percent, sum: percent > 0 ? 0 : sum);
        final off = percent <= 0 && sum <= 0;
        final shown = percent > 0 ? _pct(percent, lang) : _m(sum, lang);
        final say = off
            ? am('discountOff', lang, {'total': _m(host.cart.total, lang)})
            : am('discountOrder', lang, {'what': shown, 'total': _m(host.cart.total, lang)});
        Audit.log('discount', 'ИИ: чек', amount: host.cart.orderDiscount);
        return finish(say, (l) => tr5(l, 'скидка на чек', 'чекке арзандатуу', 'chekka chegirma', 'cart discount', 'fiş indirimi'), amount: host.cart.orderDiscount);
      }
    case 'clear':
      {
        if (!env.session.can(Session.deleteLine)) return _denied(env, 'Удаление из чека');
        if (host.cart.isEmpty) return _fail(env, 'emptyCart');
        final n = host.cart.count;
        host.clearCart();
        return finish(am('cleared', lang, {'count': '$n'}), (l) => tr5(l, 'очистка чека', 'чекти тазалоо', 'chekni tozalash', 'cart cleared', 'fişi temizleme'));
      }
  }
  return _fail(env, 'notRequestedOp');
}

AssistantTool _holdCart(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'hold_cart',
    'Hold the cart for later (op hold) or bring a held one back (op restore). ru: отложи чек, верни отложенный.',
    parameters: sObj({
      'op': sStr(values: ['hold', 'restore']),
      'note': sStr(),
      'n': sInt(desc: '1-based held cart number, newest first'),
    }, required: ['op']),
  ),
  group: ToolGroup.action,
  gateFor: (a) => switch ('${a['op']}') {
    'hold' => const [GateReq(words: Intents.hold)],
    'restore' => const [GateReq(words: Intents.restore)],
    _ => const [GateReq()],
  },
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final host = k.host;
    final op = _s(r.args['op']);
    if (op == 'hold') {
      if (host.cart.isEmpty) return _fail(env, 'emptyCart');
      final snap = cloneCart(host.cart);
      final note = _s(r.args['note']);
      await host.holdCart(snap, note);
      final held = await host.heldCarts();
      final heldId = held.isEmpty ? null : held.first.id;
      host.clearCart();
      final say = am('held', lang, {'note': note.isEmpty ? '' : ' («$note»)', 'count': '${snap.count}', 'total': _m(snap.total, lang)});
      await k.commit(
        r,
        tool: 'hold_cart',
        say: say,
        label: (l) => tr5(l, 'отложила чек', 'чекти кое турдум', 'chekni qoldirdim', 'held the cart', 'fişi askıya aldım'),
        data: {'op': 'hold', 'lines': snap.count, 'total': snap.total},
        undoFn: (_) async {
          if (!host.cart.isEmpty) return UndoResult.conflict;
          if (heldId != null) await host.removeHeld(heldId);
          host.restoreCart(cloneCart(snap));
          return UndoResult.ok;
        },
        amount: snap.total,
      );
      return ToolResult(ToolStatus.ok, say, undo: true);
    }
    if (op == 'restore') {
      final held = await host.heldCarts();
      if (held.isEmpty) return _fail(env, 'noHeld', const {}, ToolStatus.notFound);
      if (!host.cart.isEmpty) return _fail(env, 'cartNotEmpty');
      final n = _int(r.args['n']);
      HeldCart? h;
      if (n != null && n >= 1 && n <= held.length) {
        h = held[n - 1];
      } else if (held.length == 1) {
        h = held.first;
      }
      if (h == null) {
        return ToolResult(
          ToolStatus.ambiguous,
          am('manyHeld', lang, {'n': '${held.length}'}),
          options: [
            for (var i = 0; i < held.length && i < 5; i++) {'n': i + 1, 'lines': held[i].cart.count, 'total': held[i].cart.total, 'note': _clip(held[i].note, 24)},
          ],
        );
      }
      final target = h;
      host.restoreCart(cloneCart(target.cart));
      await host.removeHeld(target.id);
      final sig = cartSignature(host.cart);
      final say = am('restored', lang, {'count': '${target.cart.count}', 'total': _m(target.cart.total, lang)});
      await k.commit(
        r,
        tool: 'hold_cart',
        say: say,
        label: (l) => tr5(l, 'вернула отложенный чек', 'кечиктирилген чекти кайтардым', 'qoldirilgan chekni qaytardim', 'restored a held cart', 'bekleyen fişi getirdim'),
        data: {'op': 'restore', 'lines': target.cart.count, 'total': target.cart.total},
        undoFn: (_) async {
          if (cartSignature(host.cart) != sig) return UndoResult.conflict;
          await host.holdCart(cloneCart(target.cart), target.note);
          host.restoreCart(const Cart());
          return UndoResult.ok;
        },
        amount: target.cart.total,
      );
      return ToolResult(ToolStatus.ok, say, undo: true);
    }
    return _fail(env, 'notRequestedOp');
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Деньги через «ручки» экранов: оплата, возврат, закрытие смены, приёмка с поставщиком, новый товар
// ---------------------------------------------------------------------------------------------------------------------

typedef _Prepare = FutureOr<ToolResult?> Function(ActionKit k, ToolRequest r);
typedef _Done = Future<ToolResult> Function(ActionKit k, ToolRequest r, HandleOutcome o);

AssistantTool _handleTool(
  ActionKit k, {
  required String name,
  required String handleKey,
  required String screenTitle,
  required ToolGroup group,
  required List<String> words,
  required String description,
  required Map<String, dynamic> params,
  required _Prepare prepare,
  required _Done done,
  bool Function(Session)? allowed,
  String? needs,
  List<String> numeric = const [],
  List<String> objects = const [],
  List<double> Function(Map<String, dynamic>)? values,
}) => AssistantTool(
  decl: ToolDecl(name, description, parameters: params),
  group: group,
  allowed: allowed,
  needs: needs,
  intentWords: words,
  numericArgs: numeric,
  objectArgs: objects,
  gateFor: (a) => [
    GateReq(words: words, numeric: numeric, objects: objects, values: {
      if (values != null)
        for (final (i, v) in values(a).indexed) 'line$i': v,
    }),
  ],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final pre = await prepare(k, r);
    if (pre != null) return pre;
    final opened = await k.host.openScreenFor(handleKey, r.args);
    if (opened == OpenResult.notFound) return _fail(env, name == 'return_sale' ? 'checkNone' : 'productNone', const {}, ToolStatus.notFound);
    if (opened == OpenResult.unsupported) return _noHandle(env, screenTitle);
    final h = await k.handles.waitFor(handleKey, timeout: k.handleTimeout);
    if (h == null) return _noHandle(env, screenTitle);
    final ask = await h.fill(r.args);
    if (ask != null) return _outcomeResult(env, ask);
    final o = await h.run();
    if (o.status == ToolStatus.ok) return done(k, r, o);
    return _outcomeResult(env, o);
  }, ), money: true),
);

ToolResult _noHandle(ToolEnv env, String screen) => ToolResult(ToolStatus.error, am('noHandle', env.lang, {'screen': screen}), data: {'unavailable': true, 'needs': 'handle'});

/// Ответ экрана, не `ok`: вопрос (clarify), неизвестный исход, ошибка.
ToolResult _outcomeResult(ToolEnv env, HandleOutcome o) {
  // 2026-10-26 (голос-управление, В3б): раньше тут было «не повторяю, проверьте на экране» — на деле репозиторий повторяет
  // операцию ТЕМ ЖЕ ключом и второй раз деньги не проведёт, поэтому ИИ честно предлагает повторить просьбу.
  if (o.unknown) return ToolResult(ToolStatus.error, am('unknownMoney', env.lang), data: {'unknown_outcome': true});
  final key = o.askKey;
  final text = key == null
      ? o.ask
      : am(key, env.lang, {
          // число в тексте ручки: имя оканчивается на «Qty» — количество, иначе деньги (на языке звонка)
          for (final e in o.askParams.entries) e.key: e.value is num ? (e.key.endsWith('Qty') ? _q(e.value as num, env.lang) : _m(e.value as num, env.lang)) : '${e.value}',
        });
  return ToolResult(o.status, text == null || text.isEmpty ? toolMsg('error', env.lang) : text);
}

String _methodWord(String m, String lang) => am(switch (m) {
  'card' || 'transfer' => 'm.card',
  'debt' => 'm.debt',
  'qr' => 'm.qr',
  _ => 'm.cash',
}, lang);

ToolResult? _prepareCheckout(ActionKit k, ToolRequest r) {
  final env = r.env;
  final cart = k.host.cart;
  if (cart.isEmpty) return _fail(env, 'emptyForPay');
  if (k.host.shift == null) return _fail(env, 'noShift');
  final method = _s(r.args['method']);
  if (method == 'debt' && _s(r.args['client']).isEmpty) return _fail(env, 'needDebtClient');
  final given = _dbl(r.args['cash_given']);
  if (method == 'cash' && given != null && given + 0.004 < cart.total) {
    return ToolResult(
      ToolStatus.clarify,
      tr5(
        env.lang,
        'Дали ${_m(given, 'ru')}, а чек ${_m(cart.total, 'ru')} — не хватает ${_m(cart.total - given, 'ru')}. Скажите верную сумму.',
        'Берилди ${_m(given, 'ky')}, чек ${_m(cart.total, 'ky')} — ${_m(cart.total - given, 'ky')} жетишпейт. Туура сумманы айтыңыз.',
        'Berildi ${_m(given, 'uz')}, chek ${_m(cart.total, 'uz')} — ${_m(cart.total - given, 'uz')} yetmaydi. To\'g\'ri summani ayting.',
        'Given ${_m(given, 'en')}, the cart is ${_m(cart.total, 'en')} — ${_m(cart.total - given, 'en')} short. Say the right amount.',
        'Verilen ${_m(given, 'tr')}, fiş ${_m(cart.total, 'tr')} — ${_m(cart.total - given, 'tr')} eksik. Doğru tutarı söyleyin.',
      ),
    );
  }
  return null;
}

Future<ToolResult> _checkoutDone(ActionKit k, ToolRequest r, HandleOutcome o) async {
  final env = r.env;
  final lang = env.lang;
  final d = o.data;
  final method = _s(r.args['method']);
  final total = _dbl(d['total']) ?? 0;
  final change = _dbl(d['change']) ?? 0;
  final queued = d['queued'] == true;
  final number = _s(d['number']);
  final saleId = _s(d['sale_id']);
  final shiftId = k.host.shift?.id;
  final say = am(queued ? 'payQueued' : 'payDone', lang, {
    'method': _methodWord(method, lang),
    'total': _m(total, lang),
    'number': number.isEmpty ? '—' : '№$number',
    'change': change > 0 ? am('payChange', lang, {'change': _m(change, lang)}) : '',
  });
  // Отмена оплаты — полный возврат чека (только при праве «Возврат», в той же смене, через экран возврата).
  final canRefund = !queued && saleId.isNotEmpty && method != 'debt';
  await k.commit(
    r,
    tool: 'checkout',
    say: say,
    label: (l) => '${tr5(l, 'оплата', 'төлөм', "to'lov", 'payment', 'ödeme')} ${_m(total, l)}',
    data: {'sale': saleId, 'number': number, 'total': total, 'method': method, 'queued': queued},
    undoFn: canRefund
        ? (l) async {
            if (!env.session.can(Session.employeeReturn)) return UndoResult(UndoStatus.failed, '${toolMsg('denied', l)} (Возврат)');
            if (k.host.shift?.id != shiftId) return UndoResult(UndoStatus.failed, am('why.payNoHandle', l));
            return k.serial(() async {
              final opened = await k.host.openScreenFor('return', {'check_number': number, 'sale_id': saleId});
              final h = opened == OpenResult.ok ? await k.handles.waitFor('return', timeout: k.handleTimeout) : null;
              if (h == null) return UndoResult(UndoStatus.failed, am('why.payNoHandle', l));
              final ask = await h.fill({'check_number': number, 'sale_id': saleId, 'whole': true});
              if (ask != null) return UndoResult(UndoStatus.failed, am('why.payNoHandle', l));
              final out = await h.run();
              return out.status == ToolStatus.ok ? UndoResult.ok : UndoResult(UndoStatus.failed, am('why.payNoHandle', l));
            });
          }
        : null,
    whyNot: canRefund ? null : (l) => am(queued ? 'why.payNoHandle' : (method == 'debt' ? 'why.debt' : 'why.payNoHandle'), l),
    amount: total,
  );
  return ToolResult(ToolStatus.ok, say, undo: canRefund, data: {'number': number, 'total': total, 'change': change}, screen: env.screen.isEmpty ? null : env.screen);
}

ToolResult? _prepareReturn(ActionKit k, ToolRequest r) {
  if (_s(r.args['check_number']).isEmpty) return _fail(r.env, 'needCheck');
  return null;
}

Future<ToolResult> _returnDone(ActionKit k, ToolRequest r, HandleOutcome o) async {
  final env = r.env;
  final lang = env.lang;
  final number = _s(r.args['check_number']);
  final amount = _dbl(o.data['amount']) ?? 0;
  final whole = r.args['lines'] is! List || (r.args['lines'] as List).isEmpty;
  final what = amount > 0 ? _m(amount, lang) : (whole ? am('whole', lang) : '');
  final say = am('returnDone', lang, {'number': number, 'what': what});
  await k.commit(
    r,
    tool: 'return_sale',
    say: say,
    label: (l) => '${tr5(l, 'возврат', 'кайтаруу', 'qaytarish', 'refund', 'iade')} №$number',
    data: {'check': number, 'amount': amount},
    whyNot: (l) => am('why.return', l),
    amount: amount,
  );
  return ToolResult(ToolStatus.ok, say, undo: false, data: {'check': number, 'amount': amount});
}

ToolResult? _prepareSupplier(ActionKit k, ToolRequest r) {
  final lines = r.args['lines'];
  if (lines is! List || lines.isEmpty) return _fail(r.env, 'needLines');
  return null;
}

Future<ToolResult> _supplierDone(ActionKit k, ToolRequest r, HandleOutcome o) async {
  final lang = r.env.lang;
  final total = _dbl(o.data['total']) ?? 0;
  final n = _int(o.data['lines']) ?? 0;
  // 2026-10-26 (голос-управление, В3б): сводку (позиций, штук, сумма закупки) собирает движок на языке звонка
  final what = _s(o.data['summary']).isNotEmpty
      ? _s(o.data['summary'])
      : (n > 0 ? am('receiveSummary', lang, {'n': '$n', 'qty': _q(_dbl(o.data['qty']) ?? 0, lang), 'total': _m(total, lang)}) : _m(total, lang));
  final say = am('receiveSupplierDone', lang, {'what': what});
  await k.commit(
    r,
    tool: 'receive_from_supplier',
    say: say,
    label: (l) => tr5(l, 'приёмка от поставщика', 'жеткирүүчүдөн кабыл алуу', 'yetkazib beruvchidan qabul', 'supplier receiving', 'tedarikçi mal kabulü'),
    data: {'doc': _s(o.data['doc']), 'total': _dbl(o.data['total']) ?? 0},
    whyNot: (l) => am('why.received', l),
    amount: _dbl(o.data['total']),
  );
  return ToolResult(ToolStatus.ok, say, undo: false);
}

ToolResult? _prepareCreateProduct(ActionKit k, ToolRequest r) {
  if (_s(r.args['name']).isEmpty) return _fail(r.env, 'needProduct');
  if (_dbl(r.args['price']) == null) return _fail(r.env, 'needPriceValue');
  return null;
}

Future<ToolResult> _createProductDone(ActionKit k, ToolRequest r, HandleOutcome o) async {
  final name = _s(r.args['name']);
  final say = am('productCreated', r.env.lang, {'name': _clip(name, 40)});
  await k.commit(
    r,
    tool: 'create_product',
    say: say,
    label: (l) => '+ ${_clip(name, 30)}',
    data: {'name': name, 'id': _s(o.data['id'])},
    whyNot: (l) => am('why.delete', l),
  );
  return ToolResult(ToolStatus.ok, say, undo: false);
}

ToolResult? _prepareCloseShift(ActionKit k, ToolRequest r) {
  final env = r.env;
  if (k.host.shift == null) return _fail(env, 'noShift');
  if (_dbl(r.args['counted_cash']) == null) return _fail(env, 'needCounted');
  return null;
}

Future<ToolResult> _closeShiftDone(ActionKit k, ToolRequest r, HandleOutcome o) async {
  final lang = r.env.lang;
  final counted = _dbl(r.args['counted_cash']) ?? 0;
  final say = am('shiftClosedSay', lang, {'counted': _m(counted, lang)});
  await k.commit(
    r,
    tool: 'close_shift',
    say: say,
    label: (l) => tr5(l, 'закрытие смены', 'сменаны жабуу', 'smenani yopish', 'shift closed', 'vardiya kapatma'),
    data: {'counted': counted},
    whyNot: (l) => am('why.shift', l),
    amount: counted,
  );
  return ToolResult(ToolStatus.ok, say, undo: false);
}

// ---------------------------------------------------------------------------------------------------------------------
// Склад: приход без поставщика, списание, ревизия, удаление
// ---------------------------------------------------------------------------------------------------------------------

/// Строки «товар + число»: каждая строка находит свой товар; неоднозначная — ответ с вариантами (ничего не применяется).
Future<({List<({Product p, double v})>? lines, ToolResult? stop})> _resolveLines(ActionKit k, ToolRequest r, String valueKey) async {
  final env = r.env;
  final raw = r.args['lines'];
  if (raw is! List || raw.isEmpty) return (lines: null, stop: _fail(env, 'needLines'));
  final out = <({Product p, double v})>[];
  for (final l in raw) {
    if (l is! Map) continue;
    final v = _dbl(l[valueKey]);
    if (v == null) return (lines: null, stop: _fail(env, 'needQty'));
    final lineReq = ToolRequest(LiveToolCall(r.call.id, r.call.name, l.cast<String, dynamic>()), env);
    final pp = await _pickProduct(k, lineReq, query: _s(l['query']), code: _s(l['barcode']), pick: l['pick']);
    if (pp.stop != null) return (lines: null, stop: pp.stop);
    out.add((p: pp.product!, v: v));
  }
  return (lines: out, stop: null);
}

AssistantTool _receiveSimple(ActionKit k, bool Function(Session) products) => AssistantTool(
  decl: ToolDecl(
    'receive_simple',
    'Simple stock receipt, no supplier and no prices: adds quantities to products. ru: прими 5 колы, оприходуй; ky: кабыл ал.',
    parameters: sObj({'lines': _linesSchema('qty')}, required: ['lines']),
  ),
  group: ToolGroup.money,
  allowed: products,
  needs: 'Склад',
  intentWords: Intents.receive,
  gateFor: (a) => [GateReq(words: Intents.receive, values: {for (final (i, v) in _lineValues(a['lines'], const ['qty']).indexed) 'q$i': v})],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final res = await _resolveLines(k, r, 'qty');
    if (res.stop != null) return res.stop!;
    final lines = res.lines!;
    if (lines.any((l) => l.v <= 0)) return _fail(env, 'needQty');
    final stock = [for (final l in lines) StockLine(productId: l.p.id, name: l.p.name, before: l.p.quantity, qty: l.v)];
    final sent = await k.host.receiveSimple(stock);
    final total = lines.fold<double>(0, (a, l) => a + l.v);
    final say = am(sent ? 'received' : 'receivedQueued', lang, {'n': '${lines.length}', 'qty': _q(total, lang)});
    Audit.log('receive', 'ИИ: ${lines.map((l) => '${l.p.name} × ${l.v}').take(3).join(', ')}', amount: total);
    await k.commit(
      r,
      tool: 'receive_simple',
      say: say,
      label: (l) => '${tr5(l, 'приход', 'кирешелөө', 'kirim', 'receipt', 'mal kabulü')}: ${lines.length}',
      data: {'lines': [for (final l in lines) {'id': l.p.id, 'qty': l.v}], 'sent': sent},
      whyNot: (l) => am('why.received', l),
    );
    return ToolResult(ToolStatus.ok, say, undo: false, data: {'n': lines.length, 'qty': total});
  }), money: true),
);

AssistantTool _writeOff(ActionKit k, bool Function(Session) products) => AssistantTool(
  decl: ToolDecl(
    'write_off',
    'Write off a quantity of ONE product from stock. ru: спиши две бутылки воды, бой; ky: эсептен чыгар.',
    parameters: sObj({
      'query': _query(),
      'barcode': sStr(),
      'pick': _pick(),
      'qty': sNum(),
      'reason': sStr(desc: 'Why, e.g. broken, expired'),
    }, required: ['qty']),
  ),
  group: ToolGroup.money,
  allowed: products,
  needs: 'Склад',
  intentWords: Intents.writeOff,
  numericArgs: const ['qty'],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final qty = _dbl(r.args['qty']);
    if (qty == null || qty <= 0) return _fail(env, 'needQty');
    final pp = await _pickProduct(k, r, query: _s(r.args['query']), code: _s(r.args['barcode']), pick: r.args['pick']);
    if (pp.stop != null) return pp.stop!;
    final p = pp.product!;
    final reason = _s(r.args['reason']);
    final why = reason.isEmpty ? am('reasonDefault', 'ru') : reason;
    final res = await k.host.changeStock(p.id, StockDoc.writeOff, qty, 'Списание (ИИ): $why');
    if (res.unchanged) return _fail(env, 'stockSame', {'qty': _q(res.to, lang, p.unit)});
    final say = res.applied
        ? am('writtenOff', lang, {'qty': _q(qty, lang, p.unit), 'name': _clip(p.name, 40), 'from': _q(res.from, lang), 'to': _q(res.to, lang, p.unit)})
        : '${am('writtenOff', lang, {'qty': _q(qty, lang, p.unit), 'name': _clip(p.name, 40), 'from': _q(res.from, lang), 'to': _q(res.to, lang, p.unit)})} ${am('stockQueued', lang)}';
    Audit.log('receive', 'ИИ: списание ${p.name} · ${res.from} → ${res.to} · $why');
    await k.commit(
      r,
      tool: 'write_off',
      say: say,
      label: (l) => '${tr5(l, 'списание', 'эсептен чыгаруу', 'hisobdan chiqarish', 'write-off', 'düşüm')} ${_clip(p.name, 24)} ${_q(qty, l)}',
      data: {'id': p.id, 'qty': qty, 'from': res.from, 'to': res.to, 'applied': res.applied},
      whyNot: (l) => am('why.writeoff', l),
    );
    return ToolResult(ToolStatus.ok, say, undo: false, data: {'from': res.from, 'to': res.to});
  }), money: true),
);

AssistantTool _stockCount(ActionKit k, bool Function(Session) products) => AssistantTool(
  decl: ToolDecl(
    'stock_count',
    'Stock count: sets the stock of products to the counted quantity. ru: пересчитал, остаток стал 12; ky: санап чыктым.',
    parameters: sObj({'lines': _linesSchema('counted'), 'note': sStr()}, required: ['lines']),
  ),
  group: ToolGroup.money,
  allowed: products,
  needs: 'Склад',
  intentWords: Intents.count,
  gateFor: (a) => [GateReq(words: Intents.count, values: {for (final (i, v) in _lineValues(a['lines'], const ['counted']).indexed) 'c$i': v})],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final res = await _resolveLines(k, r, 'counted');
    if (res.stop != null) return res.stop!;
    final lines = res.lines!;
    if (lines.any((l) => l.v < 0)) return _fail(env, 'needQty');
    final counted = {for (final l in lines) l.p.id: l.v};
    final diffs = [for (final l in lines) l.v - l.p.quantity];
    final diffText = lines.length == 1 ? _q(diffs.first, lang) : '${diffs.where((d) => d != 0).length}';
    final note = _s(r.args['note']);
    final applied = await k.host.applyCount('Ревизия (ИИ)${note.isEmpty ? '' : ': $note'}', counted);
    final say = am(applied ? 'counted' : 'countedQueued', lang, {'n': '${lines.length}', 'diff': diffText});
    Audit.log('receive', 'ИИ: ревизия ${lines.length} поз.');
    await k.commit(
      r,
      tool: 'stock_count',
      say: say,
      label: (l) => '${tr5(l, 'ревизия', 'ревизия', 'reviziya', 'stock count', 'sayım')}: ${lines.length}',
      data: {'lines': [for (final l in lines) {'id': l.p.id, 'was': l.p.quantity, 'now': l.v}], 'applied': applied},
      whyNot: (l) => am('why.count', l),
    );
    return ToolResult(ToolStatus.ok, say, undo: false);
  }), money: true),
);

AssistantTool _deleteProduct(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'delete_product',
    'Delete ONE product (owner/admin only). ru: удали товар Кола; ky: товарды өчүр.',
    parameters: sObj({'query': _query(), 'barcode': sStr(), 'pick': _pick()}),
  ),
  group: ToolGroup.money,
  allowed: (s) => s.isAdmin,
  needs: 'Администратор',
  intentWords: Intents.delete,
  objectArgs: const ['query'],
  numericArgs: const ['barcode'], // удаление по штрихкоду: цифры должны прозвучать (объект просьбы)
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final pp = await _pickProduct(k, r, query: _s(r.args['query']), code: _s(r.args['barcode']), pick: r.args['pick']);
    if (pp.stop != null) return pp.stop!;
    final p = pp.product!;
    // В журнал — все поля товара до удаления: «восстановить» = создать заново (история продаж не вернётся).
    final snapshot = {
      'id': p.id,
      'name': p.name,
      'price': p.price,
      'wholesale': p.wholesalePrice,
      'barcode': p.barcode,
      'unit': p.unit,
      'category': p.category,
      'quantity': p.quantity,
    };
    await k.host.deleteProduct(p.id);
    Audit.log('product', 'ИИ: удалён ${p.name}');
    final say = am('deleted', lang, {'name': _clip(p.name, 40)});
    await k.commit(
      r,
      tool: 'delete_product',
      say: say,
      label: (l) => '${tr5(l, 'удаление товара', 'товарды өчүрүү', "tovarni o'chirish", 'product deleted', 'ürün silme')} ${_clip(p.name, 24)}',
      data: snapshot,
      whyNot: (l) => am('why.delete', l),
    );
    return ToolResult(ToolStatus.ok, say, undo: false);
  }), money: true),
);

// ---------------------------------------------------------------------------------------------------------------------
// Цены, массовая наценка, штрихкод и минимальный остаток
// ---------------------------------------------------------------------------------------------------------------------

bool _moneyDiff(double a, double b) => (a - b).abs() > 0.004;

AssistantTool _setPrice(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'set_price',
    'Set price of ONE product: price, purchase cost, wholesale price, or markup_percent over purchase. A price below purchase is allowed with a warning. ru: поставь цену 120; ky: баасын койчу.',
    parameters: sObj({
      'query': _query(),
      'barcode': sStr(),
      'pick': _pick(),
      'price': sNum(),
      'purchase': sNum(),
      'wholesale': sNum(),
      'markup_percent': sNum(),
    }),
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canEditPrices,
  needs: 'Изменение цены',
  confirmInAskMode: true,
  intentWords: Intents.price,
  numericArgs: const ['price', 'purchase', 'wholesale', 'markup_percent'],
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final host = k.host;
    final newPrice = _dbl(r.args['price']);
    final newPurchase = _dbl(r.args['purchase']);
    final newWholesale = _dbl(r.args['wholesale']);
    final markup = _dbl(r.args['markup_percent']);
    if (newPrice == null && newPurchase == null && newWholesale == null && markup == null) return _fail(env, 'nothingToChange');
    if ([newPrice, newPurchase, newWholesale].any((v) => v != null && v < 0) || (markup != null && markup < -100)) return _fail(env, 'needPriceValue');
    final pp = await _pickProduct(k, r, query: _s(r.args['query']), code: _s(r.args['barcode']), pick: r.args['pick']);
    if (pp.stop != null) return pp.stop!;
    final p = pp.product!;
    final before = await host.loadDraft(p);
    final purchaseAfter = newPurchase ?? before.purchase;
    double? priceAfter = newPrice;
    if (priceAfter == null && markup != null) {
      if (purchaseAfter <= 0) return _fail(env, 'noPurchase');
      priceAfter = suggestedPrice(purchaseAfter, markup);
    }
    final after = before.copyWith(price: priceAfter, purchase: newPurchase, wholesale: newWholesale);
    final changed = _moneyDiff(after.price, before.price) || _moneyDiff(after.purchase, before.purchase) || _moneyDiff(after.wholesale, before.wholesale);
    if (!changed) return _fail(env, 'priceSame', {'name': _clip(p.name, 40), 'price': _m(before.price, lang)}, ToolStatus.ok);
    await host.saveDraft(p.id, before, after);
    final parts = <String>[
      if (_moneyDiff(after.price, before.price)) am('partPrice', lang, {'to': _m(after.price, lang), 'from': _m(before.price, lang)}),
      if (_moneyDiff(after.purchase, before.purchase)) am('partPurchase', lang, {'to': _m(after.purchase, lang), 'from': _m(before.purchase, lang)}),
      if (_moneyDiff(after.wholesale, before.wholesale)) am('partWholesale', lang, {'to': _m(after.wholesale, lang), 'from': _m(before.wholesale, lang)}),
    ];
    var say = am('priceSet', lang, {'name': _clip(p.name, 40), 'parts': parts.join(', ')});
    if (after.purchase > 0 && after.price < after.purchase - 0.004) {
      say += am('belowCost', lang, {'diff': _m(after.purchase - after.price, lang)});
    }
    Audit.log('price', 'ИИ: ${p.name} · ${before.price} → ${after.price}', amount: after.price);
    await k.commit(
      r,
      tool: 'set_price',
      say: say,
      label: (l) => '${tr5(l, 'цена', 'баа', 'narx', 'price', 'fiyat')} ${_clip(p.name, 24)}: ${_m(after.price, l)}',
      data: {'id': p.id, 'price_from': before.price, 'price_to': after.price, 'purchase_from': before.purchase, 'purchase_to': after.purchase, 'wholesale_from': before.wholesale, 'wholesale_to': after.wholesale},
      undoFn: (l) => _undoDraft(k, p, expected: after, restore: before, label: _clip(p.name, 30)),
      amount: after.price,
    );
    return ToolResult(ToolStatus.ok, say, undo: true, data: {'price': after.price, 'was': before.price});
  }),
);

/// Откат карточки: цену вернём, только если на сервере сейчас то, что поставила ИИ (иначе — «уже меняли после меня»).
Future<UndoResult> _undoDraft(ActionKit k, Product p, {required ProductDraft expected, required ProductDraft restore, String label = ''}) async {
  final cur = await k.host.loadDraft(p);
  final same = !_moneyDiff(cur.price, expected.price) &&
      !_moneyDiff(cur.purchase, expected.purchase) &&
      !_moneyDiff(cur.wholesale, expected.wholesale) &&
      cur.barcode.trim() == expected.barcode.trim() &&
      (cur.minQty ?? 0) == (expected.minQty ?? 0);
  if (!same) return UndoResult.conflict;
  await k.host.saveDraft(p.id, cur, restore);
  return UndoResult.ok;
}

AssistantTool _bulkMarkup(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'bulk_markup',
    'Set price = purchase × (1 + markup%) for MANY products, max 40; scope: low_markup (markup below it) | category | table (the table on screen). Shows a was→now table. ru: подними наценку до 20%.',
    parameters: sObj({
      'markup_percent': sNum(),
      'scope': sStr(values: ['low_markup', 'category', 'table']),
      'category': sStr(),
      'table_id': sStr(),
    }, required: ['markup_percent', 'scope']),
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canEditPrices,
  needs: 'Изменение цены',
  confirmInAskMode: true,
  intentWords: Intents.priceBulk,
  numericArgs: const ['markup_percent'],
  run: (r) => _guard(r.env, () => k.serial(() => _bulkRun(k, r))),
);

Future<ToolResult> _bulkRun(ActionKit k, ToolRequest r) async {
  final env = r.env;
  final lang = env.lang;
  final host = k.host;
  final pct = _dbl(r.args['markup_percent']);
  if (pct == null || pct < 0 || pct > 1000) return _fail(env, 'bulkNeedPct');
  final scope = _s(r.args['scope']);
  // 1. Кого меняем (без записи).
  final targets = <Product>[];
  final known = <String, double>{}; // id → закупка, если уже известна из отчёта
  switch (scope) {
    case 'low_markup':
      {
        final all = await host.allProducts();
        final now = env.now();
        final sold = await host.productSales(now.subtract(const Duration(days: recSalesDays)), now);
        final stock = [for (final p in all) RecStock(p.id, p.name, p.unit, p.quantity, p.price)];
        for (final row in lowMarkup(stock, sold, minPct: pct)) {
          if (row.item.id.isEmpty) continue;
          final p = all.where((x) => x.id == row.item.id).firstOrNull;
          if (p == null) continue;
          targets.add(p);
          known[p.id] = row.purchase;
        }
      }
    case 'category':
      {
        final cat = foldText(_s(r.args['category']));
        if (cat.isEmpty) return _fail(env, 'bulkNeedScope');
        final all = await host.allProducts();
        targets.addAll(all.where((p) => !p.isService && (foldText(p.category) == cat || (cat.length >= 3 && foldText(p.category).startsWith(cat)))));
      }
    case 'table':
      {
        final t = k.tables?.byId(_s(r.args['table_id']).isEmpty ? null : _s(r.args['table_id'])) ?? k.tables?.current;
        if (t == null) return _fail(env, 'bulkNeedScope');
        for (final id in t.refs.whereType<String>()) {
          final p = await host.productById(id);
          if (p != null) targets.add(p);
        }
      }
    default:
      return _fail(env, 'bulkNeedScope');
  }
  if (targets.isEmpty) return _fail(env, 'bulkNone', const {}, ToolStatus.notFound);
  if (targets.length > k.bulkMax) return _fail(env, 'bulkTooMany', {'n': '${targets.length}', 'max': '${k.bulkMax}'});

  // 2. Применяем по одному (ProductEditRepository.save). Сбой связи — останавливаемся, что успели, то отменимо.
  final done = <({Product p, ProductDraft before, ProductDraft after})>[];
  var skipped = 0;
  ToolResult? stopped;
  for (final p in targets) {
    try {
      final before = await host.loadDraft(p);
      final purchase = before.purchase > 0 ? before.purchase : (known[p.id] ?? 0);
      if (purchase <= 0) {
        skipped++;
        continue;
      }
      final target = suggestedPrice(purchase, pct);
      if (!_moneyDiff(target, before.price)) {
        skipped++;
        continue;
      }
      final after = before.copyWith(price: target);
      await host.saveDraft(p.id, before, after);
      done.add((p: p, before: before, after: after));
    } on ApiException catch (e) {
      stopped = _apiError(env, e);
      break;
    }
  }
  if (done.isEmpty) return stopped ?? _fail(env, 'bulkNone', const {}, ToolStatus.notFound);

  final t = CallTable.build(
    id: k.tables?.nextId() ?? 't0',
    title: tr5(lang, 'Наценка $pct%: было → стало', 'Үстөк $pct%: болгон → болду', "Ustama $pct%: edi → bo'ldi", 'Markup $pct%: was → now', 'Kâr %$pct: önce → sonra'),
    columns: [
      CallColumn('name', tr5(lang, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'), CellKind.text),
      CallColumn('was', tr5(lang, 'Было', 'Болгон', 'Edi', 'Was', 'Önce'), CellKind.money),
      CallColumn('now', tr5(lang, 'Стало', 'Болду', "Bo'ldi", 'Now', 'Sonra'), CellKind.money),
      CallColumn('mk', tr5(lang, 'Наценка', 'Үстөк', 'Ustama', 'Markup', 'Kâr'), CellKind.percent, signed: false),
    ],
    rows: [
      for (final d in done) <Object?>[d.p.name, d.before.price, d.after.price, (d.after.price - d.after.purchase) / (d.after.purchase > 0 ? d.after.purchase : 1) * 100],
    ],
    refs: [for (final d in done) d.p.id],
    source: 'phone',
    at: env.now(),
    lang: lang,
    objectRoute: 'product',
  );
  k.tables?.add(t);
  final pctText = pct == pct.roundToDouble() ? pct.toStringAsFixed(0) : pct.toString();
  var say = am('bulkDone', lang, {'pct': pctText, 'n': '${done.length}', 'skipped': skipped > 0 ? am('bulkSkipped', lang, {'n': '$skipped'}) : ''});
  if (stopped != null) say += ' ${stopped.say}';
  Audit.log('price', 'ИИ: наценка $pctText% · ${done.length} товаров');
  final items = done;
  await k.commit(
    r,
    tool: 'bulk_markup',
    say: say,
    label: (l) => '${tr5(l, 'наценка', 'үстөк', 'ustama', 'markup', 'kâr')} $pctText% · ${items.length}',
    data: {
      'pct': pct,
      'scope': scope,
      'items': [for (final d in items) {'id': d.p.id, 'from': d.before.price, 'to': d.after.price}],
    },
    undoFn: (l) async {
      var ok = 0, conflict = 0, failed = 0;
      for (final d in items) {
        try {
          final res = await _undoDraft(k, d.p, expected: d.after, restore: d.before);
          if (res.status == UndoStatus.ok) {
            ok++;
          } else {
            conflict++;
          }
        } on ApiException {
          failed++;
        }
      }
      if (conflict == 0 && failed == 0) return UndoResult.ok;
      // Частично: хоть что-то вернули — шаг засчитан (запись снимается), остальное названо в итоге.
      final msg = tr5(
        l,
        'Вернула цены у $ok из ${items.length}. ${conflict > 0 ? 'У $conflict цену уже меняли после меня — не трогаю. ' : ''}${failed > 0 ? 'У $failed не вышло (нет связи).' : ''}',
        'Баасы кайтарылды: $ok / ${items.length}. ${conflict > 0 ? '$conflict товардын баасы менден кийин өзгөргөн — тийбейм. ' : ''}${failed > 0 ? '$failed үчүн болгон жок (байланыш жок).' : ''}',
        'Narxlar qaytarildi: $ok / ${items.length}. ${conflict > 0 ? '$conflict tovar narxi mendan keyin o\'zgargan — tegmayman. ' : ''}${failed > 0 ? '$failed tasi bo\'lmadi (aloqa yo\'q).' : ''}',
        'Restored prices for $ok of ${items.length}. ${conflict > 0 ? '$conflict were changed after me — left alone. ' : ''}${failed > 0 ? '$failed failed (no connection).' : ''}',
        'Fiyatlar geri alındı: $ok / ${items.length}. ${conflict > 0 ? '$conflict ürün benden sonra değişmiş — dokunmuyorum. ' : ''}${failed > 0 ? '$failed başarısız (bağlantı yok).' : ''}',
      );
      if (ok > 0) return UndoResult(UndoStatus.ok, msg.trim());
      return UndoResult(conflict > 0 ? UndoStatus.conflict : UndoStatus.failed, msg.trim());
    },
  );
  return ToolResult(ToolStatus.ok, say, undo: true, tableId: t.id, data: {'changed': done.length, 'skipped': skipped});
}

AssistantTool _productEdit(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'product_edit',
    'Change the barcode (new_barcode) or the minimum stock (min_qty) of ONE product. ru: поставь штрихкод, минимальный остаток 5.',
    parameters: sObj({
      'query': _query(),
      'code': sStr(desc: 'Current barcode of the product, if scanned'),
      'pick': _pick(),
      'new_barcode': sStr(),
      'min_qty': sNum(),
    }),
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canRoute('/admin/product'),
  needs: 'Склад',
  gateFor: (a) => [
    if (_s(a['new_barcode']).isNotEmpty) const GateReq(words: Intents.barcode, numeric: ['new_barcode']),
    if (a['min_qty'] != null) const GateReq(words: Intents.minQty, numeric: ['min_qty']),
    if (_s(a['new_barcode']).isEmpty && a['min_qty'] == null) const GateReq(words: Intents.barcode),
  ],
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final newCode = _s(r.args['new_barcode']).replaceAll(RegExp(r'\s+'), '');
    final minQty = _dbl(r.args['min_qty']);
    if (newCode.isEmpty && minQty == null) return _fail(env, 'nothingToChange');
    if (minQty != null && minQty < 0) return _fail(env, 'needQty');
    final pp = await _pickProduct(k, r, query: _s(r.args['query']), code: _s(r.args['code']), pick: r.args['pick']);
    if (pp.stop != null) return pp.stop!;
    final p = pp.product!;
    if (newCode.isNotEmpty) {
      final owner = await k.host.barcodeOwner(p.id, newCode);
      if (owner != null) return _fail(env, 'barcodeTaken', {'name': _clip(owner, 40)});
    }
    final before = await k.host.loadDraft(p);
    final after = before.copyWith(barcode: newCode.isEmpty ? null : newCode, minQty: minQty);
    await k.host.saveDraft(p.id, before, after);
    final say = [
      if (newCode.isNotEmpty) am('barcodeSet', lang, {'name': _clip(p.name, 40), 'code': newCode, 'from': before.barcode.isEmpty ? am('noBarcode', lang) : before.barcode}),
      if (minQty != null) am('minSet', lang, {'name': _clip(p.name, 40), 'qty': _q(minQty, lang, p.unit), 'from': _q(before.minQty ?? 0, lang, p.unit)}),
    ].join(' ');
    Audit.log('product', 'ИИ: ${p.name}${newCode.isEmpty ? '' : ' · штрихкод $newCode'}${minQty == null ? '' : ' · мин. остаток $minQty'}');
    await k.commit(
      r,
      tool: 'product_edit',
      say: say,
      label: (l) => '${tr5(l, 'карточка', 'карта', 'karta', 'card', 'kart')} ${_clip(p.name, 24)}',
      data: {'id': p.id, 'barcode_from': before.barcode, 'barcode_to': after.barcode, 'min_from': before.minQty, 'min_to': after.minQty},
      undoFn: (l) => _undoDraft(k, p, expected: after, restore: before, label: _clip(p.name, 30)),
    );
    return ToolResult(ToolStatus.ok, say, undo: true);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Касса, смена, долги
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _cashInOut(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'cash_in_out',
    'Put money into the drawer (in) or take it out (out) in the open shift. ru: внеси в кассу тысячу, изъять 500; ky: кассага салып кой.',
    parameters: sObj({
      'kind': sStr(values: ['in', 'out']),
      'amount': sNum(),
      'reason': sStr(),
    }, required: ['kind', 'amount']),
  ),
  group: ToolGroup.money,
  numericArgs: const ['amount'],
  gateFor: (a) => [GateReq(words: '${a['kind']}' == 'out' ? Intents.cashOut : Intents.cashIn, numeric: const ['amount'])],
  intentWords: Intents.cashIn,
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final host = k.host;
    final out = _s(r.args['kind']) == 'out';
    final amount = _dbl(r.args['amount']);
    if (amount == null || amount <= 0) return _fail(env, 'needAmount');
    final shift = host.shift;
    if (shift == null || shift.cashboxId == null) return _fail(env, 'noShift');
    // Как экран: изъять больше, чем должно быть в ящике, нельзя.
    if (out && shift.expectedCash > 0 && amount > shift.expectedCash + 0.004) {
      return _fail(env, 'cashOutTooMuch', {'drawer': _m(shift.expectedCash, lang), 'amount': _m(amount, lang)});
    }
    final reason = _s(r.args['reason']);
    await host.cashFlow(shiftId: shift.id, cashboxId: shift.cashboxId!, withdrawal: out, amount: amount, reason: reason);
    await host.refreshShift();
    Audit.log('cash', 'ИИ: ${out ? 'изъятие' : 'внесение'}${reason.isEmpty ? '' : ' · $reason'}', amount: out ? -amount : amount);
    final drawer = host.shift?.expectedCash ?? (out ? shift.expectedCash - amount : shift.expectedCash + amount);
    final say = am(out ? 'cashOut' : 'cashIn', lang, {'amount': _m(amount, lang), 'drawer': _m(drawer, lang)});
    final shiftId = shift.id;
    final boxId = shift.cashboxId!;
    await k.commit(
      r,
      tool: 'cash_in_out',
      say: say,
      label: (l) => '${out ? tr5(l, 'изъятие', 'алуу', 'chiqim', 'cash out', 'çıkış') : tr5(l, 'внесение', 'киргизүү', 'kirim', 'cash in', 'giriş')} ${_m(amount, l)}',
      data: {'kind': out ? 'out' : 'in', 'amount': amount, 'shift': shiftId},
      // Отмена — обратная операция той же смены (раздел 2.5 б ТЗ).
      undoFn: (l) async {
        final cur = host.shift;
        if (cur == null || cur.id != shiftId) return UndoResult(UndoStatus.failed, am('noShift', l));
        await host.cashFlow(shiftId: shiftId, cashboxId: boxId, withdrawal: !out, amount: amount, reason: 'отмена ИИ');
        await host.refreshShift();
        Audit.log('cash', 'ИИ: отмена ${out ? 'изъятия' : 'внесения'}', amount: out ? amount : -amount);
        return UndoResult.ok;
      },
      amount: out ? -amount : amount,
    );
    return ToolResult(ToolStatus.ok, say, undo: true, data: {'drawer': drawer});
  }, ), money: true),
);

AssistantTool _payDebt(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'pay_debt',
    "Accept a client's payment toward their debt: amount, or all=true for the whole debt. ru: Асан вернул долг 500; ky: карызын төлөдү.",
    parameters: sObj({
      'client': sStr(desc: 'Debtor name as said'),
      'amount': sNum(),
      'all': sBool(desc: 'true to take the whole debt'),
      'method': sStr(values: ['cash', 'transfer']),
      'pick': _pick(),
    }, required: ['client']),
  ),
  group: ToolGroup.money,
  intentWords: Intents.debtPay,
  numericArgs: const ['amount'],
  objectArgs: const ['client'],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final host = k.host;
    final name = _s(r.args['client']);
    if (name.isEmpty) return _fail(env, 'needClient');
    final all = r.args['all'] == true;
    final amount = _dbl(r.args['amount']);
    if (!all && (amount == null || amount <= 0)) return _fail(env, 'needAmount');
    final method = _s(r.args['method']) == 'transfer' ? 'transfer' : 'cash';
    final list = (await host.debtors()).where((d) => d.debt > 0.004 && (looseNameMatch(d.name, name) || productNameMatchesQuery(d.name, name))).toList();
    if (list.isEmpty) return _fail(env, 'debtNone', const {}, ToolStatus.notFound);
    final pk = _int(r.args['pick']);
    Debtor? d;
    if (pk != null && pk >= 1 && pk <= list.length) {
      d = list[pk - 1];
    } else if (list.length == 1) {
      d = list.first;
    }
    if (d == null) {
      // Имена и телефоны в Google не уходят: модель получает только номера и суммы.
      final shown = list.take(5).toList();
      return ToolResult(
        ToolStatus.ambiguous,
        am('debtMany', lang, {'list': [for (var i = 0; i < shown.length; i++) '${i + 1} — ${_m(shown[i].debt, lang)}'].join('; ')}),
        options: [
          for (var i = 0; i < shown.length; i++) {'n': i + 1, 'debt': shown[i].debt},
        ],
      );
    }
    final sum = all ? d.debt : (amount! > d.debt ? d.debt : amount);
    if (method != 'cash' && !all && amount! > d.debt + 0.004) return _fail(env, 'debtOverCard', {'debt': _m(d.debt, lang)});
    await host.payDebt(clientId: d.clientId, amount: sum, method: method, shiftId: host.shift?.id);
    Audit.log('cash', 'ИИ: погашение долга · ${d.name}', amount: sum);
    await host.refreshShift();
    final left = d.debt - sum < 0 ? 0.0 : d.debt - sum;
    final say = am('debtPaid', lang, {'amount': _m(sum, lang), 'left': _m(left, lang)});
    await k.commit(
      r,
      tool: 'pay_debt',
      say: say,
      label: (l) => '${tr5(l, 'погашение долга', 'карызды жабуу', 'qarzni yopish', 'debt payment', 'borç ödemesi')} ${_m(sum, l)}',
      data: {'client_code': 'Д', 'amount': sum, 'left': left, 'method': method},
      whyNot: (l) => am('why.debt', l),
      amount: sum,
    );
    return ToolResult(ToolStatus.ok, say, undo: false, data: {'amount': sum, 'left': left});
  }, ), money: true),
);

AssistantTool _openShift(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'open_shift',
    'Open a shift with the opening cash. ru: открой смену, в кассе 1000; ky: сменаны ач.',
    parameters: sObj({'opening_cash': sNum()}, required: ['opening_cash']),
  ),
  group: ToolGroup.money,
  confirmInAskMode: false,
  intentWords: Intents.shiftOpen,
  numericArgs: const ['opening_cash'],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final cash = _dbl(r.args['opening_cash']);
    if (cash == null || cash < 0) return _fail(env, 'needAmount');
    if (k.host.shift != null) return _fail(env, 'shiftAlready', const {}, ToolStatus.ok);
    if (env.session.cashboxId == null) return _fail(env, 'noCashbox', const {}, ToolStatus.error);
    await k.host.openShift(cash);
    final say = am('shiftOpened', lang, {'cash': _m(cash, lang)});
    await k.commit(
      r,
      tool: 'open_shift',
      say: say,
      label: (l) => tr5(l, 'открытие смены', 'сменаны ачуу', 'smenani ochish', 'shift opened', 'vardiya açma'),
      data: {'cash': cash},
      whyNot: (l) => am('why.shift', l),
      amount: cash,
    );
    return ToolResult(ToolStatus.ok, say, undo: false);
  }, ), money: true),
);

// ---------------------------------------------------------------------------------------------------------------------
// Клиенты, бонусы, заказы
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _addBonus(ActionKit k, bool Function(Session) clients) => AssistantTool(
  decl: ToolDecl(
    'add_bonus',
    'Add or subtract bonus points of a client. ru: начисли Асану 100 бонусов; ky: бонус кош.',
    parameters: sObj({
      'client': sStr(),
      'amount': sNum(),
      'op': sStr(values: ['add', 'subtract']),
      'note': sStr(),
      'pick': _pick(),
    }, required: ['client', 'amount']),
  ),
  group: ToolGroup.money,
  allowed: clients,
  needs: 'Клиенты',
  confirmInAskMode: false,
  intentWords: Intents.bonus,
  numericArgs: const ['amount'],
  objectArgs: const ['client'],
  run: (r) => _guard(r.env, () => k.serial(() async {
    final env = r.env;
    final lang = env.lang;
    final amount = _dbl(r.args['amount']);
    if (amount == null || amount <= 0) return _fail(env, 'needAmount');
    final pc = await _pickClient(k, r, _s(r.args['client']), r.args['pick']);
    if (pc.stop != null) return pc.stop!;
    final c = pc.client!;
    final subtract = _s(r.args['op']) == 'subtract';
    final delta = subtract ? -amount : amount;
    final note = _s(r.args['note']);
    await k.host.addBonus(c.id, delta, 'ИИ${note.isEmpty ? '' : ': $note'}');
    final nStr = amount == amount.roundToDouble() ? amount.toStringAsFixed(0) : amount.toString();
    final say = am(subtract ? 'bonusTaken' : 'bonusAdded', lang, {'n': nStr, 'name': _clip(c.name, 30)});
    Audit.log('open', 'ИИ: бонусы ${subtract ? '−' : '+'}$nStr · ${c.name}', amount: delta);
    await k.commit(
      r,
      tool: 'add_bonus',
      say: say,
      label: (l) => '${tr5(l, 'бонусы', 'бонус', 'bonus', 'bonus', 'bonus')} ${subtract ? '−' : '+'}$nStr',
      data: {'client_code': 'К', 'delta': delta},
      // Компенсирующая операция с пометкой «отмена ИИ».
      undoFn: (l) async {
        await k.host.addBonus(c.id, -delta, 'отмена ИИ');
        return UndoResult.ok;
      },
    );
    return ToolResult(ToolStatus.ok, say, undo: true);
  }, ), money: true),
);

Future<({ClientRow? client, ToolResult? stop})> _pickClient(ActionKit k, ToolRequest r, String name, Object? pick) async {
  final env = r.env;
  final lang = env.lang;
  if (name.isEmpty) return (client: null, stop: _fail(env, 'needClient'));
  final list = (await k.host.findClients(name)).where((c) => looseNameMatch(c.name, name) || productNameMatchesQuery(c.name, name)).toList();
  if (list.isEmpty) return (client: null, stop: _fail(env, 'clientNone', const {}, ToolStatus.notFound));
  final pk = _int(pick);
  if (pk != null && pk >= 1 && pk <= list.length) return (client: list[pk - 1], stop: null);
  if (list.length == 1) return (client: list.first, stop: null);
  final shown = list.take(5).toList();
  return (
    client: null,
    stop: ToolResult(
      ToolStatus.ambiguous,
      am('clientMany', lang, {'list': [for (var i = 0; i < shown.length; i++) '${i + 1} — ${_clip(shown[i].name, 24)}'].join('; ')}),
      options: [
        for (var i = 0; i < shown.length; i++) {'n': i + 1, 'title': _clip(shown[i].name, 24)},
      ],
    ),
  );
}

AssistantTool _createClient(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'create_client',
    'Create a new client. ru: создай клиента Айбек; ky: жаңы кардар.',
    parameters: sObj({'name': sStr(), 'phone': sStr()}, required: ['name']),
  ),
  group: ToolGroup.action,
  intentWords: Intents.clientCreate,
  objectArgs: const ['name'],
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final name = _s(r.args['name']);
    if (name.isEmpty) return _fail(env, 'needName');
    final phone = _s(r.args['phone']);
    final c = await k.host.createClient(name, phone);
    final say = am('clientCreated', env.lang, {'name': _clip(c.name, 30)});
    Audit.log('open', 'ИИ: клиент создан');
    await k.commit(
      r,
      tool: 'create_client',
      say: say,
      label: (l) => '+ ${tr5(l, 'клиент', 'кардар', 'mijoz', 'client', 'müşteri')}',
      data: {'client_code': 'К'},
      whyNot: (l) => am('why.client', l),
    );
    return ToolResult(ToolStatus.ok, say, undo: false);
  }),
);

AssistantTool _setOrderStatus(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'set_order_status',
    'Set the status of a site order: accepted | done (handed over) | canceled. Ready is set only while picking. ru: заказ 25 выдан.',
    parameters: sObj({
      'order': sStr(desc: 'Order number'),
      'status': sStr(values: ['accepted', 'done', 'canceled']),
    }, required: ['order', 'status']),
  ),
  group: ToolGroup.action,
  allowed: (s) => s.canRoute('/admin/orders'),
  needs: 'Заказы',
  intentWords: Intents.orderStatus,
  numericArgs: const ['order'],
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final number = _s(r.args['order']).replaceAll(RegExp(r'^[#№]+'), '');
    if (number.isEmpty) return _fail(env, 'needOrder');
    final status = _s(r.args['status']);
    if (status == 'ready') return _fail(env, 'orderReadyByScreen');
    if (!const {'accepted', 'done', 'canceled'}.contains(status)) return _fail(env, 'orderReadyByScreen');
    final orders = await k.host.orders();
    final o = orders.where((x) => x.number == number || x.number.endsWith('-$number') || x.number.replaceFirst(RegExp(r'^0+'), '') == number.replaceFirst(RegExp(r'^0+'), '')).firstOrNull;
    if (o == null) return _fail(env, 'orderNone', const {}, ToolStatus.notFound);
    final prev = o.status;
    await k.host.setOrderStatus(o.id, status);
    final say = am('orderStatus', lang, {'number': o.number, 'status': am('st.$status', lang)});
    Audit.log('open', 'ИИ: заказ ${o.number} → $status');
    await k.commit(
      r,
      tool: 'set_order_status',
      say: say,
      label: (l) => '${tr5(l, 'заказ', 'буйрутма', 'buyurtma', 'order', 'sipariş')} ${o.number}: ${am('st.$status', l)}',
      data: {'order': o.number, 'from': prev, 'to': status},
      undoFn: (l) async {
        await k.host.setOrderStatus(o.id, prev);
        return UndoResult.ok;
      },
    );
    return ToolResult(ToolStatus.ok, say, undo: true);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Вид приложения, отмена, журнал
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _uiSettings(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'ui_settings',
    'Change the app theme and/or the interface language. ru: включи тёмную тему, переключи на кыргызский.',
    parameters: sObj({
      'theme': sStr(values: ['light', 'dark', 'auto', 'system']),
      'language': sStr(values: ['ru', 'ky', 'uz', 'en', 'tr']),
    }),
  ),
  group: ToolGroup.action,
  gateFor: (a) => [
    if (_s(a['theme']).isNotEmpty) const GateReq(words: Intents.theme),
    if (_s(a['language']).isNotEmpty) const GateReq(words: Intents.language),
    if (_s(a['theme']).isEmpty && _s(a['language']).isEmpty) const GateReq(words: Intents.theme),
  ],
  run: (r) => _guard(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final theme = _s(r.args['theme']);
    final language = _s(r.args['language']);
    if (theme.isEmpty && language.isEmpty) return _fail(env, 'nothingToChange');
    final prevTheme = k.host.themeMode;
    final prevLang = k.host.language;
    final says = <String>[];
    var outLang = lang;
    if (theme.isNotEmpty && const {'light', 'dark', 'auto', 'system'}.contains(theme)) {
      await k.host.setThemeMode(theme);
      says.add(am('themeSet', lang, {'mode': am('th.$theme', lang)}));
    }
    if (const {'ru', 'ky', 'uz', 'en', 'tr'}.contains(language)) {
      await k.host.setLanguage(language);
      outLang = language;
      says.add(am('langSet', language));
    }
    if (says.isEmpty) return _fail(env, 'nothingToChange');
    final say = says.join(' ');
    await k.commit(
      r,
      tool: 'ui_settings',
      say: say,
      label: (l) => tr5(l, 'тема / язык', 'тема / тил', 'mavzu / til', 'theme / language', 'tema / dil'),
      data: {'theme_from': prevTheme, 'theme_to': theme, 'lang_from': prevLang, 'lang_to': language},
      undoFn: (l) async {
        if (theme.isNotEmpty) await k.host.setThemeMode(prevTheme);
        if (language.isNotEmpty) await k.host.setLanguage(prevLang);
        return UndoResult.ok;
      },
    );
    return ToolResult(ToolStatus.ok, say, undo: true, data: {'language': outLang});
  }),
);

Future<({String say, int done})> _undoSteps(ActionKit k, Session session, String lang, int n) async {
  final says = <String>[];
  var done = 0;
  for (var i = 0; i < n; i++) {
    final e = k.undo.lastFor(session.userId);
    if (e == null) {
      if (done == 0) says.add(am('undoNothing', lang));
      break;
    }
    final label = e.label(lang);
    if (!e.reversible) {
      // Необратимое называем честно и не трогаем предыдущие.
      says.add(am('undoNo', lang, {'label': label, 'why': e.whyNot?.call(lang) ?? ''}).trim());
      break;
    }
    UndoResult res;
    try {
      res = await e.undo!(lang);
    } on ApiException {
      says.add(am('undoFailed', lang, {'label': label}));
      break;
    } catch (_) {
      says.add(am('undoFailed', lang, {'label': label}));
      break;
    }
    if (res.status == UndoStatus.ok) {
      k.undo.remove(e);
      done++;
      says.add(res.say ?? am('undone', lang, {'label': label}));
      k.registry?.clearRecent();
      final j = k.journal;
      if (j != null) {
        await j.record(
          AiActionRecord(
            at: k.now(),
            user: session.userId,
            tool: 'undo_last',
            callId: 'undo:${e.id}',
            said: '',
            args: {'undone': e.tool},
            status: 'ok',
            say: says.last,
            mode: 'act',
            model: k.model,
          ),
        );
      }
    } else if (res.status == UndoStatus.conflict) {
      k.undo.remove(e);
      says.add(res.say ?? am('undoConflict', lang, {'label': label}));
      break;
    } else {
      says.add(res.say ?? am('undoFailed', lang, {'label': label}));
      break;
    }
  }
  return (say: says.join(' '), done: done);
}

AssistantTool _undoLast(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'undo_last',
    'Undo the last action(s) the assistant did for this user; irreversible ones are named honestly. ru: отмени последнее; ky: жокко чыгар.',
    parameters: sObj({'n': sInt(desc: 'How many steps back, 1-5')}),
  ),
  group: ToolGroup.action,
  dedupe: false,
  intentWords: Intents.undo,
  run: (r) => _guard(r.env, () async {
    final n = (_int(r.args['n']) ?? 1).clamp(1, 5);
    final res = await _undoSteps(k, r.env.session, r.env.lang, n);
    return ToolResult(res.done > 0 ? ToolStatus.ok : ToolStatus.notFound, res.say);
  }),
);

AssistantTool _lastActions(ActionKit k) => AssistantTool(
  decl: ToolDecl(
    'last_actions',
    'Read-only. The latest actions the assistant did for this user. ru: что ты сделала.',
    parameters: sObj({'limit': sInt()}),
  ),
  group: ToolGroup.read,
  run: (r) async {
    final env = r.env;
    final lang = env.lang;
    final limit = (_int(r.args['limit']) ?? 5).clamp(1, 10);
    final items = k.undo.recent(env.session.userId, limit);
    if (items.isEmpty) return ToolResult(ToolStatus.ok, am('lastNone', lang));
    final labels = [for (final e in items) e.label(lang)];
    return ToolResult(
      ToolStatus.ok,
      am('lastList', lang, {'list': labels.join('; ')}),
      data: {
        'actions': [
          for (final e in items) {'tool': e.tool, 'label': e.label(lang), 'undo': e.reversible},
        ],
      },
    );
  },
);
