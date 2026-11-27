// 2026-11-26 (владелец: «при скане QR на чеке должен открываться красивый чек»): ссылка share_url сервера — разбор ответа,
// запасной запрос POST pos/sales/{id}/share-link/, офлайн-чек, экран «QR-код чека». Всё на фейках: сервер NurCRM не трогаем.
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/features/payment/receipt_qr.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _url = 'https://app.nurcrm.kg/r/AbCdEfGhIjKlMnOpQrStUvWxYz012345';

class _Api extends ApiClient {
  _Api(this.answer) : super(SessionStore());
  Future<dynamic> Function(String path) answer;
  final posts = <String>[];
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) {
    posts.add(path);
    return answer(path);
  }
}

Receipt _receipt({String? url, String? saleId, bool offline = false}) =>
    Receipt(shop: 'NBS', number: '1601', at: DateTime(2026, 11, 26, 21), lines: const [], total: 100, eReceiptUrl: url, saleId: saleId, offline: offline);

void main() {
  late AppDatabase db;
  setUp(() {
    SalesRepository.serverLacksShareLink = false;
    db = AppDatabase(NativeDatabase.memory());
  });
  tearDown(() => db.close());

  test('share_url из ответа: есть, null, пусто, мусор, вложенный data', () {
    expect(eReceiptUrl({'id': '7', 'share_url': _url}), _url);
    expect(eReceiptUrl({'share_url': '  $_url  '}), _url);
    expect(eReceiptUrl({'data': {'share_url': _url}}), _url);
    expect(eReceiptUrl({'share_url': null}), isNull);
    expect(eReceiptUrl({'share_url': ''}), isNull);
    expect(eReceiptUrl({'share_url': 'http://app.nurcrm.kg/r/x'}), isNull); // только https
    expect(eReceiptUrl({'share_url': 'javascript:alert(1)'}), isNull);
    expect(eReceiptUrl({'share_url': 'https://a.kg/r/a b'}), isNull);
    expect(eReceiptUrl({'share_url': 'https://a.kg/${'x' * 300}'}), isNull);
    // старые поля больше не читаем — QR только на красивую страницу
    expect(eReceiptUrl({'e_receipt_url': 'https://check.nurcrm.kg/abc', 'e_receipt': {'token': 'xyz'}}), isNull);
    expect(eReceiptUrl(null), isNull);
  });

  test('чек: ссылка из checkout; share_url: null — отозвана, id для запроса не хранится; без ключа — id есть', () {
    final withUrl = Receipt.fromSale(session: null, sale: {'id': '7', 'share_url': _url, 'items': []}, number: '1', total: 10, at: null, payment: 'cash');
    expect(withUrl.eReceiptUrl, _url);
    expect(withUrl.saleId, '7');
    expect(hasShareUrlKey({'share_url': null}), isTrue);
    expect(hasShareUrlKey({'id': '7'}), isFalse);
  });

  test('запасной запрос: POST share-link отдаёт url — чек получает ссылку', () async {
    final api = _Api((p) async => {'url': _url, 'token': 't', 'created': true});
    final r = await SalesRepository(api, db).withShareLink(_receipt(saleId: '42'));
    expect(api.posts, ['api/main/pos/sales/42/share-link/']);
    expect(r.eReceiptUrl, _url);
    expect(r.saleId, '42');
  });

  test('ссылка уже есть — запрос не уходит', () async {
    final api = _Api((p) async => throw StateError('не должен вызываться'));
    final r = await SalesRepository(api, db).withShareLink(_receipt(url: _url, saleId: '42'));
    expect(api.posts, isEmpty);
    expect(r.eReceiptUrl, _url);
  });

  test('офлайн-чек и чек без id — запроса нет, чек как был (без падений)', () async {
    final api = _Api((p) async => throw StateError('не должен вызываться'));
    final repo = SalesRepository(api, db);
    expect((await repo.withShareLink(_receipt(offline: true, saleId: '42'))).eReceiptUrl, isNull);
    expect((await repo.withShareLink(_receipt())).eReceiptUrl, isNull);
    expect(api.posts, isEmpty);
  });

  test('сбои: нет связи, ответ без url, мусорная ссылка — null; 404 — больше не спрашиваем', () async {
    var api = _Api((p) async => throw ApiException('Нет связи с сервером.'));
    expect(await SalesRepository(api, db).shareLink('1'), isNull);
    api = _Api((p) async => {'token': 'x'});
    expect(await SalesRepository(api, db).shareLink('1'), isNull);
    api = _Api((p) async => {'url': 'ftp://x'});
    expect(await SalesRepository(api, db).shareLink('1'), isNull);
    api = _Api((p) async => throw ApiException('Не найдено', status: 404));
    final repo = SalesRepository(api, db);
    expect(await repo.shareLink('1'), isNull);
    expect(await repo.shareLink('2'), isNull);
    expect(api.posts.length, 1);
  });

  test('«Поделиться»: «Ваш чек №…» на языке интерфейса и ссылка', () async {
    final ru = await AppLocalizations.delegate.load(const Locale('ru'));
    final en = await AppLocalizations.delegate.load(const Locale('en'));
    final r = _receipt(url: _url);
    expect(shareReceiptMessage(ru, r, _url), 'Ваш чек №001601\n$_url');
    expect(shareReceiptMessage(en, r, _url), 'Your receipt No. 001601\n$_url');
  });

  Future<void> pumpQr(WidgetTester tester, Receipt r, _Api api) async {
    final repo = SalesRepository(api, db);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [salesRepoProvider.overrideWithValue(repo)],
        child: MaterialApp(
          localizationsDelegates: const [AppLocalizations.delegate, ...GlobalMaterialLocalizations.delegates],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('ru'),
          home: ReceiptQrScreen(receipt: r, title: 'Чек'),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('экран QR: ссылка в чеке — QR, «Открыть чек» и «Поделиться», запросов нет', (tester) async {
    final api = _Api((p) async => throw StateError('не должен вызываться'));
    await pumpQr(tester, _receipt(url: _url, saleId: '42'), api);
    expect(find.byKey(const Key('receiptQrImage')), findsOneWidget);
    expect(find.byKey(const Key('receiptOpenLink')), findsOneWidget);
    expect(find.byKey(const Key('receiptShareLink')), findsOneWidget);
    expect(api.posts, isEmpty);
  });

  testWidgets('экран QR: ссылки нет — запрашивает share-link и рисует QR', (tester) async {
    final api = _Api((p) async => {'url': _url});
    await pumpQr(tester, _receipt(saleId: '42'), api);
    expect(api.posts, ['api/main/pos/sales/42/share-link/']);
    expect(find.byKey(const Key('receiptQrImage')), findsOneWidget);
  });

  testWidgets('экран QR: офлайн-чек — подсказка про синхронизацию, QR с текстом чека не рисуется', (tester) async {
    final api = _Api((p) async => throw StateError('не должен вызываться'));
    await pumpQr(tester, _receipt(offline: true), api);
    expect(find.byKey(const Key('receiptQrImage')), findsNothing);
    expect(find.byKey(const Key('receiptQrNoLink')), findsOneWidget);
    expect(find.textContaining('после синхронизации'), findsOneWidget);
    expect(api.posts, isEmpty);
  });

  testWidgets('экран QR: нет связи при запросе — подсказка и «Повторить», без падения', (tester) async {
    final api = _Api((p) async => throw ApiException('Нет связи с сервером.'));
    await pumpQr(tester, _receipt(saleId: '42'), api);
    expect(find.byKey(const Key('receiptQrImage')), findsNothing);
    expect(find.byKey(const Key('receiptQrNoLink')), findsOneWidget);
    expect(find.text('Повторить'), findsOneWidget);
  });
}
