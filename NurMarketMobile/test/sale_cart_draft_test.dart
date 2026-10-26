// 2026-10-26 (живой аудит, П3): незавершённый чек переживает перезапуск (cart.draft в настройках телефона, схема БД не
// менялась), стирается после оплаты/очистки и не переходит к чужому аккаунту; на экране продажи «скан → не найден → скан»
// учитывает оба кода, у админа есть «Создать товар с этим кодом», пустой чек с подсказкой, плитка с остатком и «×2».
// Сервер не трогается — запросы идут в фейк.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'results': <dynamic>[]};
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

Session _session(String email, {String role = 'cashier', bool owner = false}) =>
    Session(email: email, access: 'a', refresh: 'r', userId: 'u', userName: 'Тест', role: role, isOwner: owner, cashboxId: 'b', perms: owner ? null : const []);

Cart _cart() => Cart(
  lines: [
    CartLine(productId: 'p1', name: 'Вода', price: 100, qty: 2, discount: 10, wholesalePrice: 80),
    CartLine(productId: 'p2', name: 'Яблоки', price: 120, qty: 1.25, isWeight: true, unit: 'кг'),
  ],
  orderDiscountPercent: 5,
);

/// Ждём, пока цепочка асинхронных записей в базу дойдёт до конца.
Future<void> _settle() async {
  for (var i = 0; i < 20; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// Настоящий шрифт приложения: тестовый Ahem шире Montserrat и давал бы ложное переполнение.
Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'w', name: 'Вода «Нур»', price: const Value(85), quantity: const Value(12), barcode: const Value('4870001112223'), searchKey: const Value('вода «нур» 4870001112223')),
      ProductsCompanion.insert(id: 's', name: 'Сок', price: const Value(140), quantity: const Value(0), barcode: const Value('4870001119999'), searchKey: const Value('сок 4870001119999')),
    ]);
  });
  tearDown(() => db.close());

  ProviderContainer container(Session s) {
    final c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), sessionProvider.overrideWith(() => _Fixed(s))]);
    addTearDown(c.dispose);
    return c;
  }

  group('чек переживает перезапуск', () {
    test('записывается, восстанавливается с оптом и скидкой, стирается после очистки', () async {
      final a = container(_session('a@x.kg'));
      a.read(cartProvider.notifier).restore(_cart());
      await _settle();
      expect((await db.getSetting('cart.draft')) ?? '', isNotEmpty, reason: 'чек записан при изменении');

      // «перезапуск»: новый контейнер с той же базой и тем же аккаунтом
      final b = container(_session('A@x.kg')); // логин без учёта регистра, как у очереди
      expect(b.read(cartProvider).isEmpty, isTrue);
      await b.read(cartProvider.notifier).restoreDraft();
      final back = b.read(cartProvider);
      expect(back.lines.length, 2);
      expect(back.lines[0].qty, 2);
      expect(back.lines[0].discount, 10);
      expect(back.lines[1].isWeight, isTrue);
      expect(back.orderDiscountPercent, 5);
      expect(back.total, _cart().total);

      // оплата/отложение/очистка стирают черновик
      b.read(cartProvider.notifier).clear();
      await _settle();
      expect((await db.getSetting('cart.draft')) ?? '', isEmpty);
      final c = container(_session('a@x.kg'));
      await c.read(cartProvider.notifier).restoreDraft();
      expect(c.read(cartProvider).isEmpty, isTrue);
    });

    test('чужой аккаунт чек не получает; в непустой чек восстановление не пишет', () async {
      final a = container(_session('a@x.kg'));
      a.read(cartProvider.notifier).restore(_cart());
      await _settle();

      final other = container(_session('b@x.kg'));
      await other.read(cartProvider.notifier).restoreDraft();
      expect(other.read(cartProvider).isEmpty, isTrue, reason: 'чек другого аккаунта');

      final same = container(_session('a@x.kg'));
      same.read(cartProvider.notifier).restore(Cart(lines: [CartLine(productId: 'z', name: 'Новый', price: 5)]));
      await same.read(cartProvider.notifier).restoreDraft();
      expect(same.read(cartProvider).lines.single.name, 'Новый', reason: 'то, что уже набрано, не затирается');
    });

    test('испорченный черновик не мешает', () async {
      await db.setSetting('cart.draft', '{не json');
      final a = container(_session('a@x.kg'));
      await a.read(cartProvider.notifier).restoreDraft();
      expect(a.read(cartProvider).isEmpty, isTrue);
    });
  });

  group('экран продажи', () {
    Widget app(Session s) => ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        apiProvider.overrideWithValue(_Api()),
        sessionProvider.overrideWith(() => _Fixed(s)),
        shiftProvider.overrideWith(_OpenShift.new),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: const Scaffold(body: SaleScreen()),
      ),
    );

    void phone(WidgetTester t) {
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(t.view.resetPhysicalSize);
      addTearDown(t.view.resetDevicePixelRatio);
    }

    Future<void> scan(WidgetTester t, String code) async {
      await t.enterText(find.byType(TextField).first, code);
      await t.testTextInput.receiveAction(TextInputAction.search);
      await t.pumpAndSettle(const Duration(milliseconds: 100));
    }

    ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(SaleScreen)));

    testWidgets('скан → «не найден» → скан: поле очищено, второй товар попал в чек', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      await scan(t, '9990000000017');
      expect(t.widget<TextField>(find.byType(TextField).first).controller!.text, isEmpty, reason: 'код не остался в поле');
      expect(find.textContaining('9990000000017'), findsOneWidget); // «Штрихкод … не найден»
      expect(of(t).read(cartProvider).isEmpty, isTrue);

      await scan(t, '4870001112223');
      expect(of(t).read(cartProvider).lines.single.productId, 'w');
      expect(t.widget<TextField>(find.byType(TextField).first).controller!.text, isEmpty);
      await t.pumpAndSettle(const Duration(seconds: 5));
    });

    testWidgets('кассиру «Создать товар» не предлагается', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      await scan(t, '9990000000017');
      expect(find.textContaining('9990000000017'), findsOneWidget);
      expect(find.text('Создать товар с этим кодом'), findsNothing);
      await t.pumpAndSettle(const Duration(seconds: 15));
    });

    testWidgets('владельцу после «не найден» предлагается «Создать товар с этим кодом»', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('o@x.kg', role: 'owner', owner: true)));
      await t.pumpAndSettle();
      await scan(t, '9990000000018');
      expect(find.text('Создать товар с этим кодом'), findsOneWidget);
      await t.pumpAndSettle(const Duration(seconds: 15));
    });

    testWidgets('плитка: остаток «12 шт» / «нет», значок «×2» у товара в чеке; «×» очищает поиск', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      expect(find.text('12 шт'), findsOneWidget);
      expect(find.text('нет'), findsOneWidget);
      expect(find.textContaining('×'), findsNothing);
      final p = (await db.productById('w'))!;
      of(t).read(cartProvider.notifier).add(p, qty: 2);
      await t.pumpAndSettle();
      expect(find.text('×2'), findsOneWidget);

      await t.enterText(find.byType(TextField).first, 'сок');
      await t.pumpAndSettle(const Duration(milliseconds: 400));
      await t.tap(find.byIcon(Icons.close_rounded));
      await t.pumpAndSettle(const Duration(milliseconds: 400));
      expect(t.widget<TextField>(find.byType(TextField).first).controller!.text, isEmpty);
    });

    testWidgets('после оплаты (чек опустел) поиск очищается; пустой чек — подсказка и «К товарам»', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      final p = (await db.productById('w'))!;
      of(t).read(cartProvider.notifier).add(p);
      await t.enterText(find.byType(TextField).first, 'сок');
      await t.pumpAndSettle(const Duration(milliseconds: 400));
      of(t).read(cartProvider.notifier).clear(); // как после оплаты
      await t.pumpAndSettle(const Duration(milliseconds: 400));
      expect(t.widget<TextField>(find.byType(TextField).first).controller!.text, isEmpty);

      await t.tap(find.textContaining('Чек · 0'));
      await t.pumpAndSettle();
      expect(find.text('Добавьте товар: найдите, отсканируйте или нажмите плитку'), findsOneWidget);
      await t.tap(find.text('К товарам'));
      await t.pumpAndSettle();
      expect(find.text('Товар или штрихкод'), findsOneWidget);
    });

    testWidgets('чек и плитки при шрифте 1,3× на 360 dp — без переполнения', (t) async {
      phone(t);
      t.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(t.platformDispatcher.clearTextScaleFactorTestValue);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      of(t).read(cartProvider.notifier).restore(_cart());
      await t.pumpAndSettle();
      await t.tap(find.textContaining('Чек · 2'));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
    });

    testWidgets('строка чека: «−» неактивна при количестве 1, кнопки не меньше 48 dp, корзина одна', (t) async {
      phone(t);
      await t.pumpWidget(app(_session('a@x.kg')));
      await t.pumpAndSettle();
      final p = (await db.productById('w'))!;
      final c = of(t).read(cartProvider.notifier);
      c.add(p);
      await t.pumpAndSettle();
      await t.tap(find.textContaining('Чек · 1'));
      await t.pumpAndSettle();
      expect(find.byIcon(Icons.delete_outline_rounded), findsOneWidget);
      await t.tap(find.byIcon(Icons.remove));
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.single.qty, 1, reason: '«−» при 1 не убирает строку');
      for (final icon in [Icons.remove, Icons.add]) {
        final size = t.getSize(find.ancestor(of: find.byIcon(icon), matching: find.byType(InkWell)).first);
        expect(size.width, greaterThanOrEqualTo(48));
        expect(size.height, greaterThanOrEqualTo(48));
      }
      await t.tap(find.byIcon(Icons.add));
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.single.qty, 2);
    });
  });
}
