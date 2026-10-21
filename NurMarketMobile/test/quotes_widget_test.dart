// 2026-10-21: КП и счета — сценарий на экранах: Enter/«Готово» в окне количества и в поиске товара не удаляют строку
// и не добавляют дубль, двойное «Сохранить» — один документ, «Новый клиент» из КП сохраняется в NurCRM (фейк) и сразу
// выбирается, экран документа «2 × 85 = 170» и «Сделать счёт» один раз. Сервер не трогается — всё на фейках.
import 'dart:async';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/admin/quotes.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

/// Клиенты NurCRM без сети: запоминает созданных, [offline] — как обрыв связи.
class _FakeSales extends SalesRepository {
  _FakeSales(AppDatabase db) : super(ApiClient(SessionStore()), db);
  final existing = <Client>[];
  final created = <(String, String, String, String)>[];
  bool offline = false;

  @override
  Future<List<Client>> clients(String search) async {
    if (offline) throw ApiException('нет связи');
    return existing.where((c) => c.name.contains(search) || c.phone.replaceAll(RegExp(r'\D'), '').contains(search)).toList();
  }

  @override
  Future<Client> createClient(String name, String phone, {String company = '', String inn = ''}) async {
    if (offline) throw ApiException('нет связи');
    created.add((name, phone, company, inn));
    return Client('new-${created.length}', name, phone, company: company, inn: inn);
  }
}

/// Номер выдаётся медленно — чтобы успеть нажать «Сохранить» второй раз.
class _SlowStore extends QuotesStore {
  _SlowStore(super.db);
  int calls = 0;
  final gate = Completer<void>();
  @override
  Future<String> nextNumber(bool quote) async {
    calls++;
    await gate.future;
    return 'КП-${calls.toString().padLeft(4, '0')}';
  }
}

