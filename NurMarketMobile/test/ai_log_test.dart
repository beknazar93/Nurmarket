// 2026-10-26 (голос-управление, В5): журнал «Действия ИИ» (/ai/log) и «Отменить» вне звонка — на фейках, без сети и боевой базы.
// Часть 1 (без экрана): записи журнала от настоящих инструментов звонка → что можно отменить из журнала, как отменяется (цена,
// минимальный остаток, внесение/изъятие, заказ, тема), что нельзя («нельзя отменить», «запросом владельцу», «срок отмены прошёл»),
// конфликт «уже менялось после ИИ», повторяющийся id вызова в разных звонках.
// Часть 2 (экран): дни, значки групп, сказанная фраза курсивом, итог, фильтры, сотрудник видит свои, после «Отменить», пусто,
// раскладка ru/ky, шрифт 1.0 и 1.3, светлая и тёмная тема, кнопки ≥ 48 dp.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/journal_undo.dart';
import 'package:nurmarket/core/ai/assistant/undo.dart' show UndoStatus;
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_repository.dart' show SiteOrder;
import 'package:nurmarket/features/assistant/ai_log_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart' show ShiftInfo;
import 'package:nurmarket/features/stock/product_edit_repository.dart' show ProductDraft;
import 'package:nurmarket/l10n/app_localizations.dart';

import 'action_tools_test.dart' show FakeHost, Rig;

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

Session _sess(String id, {bool owner = false, String role = 'cashier', List<String>? perms}) =>
    Session(email: '$id@x.kg', access: 'a', refresh: 'r', userId: id, userName: id == 'u2' ? 'Бакыт О.' : 'Айгерим С.', role: role, isOwner: owner, companyName: 'Нур', cashboxId: 'b', perms: perms);

final _owner = _sess('u2', owner: true, role: 'owner');
final _cashier = _sess('u1', perms: const []);
final _cashierPrice = _sess('u1', perms: const [Session.editPrice]);

Product _p(String id, String name, {double price = 100}) => Product(
  id: id,
  name: name,
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 10,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
  barcode: null,
);

ProductDraft _draft(Product p, {double purchase = 0, double wholesale = 0, double? minQty}) =>
    ProductDraft(name: p.name, unit: p.unit, category: '', barcode: '', alt: const [], price: p.price, purchase: purchase, wholesale: wholesale, minQty: minQty);

