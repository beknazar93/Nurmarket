// 2026-10-26 (приёмка 1.18.6): экран продажи — красная плашка «Штрихкод не найден» уходит сама и не закрывает «Оплатить» (В-2н),
// строка чека компактнее, при этом касание −/+ и «опт» не меньше 48 dp (С-6н), «Розница/Опт» и чипы категорий — 48 dp (Н-6н).
// Фейки: база в памяти, фейковый клиент; сервер и боевая база не трогаются.
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

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u', userName: 'Тест', role: 'owner', isOwner: true, cashboxId: 'b');

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
      ProductsCompanion.insert(id: 'w', name: 'Вода «Нур» негазированная 1,5 л', price: const Value(85), quantity: const Value(12), barcode: const Value('4870001112223'), searchKey: const Value('вода «нур» 4870001112223')),
      ProductsCompanion.insert(id: 's', name: 'Сок', price: const Value(140), quantity: const Value(5), barcode: const Value('4870001119999'), searchKey: const Value('сок 4870001119999')),
    ]);
  });
  tearDown(() => db.close());

  Widget app({String lang = 'ru'}) => ProviderScope(
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(_Api()),
      sessionProvider.overrideWith(() => _Fixed(_owner)),
      shiftProvider.overrideWith(_OpenShift.new),
    ],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: const Scaffold(body: SaleScreen()),
    ),
  );

  void phone(WidgetTester t, {double scale = 1.0, Size size = const Size(720, 1280)}) {
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

  Future<void> scan(WidgetTester t, String code) async {
    await t.enterText(find.byType(TextField).first, code);
    await t.testTextInput.receiveAction(TextInputAction.search);
    await t.pump(const Duration(milliseconds: 300));
  }

  ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(SaleScreen)));

  group('В-2н: плашка «Штрихкод не найден»', () {
    testWidgets('не «висит»: уходит сама через несколько секунд; кнопка «Создать товар» не держит её', (t) async {
      phone(t);
      await t.pumpWidget(app());
      await t.pumpAndSettle();
      await scan(t, '9990000000017');
      final l = lookupAppLocalizations(const Locale('ru'));
      expect(find.text(l.createProductWithCode), findsOneWidget, reason: 'владельцу предложено создать товар');
      expect(find.byType(SnackBar), findsOneWidget);
      final bar = t.widget<SnackBar>(find.byType(SnackBar));
      expect(bar.persist, isFalse, reason: 'с кнопкой действия плашка по умолчанию не пропадала — теперь persist: false');
      expect(bar.duration, lessThanOrEqualTo(const Duration(seconds: 8)));
      // кадры идут по частям: таймер плашки заводится после того, как она выехала целиком
      for (var i = 0; i < 4; i++) {
        await t.pump(const Duration(milliseconds: 300));
      }
      expect(find.byType(SnackBar), findsOneWidget, reason: 'в первые секунды плашка ещё видна — прочитать успеют');
      await t.pump(const Duration(seconds: 5));
      await t.pump(const Duration(milliseconds: 600)); // анимация ухода
      expect(find.byType(SnackBar), findsNothing, reason: 'плашка ушла сама');
    });

    testWidgets('плашка стоит над строкой «Оплатить», а не на ней; пропадает при вводе в поиск', (t) async {
      phone(t);
      await t.pumpWidget(app());
      await t.pumpAndSettle();
      of(t).read(cartProvider.notifier).add((await db.productById('w'))!);
      await t.pump();
      await scan(t, '9990000000017');
      await t.pump(const Duration(milliseconds: 400));
      final l = lookupAppLocalizations(const Locale('ru'));
      final snack = t.getRect(find.textContaining('9990000000017'));
      final pay = t.getRect(find.widgetWithText(FilledButton, l.payAmount('85')));
      expect(snack.bottom, lessThanOrEqualTo(pay.top + 0.5), reason: '«Оплатить» не закрыта плашкой');
      // следующее действие (ввод в поиск) убирает плашку сразу
      await t.enterText(find.byType(TextField).first, 'со');
      await t.pump();
      await t.pump(const Duration(milliseconds: 400));
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('следующий скан убирает прежнюю плашку', (t) async {
      phone(t);
      await t.pumpWidget(app());
      await t.pumpAndSettle();
      await scan(t, '9990000000017');
      expect(find.textContaining('9990000000017'), findsOneWidget);
      await scan(t, '4870001112223');
      await t.pump(const Duration(milliseconds: 500));
      expect(find.textContaining('9990000000017'), findsNothing);
      expect(of(t).read(cartProvider).lines.single.productId, 'w');
      await t.pumpAndSettle(const Duration(seconds: 6));
    });
  });

  group('С-6н и Н-6н: строка чека и касания', () {
    for (final (lang, scale) in [('ru', 1.0), ('ky', 1.3)]) {
      testWidgets('$lang, шрифт $scale, 360 dp: строка чека с «опт» компактнее (было 175 dp), касания −/+/«опт»/«%»/корзина ≥ 48 dp', (t) async {
        phone(t, scale: scale);
        await t.pumpWidget(app(lang: lang));
        await t.pumpAndSettle();
        final c = of(t).read(cartProvider.notifier);
        c.restore(Cart(lines: [CartLine(productId: 'w', name: 'Вода «Нур» негазированная 1,5 л', price: 85, qty: 2, wholesalePrice: 70)]));
        await t.pump();
        final l = lookupAppLocalizations(Locale(lang));
        // вкладка «Чек»: сегмент второй
        await t.tap(find.byIcon(Icons.receipt_long_rounded));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        final rowH = t.getSize(find.byType(Dismissible).first).height; // с полями 4 + 4
        expect(rowH, lessThanOrEqualTo(scale > 1 ? 165 : 150), reason: 'строка чека компактная (раньше 175 + поля)');
        for (final icon in [Icons.remove, Icons.add, Icons.percent_rounded, Icons.delete_outline_rounded]) {
          final size = t.getSize(find.ancestor(of: find.byIcon(icon), matching: find.byWidgetPredicate((w) => w is InkWell || w is IconButton)).first);
          expect(size.height, greaterThanOrEqualTo(48), reason: '$icon');
          expect(size.width, greaterThanOrEqualTo(48), reason: '$icon');
        }
        final opt = find.ancestor(of: find.text(l.wholesaleTag), matching: find.byType(InkWell)).first;
        expect(t.getSize(opt).height, greaterThanOrEqualTo(48));
        expect(t.getSize(opt).width, greaterThanOrEqualTo(48));
        // «Розница / Опт»
        final seg = t.getSize(find.byType(SegmentedButton<bool>).last);
        expect(seg.height, greaterThanOrEqualTo(48), reason: '«Розница / Опт»');
        // «опт» всё ещё работает
        await t.tap(opt);
        await t.pump();
        expect(of(t).read(cartProvider).lines.single.wholesale, isTrue);
      });

      testWidgets('$lang, шрифт $scale: чипы категорий — область касания ≥ 48 dp', (t) async {
        phone(t, scale: scale);
        await t.pumpWidget(app(lang: lang));
        await t.pumpAndSettle();
        final chips = find.byType(ChoiceChip);
        expect(chips, findsWidgets);
        for (var i = 0; i < chips.evaluate().length && i < 2; i++) {
          expect(t.getSize(chips.at(i)).height, greaterThanOrEqualTo(48), reason: 'чип $i');
        }
      });
    }
  });
}