void main() {
  late AppDatabase db;
  late _FakeSales sales;
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    sales = _FakeSales(db);
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'w', name: 'Вода «Нур» 1,5 л', price: const Value(85), barcode: const Value('4870001112223'), searchKey: const Value('вода «нур» 1,5 л 4870001112223')),
      ProductsCompanion.insert(id: 's', name: 'Сок яблочный 1 л', price: const Value(140), searchKey: const Value('сок яблочный 1 л')),
    ]);
  });
  tearDown(() => db.close());

  Widget app(Widget home, {List overrides = const []}) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), salesRepoProvider.overrideWithValue(sales), ...overrides],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  QuoteDoc doc({double qty = 1}) => QuoteDoc(
    id: '1',
    number: 'КП-0001',
    isQuote: true,
    client: 'Асель',
    phone: '0700 123 456',
    lines: [QuoteLine(productId: 'w', name: 'Вода «Нур» 1,5 л', qty: qty, price: 85)],
    until: DateTime.now().add(const Duration(days: 7)),
    status: 'draft',
    createdAt: DateTime.now(),
  );

  /// Экран-хозяин: открывает [screen] и собирает, чем он закрылся.
  Widget host(Widget Function() screen, List<Object?> results) => Builder(
    builder: (c) => Scaffold(
      body: Center(
        child: ElevatedButton(
          onPressed: () async => results.add(await Navigator.of(c).push<Object?>(MaterialPageRoute(builder: (_) => screen()))),
          child: const Text('open'),
        ),
      ),
    ),
  );

  testWidgets('Enter в окне количества: число выделено и заменяется, двойной Enter и «Готово» не закрывают документ', (t) async {
    final results = <Object?>[];
    await t.pumpWidget(app(host(() => QuoteEditScreen(doc: doc(), quote: true), results)));
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    await t.ensureVisible(find.text('1 шт'));
    await t.pumpAndSettle();
    await t.tap(find.text('1 шт'));
    await t.pumpAndSettle();
    final field = t.widget<TextField>(find.descendant(of: find.byType(AlertDialog), matching: find.byType(TextField)));
    // Прежнее «1» выделено целиком — набор «2» даёт 2, а не 12.
    expect(field.controller!.selection, const TextSelection(baseOffset: 0, extentOffset: 1));
    await t.enterText(find.byWidget(field), '2');
    await t.testTextInput.receiveAction(TextInputAction.done);
    await t.testTextInput.receiveAction(TextInputAction.done);
    // «Готово» в том же кадре, пока окно ещё на экране.
    await t.tap(find.text('Готово'), warnIfMissed: false);
    await t.pumpAndSettle();
    expect(t.takeException(), isNull);
    expect(find.byType(QuoteEditScreen), findsOneWidget, reason: 'экран документа не закрылся');
    expect(results, isEmpty);
    expect(find.text('2 шт'), findsOneWidget);
    expect(find.text('Вода «Нур» 1,5 л'), findsOneWidget, reason: 'строка не удалена и не задвоена');
  });

  testWidgets('поиск товара: Enter со штрихкодом (сканер-клавиатура) выбирает товар один раз', (t) async {
    final picked = <Object?>[];
    await t.pumpWidget(
      app(
        Builder(
          builder: (c) => Scaffold(
            body: Center(
              child: ElevatedButton(onPressed: () async => picked.add(await pickProduct(c)), child: const Text('open')),
            ),
          ),
        ),
      ),
    );
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    await t.enterText(find.byType(TextField), '4870001112223');
    await t.testTextInput.receiveAction(TextInputAction.search);
    await t.testTextInput.receiveAction(TextInputAction.search);
    await t.pumpAndSettle();
    expect(picked.length, 1);
    expect((picked.single as Product).id, 'w');
    expect(find.text('open'), findsOneWidget, reason: 'экран под окном не закрыт');
  });

  testWidgets('добавить тот же товар дважды — количество 2 в одной строке; двойное «Сохранить» — один документ', (t) async {
    final store = _SlowStore(db);
    final results = <Object?>[];
    await t.pumpWidget(app(host(() => const QuoteEditScreen(quote: true), results), overrides: [quotesStoreProvider.overrideWithValue(store)]));
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    expect(find.text('Добавьте товары: поиск, сканер или из текущего чека.'), findsOneWidget);
    for (var i = 0; i < 2; i++) {
      await t.tap(find.text('Добавить товар'));
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(BottomSheet), matching: find.text('Вода «Нур» 1,5 л')));
      await t.pumpAndSettle();
    }
    expect(find.text('Вода «Нур» 1,5 л'), findsOneWidget);
    expect(find.text('2 шт'), findsOneWidget);
    expect(find.text('170 сом'), findsWidgets);
    await t.tap(find.text('Сохранить'));
    await t.pump(const Duration(milliseconds: 50));
    await t.tap(find.text('Сохранить'), warnIfMissed: false);
    await t.pump(const Duration(milliseconds: 50));
    store.gate.complete();
    await t.pumpAndSettle();
    expect(store.calls, 1, reason: 'номер выдан один раз');
    expect(results.length, 1);
    final d = results.single as QuoteDoc;
    expect(d.number, 'КП-0001');
    expect(d.lines.single.qty, 2);
    expect(d.total, 170);
    expect(find.text('open'), findsOneWidget, reason: 'второе закрытие не увело с экрана');
  });

  testWidgets('новый клиент из КП: имя, телефон, компания, ИНН — POST clients/ и сразу в документе', (t) async {
    final results = <Object?>[];
    await t.pumpWidget(app(host(() => const QuoteEditScreen(quote: true), results)));
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    await t.tap(find.text('Новый клиент'));
    await t.pumpAndSettle();
    final dialog = find.byType(AlertDialog);
    final fields = find.descendant(of: dialog, matching: find.byType(TextField));
    expect(fields, findsNWidgets(4));
    await t.enterText(fields.at(0), 'Бакыт');
    await t.enterText(fields.at(1), '0555 777 888');
    await t.enterText(fields.at(2), 'ОсОО «Үмүт»');
    await t.enterText(fields.at(3), '01234567890123');
    // Enter в последнем поле и сразу «Сохранить» — клиент создаётся один раз.
    await t.testTextInput.receiveAction(TextInputAction.done);
    await t.tap(find.descendant(of: dialog, matching: find.text('Сохранить')), warnIfMissed: false);
    await t.pumpAndSettle();
    expect(sales.created, [('Бакыт', '0555 777 888', 'ОсОО «Үмүт»', '01234567890123')]);
    expect(find.byType(AlertDialog), findsNothing);
    final edit = find.byType(QuoteEditScreen);
    String text(int i) => t.widget<TextField>(find.descendant(of: edit, matching: find.byType(TextField)).at(i)).controller!.text;
    expect(text(0), 'Бакыт');
    expect(text(1), 'ОсОО «Үмүт»');
    expect(text(2), '0555 777 888');
    expect(text(3), '01234567890123');
  });

  testWidgets('новый клиент: такой телефон уже есть — выбирается он, дубля в NurCRM нет', (t) async {
    sales.existing.add(const Client('c7', 'Бакыт Асанов', '+996 555 777 888', company: 'ОсОО «Үмүт»', inn: '01234567890123'));
    await t.pumpWidget(app(host(() => const QuoteEditScreen(quote: true), [])));
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    await t.tap(find.text('Новый клиент'));
    await t.pumpAndSettle();
    final fields = find.descendant(of: find.byType(AlertDialog), matching: find.byType(TextField));
    await t.enterText(fields.at(0), 'Бакыт');
    await t.enterText(fields.at(1), '0555777888');
    await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Сохранить')));
    await t.pumpAndSettle();
    expect(sales.created, isEmpty);
    final edit = find.byType(QuoteEditScreen);
    expect(t.widget<TextField>(find.descendant(of: edit, matching: find.byType(TextField)).at(0)).controller!.text, 'Бакыт Асанов');
    expect(t.widget<TextField>(find.descendant(of: edit, matching: find.byType(TextField)).at(3)).controller!.text, '01234567890123');
  });

  testWidgets('новый клиент без связи — записан только в документ, с понятным сообщением', (t) async {
    sales.offline = true;
    await t.pumpWidget(app(host(() => const QuoteEditScreen(quote: true), [])));
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
    await t.tap(find.text('Новый клиент'));
    await t.pumpAndSettle();
    final fields = find.descendant(of: find.byType(AlertDialog), matching: find.byType(TextField));
    await t.enterText(fields.at(0), 'Айгүл');
    await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text('Сохранить')));
    await t.pumpAndSettle();
    expect(find.text('Нет связи с NurCRM — клиент записан только в этот документ.'), findsOneWidget);
    final edit = find.byType(QuoteEditScreen);
    expect(t.widget<TextField>(find.descendant(of: edit, matching: find.byType(TextField)).at(0)).controller!.text, 'Айгүл');
  });

  testWidgets('экран документа: «2 шт × 85 =» и 170, «Сделать счёт» дважды — один счёт со своим номером', (t) async {
    await QuotesStore(db).save([doc(qty: 2)]);
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    expect(find.text('КП-0001'), findsOneWidget);
    await t.tap(find.text('КП-0001'));
    await t.pumpAndSettle();
    expect(find.byType(QuoteViewScreen), findsOneWidget);
    expect(find.text('2 шт × 85 ='), findsOneWidget);
    expect(find.text('170'), findsOneWidget);
    expect(find.text('PDF'), findsOneWidget);
    expect(find.text('Отправить'), findsOneWidget);
    await t.tap(find.text('Сделать счёт'));
    await t.tap(find.text('Сделать счёт'), warnIfMissed: false);
    await t.pumpAndSettle();
    final docs = await QuotesStore(db).load();
    expect(docs.where((d) => !d.isQuote).length, 1);
    final inv = docs.firstWhere((d) => !d.isQuote);
    expect(inv.number, 'СЧ-0001');
    expect(inv.basedOn, 'КП-0001');
    expect(inv.total, 170);
    expect(docs.firstWhere((d) => d.isQuote).status, 'accepted');
    // Экран показывает новый счёт: главная кнопка — «Принять оплату».
    expect(find.text('СЧ-0001'), findsOneWidget);
    expect(find.text('Принять оплату'), findsOneWidget);
  });

  testWidgets('удаление документа — только после подтверждения', (t) async {
    await QuotesStore(db).save([doc()]);
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    await t.tap(find.text('КП-0001'));
    await t.pumpAndSettle();
    await t.tap(find.byIcon(Icons.more_horiz_rounded));
    await t.pumpAndSettle();
    await t.tap(find.text('Удалить документ'));
    await t.pumpAndSettle();
    expect(find.text('Удалить КП-0001?'), findsOneWidget);
    await t.tap(find.text('Отмена'));
    await t.pumpAndSettle();
    expect((await QuotesStore(db).load()).length, 1);
  });

  testWidgets('список: пустое состояние с подсказкой', (t) async {
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    expect(find.text('Пока нет КП и счетов'), findsOneWidget);
    expect(find.text('Создать'), findsOneWidget);
  });

  testWidgets('список: поиск по телефону и номеру', (t) async {
    await QuotesStore(db).save([doc()]);
    await t.pumpWidget(app(const QuotesScreen()));
    await t.pumpAndSettle();
    await t.enterText(find.byType(TextField), '999');
    await t.pump();
    expect(find.text('Ничего не найдено — измените поиск или фильтр.'), findsOneWidget);
    await t.enterText(find.byType(TextField), '123456');
    await t.pump();
    expect(find.text('КП-0001'), findsOneWidget);
    await t.enterText(find.byType(TextField), 'кп-0001');
    await t.pump();
    expect(find.text('КП-0001'), findsOneWidget);
  });
}