int _n = 0;
AiActionRecord _rec(
  String tool, {
  String? call,
  String user = 'u2',
  String said = '',
  String say = '',
  Map<String, dynamic> args = const {},
  Map<String, dynamic> undo = const {},
  bool reversible = false,
  double? amount,
  DateTime? at,
  String status = 'ok',
}) => AiActionRecord(at: at ?? DateTime.now(), user: user, tool: tool, callId: call ?? 'c${++_n}', said: said, args: args, status: status, say: say.isEmpty ? tool : say, amount: amount, reversible: reversible, undo: undo);

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1480);
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

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late Directory dir;
  late File file;
  setUpAll(() async {
    await _fonts();
    dir = Directory.systemTemp.createTempSync('nm_ailog_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
    file = File('${dir.path}/ai_actions.jsonl');
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });
  setUp(() {
    AiJournal.resetQueueForTests();
    Audit.resetForTests();
    if (file.existsSync()) file.deleteSync();
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('«Отменить» вне звонка: записи настоящих инструментов', () {
    late Rig rig;
    late JournalUndoer undoer;
    setUp(() {
      rig = Rig(journal: AiJournal());
      final p1 = _p('p1', 'Сникерс', price: 110);
      rig.host.products = [p1, _p('p2', 'Кола', price: 60)];
      rig.host.drafts['p1'] = _draft(p1, purchase: 80, minQty: 2);
      undoer = JournalUndoer(rig.host);
    });

    Future<AiActionRecord> last(String tool) async => (await AiJournal().read()).firstWhere((r) => r.tool == tool);

    test('цена: запись «отменяемая» → «Отменить» возвращает прежнюю цену, закупка и опт не тронуты; потом «отменено»', () async {
      await rig.say('поставь Сникерсу цену 120', 'set_price', {'query': 'Сникерс', 'price': 120});
      expect(rig.host.drafts['p1']!.price, 120);
      final r = await last('set_price');
      final info = JournalUndo.classify(r, _owner, const {});
      expect(info.kind, JournalUndoKind.available);
      final out = await undoer.undo(r, _owner);
      expect(out.status, UndoStatus.ok);
      expect(rig.host.drafts['p1']!.price, 110);
      expect(rig.host.drafts['p1']!.purchase, 80);
      await undoer.markUndone(AiJournal(), r, _owner, 'Отменено: цена');
      final all = await AiJournal().read();
      final undone = JournalUndo.undoneMap(all);
      final fresh = all.firstWhere((e) => e.tool == 'set_price'); // записи сравниваются по объекту — берём прочитанную заново
      expect(JournalUndo.classify(fresh, _owner, undone).kind, JournalUndoKind.undone);
      // запись об отмене сама не отменяется и не мешает: группа — как у отменённого
      final marker = all.firstWhere((e) => e.tool == 'undo_last');
      expect(JournalUndo.classify(marker, _owner, undone).kind, JournalUndoKind.none);
      expect(JournalUndo.groupOf(marker), JournalUndo.groupOf(fresh));
    });

    test('цену успели поменять после ИИ → «уже менялось после ИИ», ничего не перетирается', () async {
      await rig.say('поставь Сникерсу цену 120', 'set_price', {'query': 'Сникерс', 'price': 120});
      final r = await last('set_price');
      rig.host.drafts['p1'] = rig.host.drafts['p1']!.copyWith(price: 135); // поправили вручную
      final before = rig.host.saves;
      final out = await undoer.undo(r, _owner);
      expect(out.status, UndoStatus.conflict);
      expect(rig.host.saves, before);
      expect(rig.host.drafts['p1']!.price, 135);
    });

    test('кассир без права «Изменение цены» кнопки не видит и отменить не может', () async {
      await rig.say('поставь Сникерсу цену 120', 'set_price', {'query': 'Сникерс', 'price': 120});
      final r = await last('set_price');
      expect(JournalUndo.classify(r, _cashier, const {}).kind, JournalUndoKind.none);
      expect(JournalUndo.classify(r, _cashierPrice, const {}).kind, JournalUndoKind.available);
      final out = await undoer.undo(r, _cashier);
      expect(out.status, UndoStatus.failed);
      expect(rig.host.drafts['p1']!.price, 110 + 10); // не откатилось
    });

    test('минимальный остаток отменяется и возвращает прежний; если прежнего минимума не было — «срок отмены прошёл»', () async {
      await rig.say('поставь Сникерсу минимальный остаток пять', 'product_edit', {'query': 'Сникерс', 'min_qty': 5});
      final r = await last('product_edit');
      expect(JournalUndo.classify(r, _owner, const {}).kind, JournalUndoKind.available);
      expect(rig.host.drafts['p1']!.minQty, 5);
      expect((await undoer.undo(r, _owner)).status, UndoStatus.ok);
      expect(rig.host.drafts['p1']!.minQty, 2);
      // минимум не был задан (сервер не отдал) — вернуть «пусто» нельзя, из журнала запись не отменяется
      rig.host.drafts['p1'] = _draft(_p('p1', 'Сникерс', price: 110), purchase: 80);
      rig.host.drafts['p1'] = rig.host.drafts['p1']!.copyWith(price: 110);
      await rig.say('поставь Сникерсу минимальный остаток семь', 'product_edit', {'query': 'Сникерс', 'min_qty': 7});
      final r2 = (await AiJournal().read()).firstWhere((e) => e.tool == 'product_edit' && e.args['min_qty'] == 7);
      expect(JournalUndo.classify(r2, _owner, const {}).kind, JournalUndoKind.expired);
    });

    test('штрихкод: значение замаскировано по цифрам → вернуть нечем', () async {
      await rig.say('поставь Сникерсу штрихкод 4600123456789', 'product_edit', {'query': 'Сникерс', 'new_barcode': '4600123456789'});
      final r = await last('product_edit');
      expect(r.undo['barcode_to'], contains('***'));
      expect(JournalUndo.classify(r, _owner, const {}).kind, JournalUndoKind.expired);
    });

    test('внесение в кассу: отмена — изъятие той же суммы в той же смене; смену закрыли → «нельзя», денег не двигаем', () async {
      await rig.say('внеси в кассу тысячу на размен', 'cash_in_out', {'kind': 'in', 'amount': 1000, 'reason': 'размен'});
      final r = await last('cash_in_out');
      expect(JournalUndo.classify(r, _owner, const {}).kind, JournalUndoKind.available);
      final out = await undoer.undo(r, _owner);
      expect(out.status, UndoStatus.ok);
      expect(rig.host.cashFlows.last.out, isTrue);
      expect(rig.host.cashFlows.last.amount, 1000);
      expect(rig.host.cashFlows.last.reason, 'отмена ИИ');
      // другая смена
      rig.host.shiftInfo = const ShiftInfo(id: 's9', cashboxId: 'cb1', expectedCash: 5000);
      final n = rig.host.cashFlows.length;
      final out2 = await undoer.undo(r, _owner);
      expect(out2.status, UndoStatus.failed);
      expect(out2.shiftClosed, isTrue);
      expect(rig.host.cashFlows.length, n);
    });

    test('изъятие отменяется внесением; внесение нельзя отменить, если в ящике уже меньше', () async {
      await rig.say('изъять двести', 'cash_in_out', {'kind': 'out', 'amount': 200});
      final out = await last('cash_in_out');
      expect((await undoer.undo(out, _owner)).status, UndoStatus.ok);
      expect(rig.host.cashFlows.last.out, isFalse);
      await rig.say('внеси в кассу тысячу', 'cash_in_out', {'kind': 'in', 'amount': 1000});
      final inn = (await AiJournal().read()).firstWhere((r) => r.tool == 'cash_in_out' && r.undo['kind'] == 'in');
      rig.host.shiftInfo = const ShiftInfo(id: 's1', cashboxId: 'cb1', expectedCash: 300);
      final n = rig.host.cashFlows.length;
      final o = await undoer.undo(inn, _owner);
      expect(o.status, UndoStatus.failed);
      expect(rig.host.cashFlows.length, n);
    });

    test('статус заказа и тема: возвращаются; изменили сами — не трогаем', () async {
      rig.host.orderList = [SiteOrder('o1', '25', 'Клиент', '', 'ready', 100, DateTime(2026, 10, 26), true, const [])];
      await rig.say('заказ 25 выдан', 'set_order_status', {'order': '25', 'status': 'done'});
      final o = await last('set_order_status');
      expect(JournalUndo.classify(o, _owner, const {}).kind, JournalUndoKind.available);
      // заказ уже поменяли → конфликт
      rig.host.orderList = [SiteOrder('o1', '25', 'Клиент', '', 'canceled', 100, DateTime(2026, 10, 26), true, const [])];
      expect((await undoer.undo(o, _owner)).status, UndoStatus.conflict);
      rig.host.orderList = [SiteOrder('o1', '25', 'Клиент', '', 'done', 100, DateTime(2026, 10, 26), true, const [])];
      expect((await undoer.undo(o, _owner)).status, UndoStatus.ok);
      expect(rig.host.orderSets.last, 'o1:ready');

      await rig.say('включи тёмную тему', 'ui_settings', {'theme': 'dark'});
      final u = await last('ui_settings');
      expect(rig.host.theme, 'dark');
      rig.host.theme = 'light'; // сменили вручную
      expect((await undoer.undo(u, _owner)).status, UndoStatus.conflict);
      rig.host.theme = 'dark';
      expect((await undoer.undo(u, _owner)).status, UndoStatus.ok);
      expect(rig.host.theme, 'light');
    });

    test('массовая наценка: частично — что вернулось, то вернулось; у кого цену менял кто-то ещё — не трогаем', () async {
      rig.host.drafts['p1'] = _draft(_p('p1', 'Сникерс', price: 120), purchase: 80);
      rig.host.drafts['p2'] = _draft(_p('p2', 'Кола', price: 70), purchase: 50);
      final r = _rec('bulk_markup', reversible: true, undo: {
        'pct': 20,
        'items': [
          {'id': 'p1', 'from': 110, 'to': 120},
          {'id': 'p2', 'from': 60, 'to': 66}, // цена Колы уже 70 — менялась после ИИ
        ],
      });
      final out = await undoer.undo(r, _owner);
      expect(out.status, UndoStatus.ok);
      expect(out.done, 1);
      expect(out.total, 2);
      expect(out.partial, isTrue);
      expect(rig.host.drafts['p1']!.price, 110);
      expect(rig.host.drafts['p2']!.price, 70);
      // все товары чужие/изменённые → конфликт
      final r2 = _rec('bulk_markup', reversible: true, undo: {
        'items': [
          {'id': 'p2', 'from': 60, 'to': 66},
        ],
      });
      expect((await undoer.undo(r2, _owner)).status, UndoStatus.conflict);
    });
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('что показать справа', () {
    final none = const <AiActionRecord, DateTime>{};
    test('необратимое — «нельзя отменить»; проведённый приход — с подсказкой про списание; выплата — запросом владельцу', () {
      expect(JournalUndo.classify(_rec('return_sale'), _owner, none).kind, JournalUndoKind.irreversible);
      expect(JournalUndo.classify(_rec('close_shift'), _owner, none).kind, JournalUndoKind.irreversible);
      expect(JournalUndo.classify(_rec('pay_debt'), _owner, none).kind, JournalUndoKind.irreversible);
      final rc = JournalUndo.classify(_rec('receive_from_supplier'), _owner, none);
      expect(rc.kind, JournalUndoKind.irreversible);
      expect(rc.writeOffHint, isTrue);
      expect(JournalUndo.classify(_rec('salary_payout'), _owner, none).kind, JournalUndoKind.ownerRequest);
      expect(JournalUndo.classify(_rec('delete_product'), _owner, none).kind, JournalUndoKind.irreversible);
    });

    test('обратимое только в звонке (корзина, оплата чека) — «срок отмены прошёл»; запись не «ok» и запись об отмене — без подписи', () {
      expect(JournalUndo.classify(_rec('cart_edit', reversible: true), _owner, none).kind, JournalUndoKind.expired);
      expect(JournalUndo.classify(_rec('checkout', reversible: true, undo: {'sale': 's1', 'number': '1043'}), _owner, none).kind, JournalUndoKind.expired);
      expect(JournalUndo.classify(_rec('set_price', reversible: true, status: 'unknown', undo: {'id': 'p1', 'price_from': 1, 'price_to': 2}), _owner, none).kind, JournalUndoKind.none);
      expect(JournalUndo.classify(_rec('undo_last', args: {'undone': 'set_price'}), _owner, none).kind, JournalUndoKind.none);
      // данных для отмены нет (старая запись без старых значений) → честно «срок отмены прошёл»
      expect(JournalUndo.classify(_rec('set_price', reversible: true), _owner, none).kind, JournalUndoKind.expired);
    });

    test('id вызова повторился в двух звонках: отмена закрывает только ближайшую прежнюю запись с тем же инструментом', () {
      final t0 = DateTime(2026, 10, 25, 10);
      final a = _rec('set_price', call: 'fc1', at: t0, reversible: true, undo: {'id': 'p1', 'price_from': 1, 'price_to': 2});
      final b = _rec('set_price', call: 'fc1', at: t0.add(const Duration(days: 1)), reversible: true, undo: {'id': 'p1', 'price_from': 2, 'price_to': 3});
      final marker = _rec('undo_last', call: 'undo:fc1', at: t0.add(const Duration(days: 1, minutes: 5)), args: {'undone': 'set_price'});
      final map = JournalUndo.undoneMap([marker, b, a]); // новые сверху, как отдаёт AiJournal.read
      expect(map.containsKey(b), isTrue);
      expect(map.containsKey(a), isFalse);
      expect(map[b], marker.at);
    });

    test('группы значков и «только деньги»', () {
      expect(JournalUndo.groupOf(_rec('checkout')), AiLogGroup.money);
      expect(JournalUndo.groupOf(_rec('cart_edit')), AiLogGroup.cart);
      expect(JournalUndo.groupOf(_rec('set_price')), AiLogGroup.product);
      expect(JournalUndo.groupOf(_rec('task_create')), AiLogGroup.task);
      expect(JournalUndo.groupOf(_rec('salary_payout')), AiLogGroup.salary);
      expect(JournalUndo.groupOf(_rec('ui_settings')), AiLogGroup.nav);
      expect(JournalUndo.isMoney(_rec('pay_debt')), isTrue);
      expect(JournalUndo.isMoney(_rec('salary_payout')), isTrue);
      expect(JournalUndo.isMoney(_rec('set_price')), isFalse);
    });
  });

  // -------------------------------------------------------------------------------------------------------------------
  group('экран «Действия ИИ»', () {
    late FakeHost host;
    late AppDatabase db;
    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      host = FakeHost();
      final p1 = _p('p1', 'Сникерс', price: 120);
      host.products = [p1];
      host.drafts['p1'] = _draft(p1, purchase: 80);
    });
    tearDown(() => db.close());

    Widget app(Session s, {String lang = 'ru', bool dark = false}) {
      final router = GoRouter(
        routes: [
          GoRoute(path: '/', builder: (_, _) => const AiLogScreen()),
          GoRoute(path: '/admin/advisor', builder: (_, _) => const Scaffold(body: Text('ADVISOR'))),
        ],
      );
      return ProviderScope(
        key: UniqueKey(),
        overrides: [
          databaseProvider.overrideWithValue(db),
          sessionProvider.overrideWith(() => _Fixed(s)),
          aiPolicyProvider.overrideWith((ref) async => const AiAccessPolicy.unset()),
          journalUndoerProvider.overrideWithValue(JournalUndoer(host)),
        ],
        child: MaterialApp.router(
          routerConfig: router,
          theme: buildTheme(dark: false),
          darkTheme: buildTheme(dark: true),
          themeMode: dark ? ThemeMode.dark : ThemeMode.light,
          locale: Locale(lang),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        ),
      );
    }

    /// Реальные ввод-вывод и база идут вне «поддельных» часов теста: ждём (по кругу «реальная пауза → кадр»), пока экран загрузится
    /// (и пока не выполнится [until]); на нагруженном ПК фиксированной паузы не хватает.
    Future<void> settle(WidgetTester t, {bool Function()? until}) async {
      for (var i = 0; i < 200; i++) {
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
        await t.pump(const Duration(milliseconds: 50)); // со временем: Future.delayed(zero) внутри журнала тоже срабатывает
        final loading = find.byType(CircularProgressIndicator).evaluate().isNotEmpty;
        if (i >= 2 && !loading && (until == null || until())) break;
      }
      await t.pumpAndSettle(const Duration(milliseconds: 100), EnginePhase.sendSemanticsUpdate, const Duration(seconds: 5));
    }

    Future<void> seed(WidgetTester t, List<AiActionRecord> list) async {
      await t.runAsync(() async {
        for (final r in list) {
          await AiJournal().record(r);
        }
      });
    }

    String hm(DateTime d) => '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';

    List<AiActionRecord> sample() {
      final now = DateTime.now();
      final today = DateTime(now.year, now.month, now.day, 12);
      final yesterday = today.subtract(const Duration(days: 1));
      return [
        _rec('close_shift', user: 'u2', at: yesterday, said: 'закрой смену', say: 'Смена № 42 закрыта · наличные 2 860'),
        _rec('salary_payout', user: 'u2', at: yesterday.add(const Duration(minutes: 5)), said: 'выплати Айбеку восемнадцать пятьсот', say: 'Выплата Айбеку Т. 18 500 сом наличными', amount: 18500),
        _rec('receive_from_supplier', user: 'u2', at: today.add(const Duration(minutes: 1)), said: 'проведи приход', say: 'Приход № 4512 на 12 400 сом'),
        _rec('cart_edit', user: 'u1', at: today.add(const Duration(minutes: 2)), said: 'пробей две колы', say: 'В чек: Кола ×2', reversible: true),
        _rec(
          'set_price',
          user: 'u1',
          at: today.add(const Duration(minutes: 3)),
          said: 'поставь сникерсу сто двадцать',
          say: 'Snickers: цена 110 → 120 сом',
          reversible: true,
          call: 'cp1',
          args: {'query': 'Сникерс', 'price': 120},
          undo: {'id': 'p1', 'price_from': 110, 'price_to': 120, 'purchase_from': 80, 'purchase_to': 80, 'wholesale_from': 0, 'wholesale_to': 0},
        ),
      ];
    }

    testWidgets('владелец: дни, время, фраза курсивом, итог, значки групп, подписи справа', (t) async {
      _phone(t);
      await seed(t, sample());
      await t.pumpWidget(app(_owner));
      await settle(t);
      final now = DateTime.now();
      final dm = '${now.day.toString().padLeft(2, '0')}.${now.month.toString().padLeft(2, '0')}';
      expect(find.text('Сегодня · $dm'), findsOneWidget);
      expect(find.textContaining('Вчера · '), findsOneWidget);
      expect(find.text('«поставь сникерсу сто двадцать»'), findsOneWidget);
      expect(t.widget<Text>(find.text('«поставь сникерсу сто двадцать»')).style!.fontStyle, FontStyle.italic);
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
      expect(find.text('Смена № 42 закрыта · наличные 2 860'), findsOneWidget);
      // справа
      expect(find.text('Отменить'), findsOneWidget); // только цена
      expect(find.text('отменить нельзя'), findsOneWidget); // закрытие смены
      expect(find.text('нельзя отменить · можно списать'), findsOneWidget); // приход
      expect(find.text('отмена — запросом владельцу'), findsOneWidget); // выплата
      expect(find.text('отмена была только в звонке'), findsOneWidget); // корзина
      // значки групп: деньги — на жёлтой подложке
      expect(find.byIcon(Icons.payments_outlined), findsWidgets);
      expect(find.byIcon(Icons.inventory_2_outlined), findsOneWidget);
      expect(find.byIcon(Icons.shopping_cart_outlined), findsOneWidget);
      expect(find.byIcon(Icons.account_circle_outlined), findsWidgets); // зарплата (и «ваши действия»)
      expect(find.text('Хранится на телефоне, до 2000 записей. Без ключей ИИ, звука и телефонов клиентов.'), findsNothing); // подвал — в конце списка
      await t.drag(find.byType(ListView).last, const Offset(0, -2000));
      await t.pumpAndSettle();
      expect(find.text('Хранится на телефоне, до 2000 записей. Без ключей ИИ, звука и телефонов клиентов.'), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('«Только деньги» оставляет оплату, приход, выплату; «Отменённые» — пусто, пока ничего не отменено', (t) async {
      _phone(t);
      await seed(t, sample());
      await t.pumpWidget(app(_owner));
      await settle(t);
      await t.tap(find.text('Только деньги'));
      await t.pumpAndSettle();
      expect(find.text('Snickers: цена 110 → 120 сом'), findsNothing);
      expect(find.text('В чек: Кола ×2'), findsNothing);
      expect(find.text('Приход № 4512 на 12 400 сом'), findsOneWidget);
      expect(find.text('Смена № 42 закрыта · наличные 2 860'), findsOneWidget);
      expect(find.text('Выплата Айбеку Т. 18 500 сом наличными'), findsOneWidget);
      await t.tap(find.text('Отменённые'));
      await t.pumpAndSettle();
      expect(find.text('Ничего не найдено'), findsOneWidget);
      await t.tap(find.text('Все'));
      await t.pumpAndSettle();
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
    });

    testWidgets('сотрудник видит только свои записи и плашку «Видны только ваши действия»; кнопка — только если есть право', (t) async {
      _phone(t);
      await seed(t, sample());
      await t.pumpWidget(app(_cashierPrice));
      await settle(t);
      expect(find.text('Видны только ваши действия'), findsOneWidget);
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
      expect(find.text('В чек: Кола ×2'), findsOneWidget);
      expect(find.text('Смена № 42 закрыта · наличные 2 860'), findsNothing);
      expect(find.text('Приход № 4512 на 12 400 сом'), findsNothing);
      expect(find.text('Отменить'), findsOneWidget);
      // тот же журнал без права «Изменение цены»: кнопки нет, подписи нет (ничего не обещаем)
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.text('Отменить'), findsNothing);
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
    });

    testWidgets('«Отменить»: цена возвращается, тост «Отменено: …», строка «отменено ЧЧ:ММ», в журнале новая запись об отмене; фильтр «Отменённые»', (t) async {
      _phone(t);
      await seed(t, sample());
      await t.pumpWidget(app(_owner));
      await settle(t);
      await t.tap(find.text('Отменить'));
      await settle(t, until: () => find.textContaining('отменено ').evaluate().isNotEmpty);
      expect(host.drafts['p1']!.price, 110);
      expect(find.textContaining('Отменено: Snickers: цена 110 → 120 сом'), findsWidgets); // тост и новая запись
      expect(find.text('Отменить'), findsNothing);
      expect(find.textContaining('отменено ${hm(DateTime.now())}'), findsOneWidget);
      // второе нажатие невозможно; в файле — запись об отмене
      final lines = file.readAsLinesSync();
      expect(lines.last, contains('"tool":"undo_last"'));
      expect(lines.last, contains('undo:cp1'));
      await t.tap(find.text('Отменённые'));
      await t.pumpAndSettle();
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
      expect(find.text('Приход № 4512 на 12 400 сом'), findsNothing);
    });

    testWidgets('цену успели поменять → тост «Уже менялось после ИИ», запись остаётся с кнопкой', (t) async {
      _phone(t);
      await seed(t, sample());
      host.drafts['p1'] = host.drafts['p1']!.copyWith(price: 150);
      await t.pumpWidget(app(_owner));
      await settle(t);
      await t.tap(find.text('Отменить'));
      await settle(t, until: () => find.text('Уже менялось после ИИ — проверьте на экране').evaluate().isNotEmpty);
      expect(find.text('Уже менялось после ИИ — проверьте на экране'), findsOneWidget);
      expect(host.drafts['p1']!.price, 150);
      expect(find.text('Отменить'), findsOneWidget);
    });

    testWidgets('пусто: «ИИ ещё ничего не делал…», подсказка про звонок и кнопка «Позвонить советнику» (владельцу); кассиру кнопки нет', (t) async {
      _phone(t);
      await t.pumpWidget(app(_owner));
      await settle(t);
      expect(find.text('ИИ ещё ничего не делал на этом телефоне'), findsOneWidget);
      expect(find.textContaining('Позвоните Нур Советнику'), findsOneWidget);
      expect(find.text('Позвонить советнику'), findsOneWidget);
      await t.tap(find.text('Позвонить советнику'));
      await settle(t);
      expect(find.text('ADVISOR'), findsOneWidget);
      await t.pumpWidget(app(_cashier));
      await settle(t);
      expect(find.text('ИИ ещё ничего не делал на этом телефоне'), findsOneWidget);
      expect(find.text('Позвонить советнику'), findsNothing);
    });

    testWidgets('фильтр по дате: показывает записи одного дня', (t) async {
      _phone(t);
      await seed(t, sample());
      await t.pumpWidget(app(_owner));
      await settle(t);
      await t.tap(find.byTooltip('Фильтр по дате'));
      await t.pumpAndSettle();
      // выбор даты — сегодняшняя «ОК»
      await t.tap(find.text(MaterialLocalizations.of(t.element(find.byType(Scaffold).first)).okButtonLabel));
      await t.pumpAndSettle();
      expect(find.textContaining('Вчера · '), findsNothing);
      expect(find.text('Snickers: цена 110 → 120 сом'), findsOneWidget);
      expect(find.text('Смена № 42 закрыта · наличные 2 860'), findsNothing);
    });

    for (final lang in ['ru', 'ky']) {
      for (final scale in [1.0, 1.3]) {
        for (final dark in [false, true]) {
          testWidgets('раскладка без переполнения, кнопки ≥ 48 dp: $lang, шрифт $scale, тёмная=$dark', (t) async {
            _phone(t, scale: scale);
            await seed(t, sample());
            await t.pumpWidget(app(_owner, lang: lang, dark: dark));
            await settle(t);
            expect(t.takeException(), isNull);
            final l = lookupAppLocalizations(Locale(lang));
            final undoBtn = find.ancestor(of: find.text(l.aiUndo), matching: find.byType(InkWell)).first;
            expect(t.getSize(undoBtn).height, greaterThanOrEqualTo(48));
            for (final c in [l.filterAll, l.aiLogMoneyOnly, l.aiLogUndone]) {
              expect(t.getSize(find.ancestor(of: find.text(c), matching: find.byType(InkWell)).first).height, greaterThanOrEqualTo(48), reason: c);
            }
            await t.drag(find.byType(ListView).last, const Offset(0, -3000));
            await t.pumpAndSettle();
            expect(t.takeException(), isNull);
          });
        }
      }
    }
  });
}
