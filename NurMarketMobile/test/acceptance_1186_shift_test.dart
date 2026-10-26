// 2026-10-26 (приёмка 1.18.6): итоги смены — предоплата по долгу в «расчёте по строкам» (С-1н) и возвраты без кассира и смены
// в ответе сервера (С-2н). Деньги не выдумываются: чего не знаем — помечаем «частично». Сервер — фейк, сети нет.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/features/shift/shift_doc.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations_ky.dart';
import 'package:nurmarket/l10n/app_localizations_ru.dart';

typedef _H = Future<dynamic> Function(String path, Map<String, dynamic>? query);

class _Api extends ApiClient {
  _Api(this.h) : super(SessionStore());
  final _H h;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => h(path, query);
}

Map<String, dynamic> _flow(String kind, String type, num amount, {String pm = 'cash'}) => {'source_kind': kind, 'type': type, 'amount': '$amount', 'payment_method': pm};

void main() {
  final ru = AppLocalizationsRu();

  group('С-1н: предоплата по долгу', () {
    // Смена из приёмки: нач. 1000 + нал 225 − возврат 85 + оплата долга 60 + предоплата 50 = 1250 (сервер)
    final shift = ShiftInfo(id: 'sh1', openingCash: 1000, cashSales: 225, nonCashSales: 140, debtSales: 250, salesTotal: 615, expectedCash: 1250, salesCount: 3);
    final flows = [_flow('pos_sale_return', 'expense', 85)];

    test('отчёт без cash_sales (предоплата не внутри наличных): «Наличные» = 225 + 50, расчёт сходится с сервером — нет ложного «расходится на 50»', () {
      final b = ShiftBreakdown.build(
        shift: shift,
        report: {'expected_cash': '1250', 'debt_payments_cash': '60', 'debt_prepayments_cash': '50'},
        flows: flows,
        returns: (total: 85, count: 1),
      );
      expect(b.cashSales, 275);
      expect(b.calcExpected, 1250);
      expect(b.matches, isTrue);
      final text = shiftTapeDoc(ru, null, shift, b).toText();
      expect(text, isNot(contains('расходится с сервером')));
      expect(text, contains('предоплата по долгу'));
    });

    test('сервер уже прислал cash_sales с предоплатой (как на ПК) — предоплата не считается дважды', () {
      final b = ShiftBreakdown.build(
        shift: shift,
        report: {'expected_cash': '1250', 'debt_payments_cash': '60', 'debt_prepayments_cash': '50', 'cash_sales': '275', 'noncash_sales': '140'},
        flows: flows,
        returns: (total: 85, count: 1),
      );
      expect(b.cashSales, 275);
      expect(b.calcExpected, 1250);
      expect(b.matches, isTrue);
    });

    test('предоплаты нет — продажи как были', () {
      final b = ShiftBreakdown.build(
        shift: ShiftInfo(id: 'sh1', openingCash: 100, cashSales: 500, salesTotal: 500, expectedCash: 600),
        report: {'expected_cash': '600', 'debt_payments_cash': '0', 'debt_prepayments_cash': '0'},
        flows: [],
        returns: (total: 0, count: 0),
      );
      expect(b.cashSales, 500);
      expect(b.matches, isTrue);
    });

    test('настоящее расхождение по-прежнему видно', () {
      final b = ShiftBreakdown.build(
        shift: shift,
        report: {'expected_cash': '1300', 'debt_payments_cash': '60', 'debt_prepayments_cash': '50'},
        flows: flows,
        returns: (total: 85, count: 1),
      );
      expect(b.matches, isFalse);
      expect(b.diff, -50);
    });
  });

  group('С-2н: возвраты без кассира и смены', () {
    final opened = DateTime.now().subtract(const Duration(hours: 3));
    final shift = ShiftInfo(id: 'sh1', cashierId: 'u1', openedAt: opened, openingCash: 100, cashSales: 500, salesTotal: 500, expectedCash: 570);

    ShiftRepository repo(List<Map<String, dynamic>> rows) => ShiftRepository(
      _Api((p, q) async {
        if (p == 'api/main/pos/returns/') return rows;
        if (p.endsWith('/report/')) return {'sales_total': '500', 'expected_cash': '570', 'debt_payments_cash': '0'};
        if (p == 'api/construction/cashflows/') {
          return {
            'results': [_flow('pos_sale_return', 'expense', 30)],
          };
        }
        throw ApiException('Нет связи с сервером.');
      }),
    );

    final now = DateTime.now().toIso8601String();

    test('у возврата нет ни shift, ни cashier — он не пропадает: «безымянный», в сумму не входит', () async {
      final r = await repo([
        {'id': 'r1', 'amount': '30.00', 'created_at': now},
      ]).returns(shift);
      expect(r!.unattributed, 1);
      expect(r.count, 0);
      expect(r.total, 0);
    });

    test('смесь: свои по кассиру считаются, безымянные — отдельно, чужие кассиры и возвраты вне смены — игнор', () async {
      final r = await repo([
        {'id': 'r1', 'amount': '30.00', 'cashier': 'u1', 'created_at': now},
        {'id': 'r2', 'amount': '70.00', 'cashier': 'other', 'created_at': now},
        {'id': 'r3', 'amount': '10.00', 'created_at': now},
        {'id': 'r4', 'amount': '99.00', 'created_at': opened.subtract(const Duration(days: 2)).toIso8601String()},
      ]).returns(shift);
      expect(r!.count, 1);
      expect(r.total, 30);
      expect(r.unattributed, 1);
    });

    test('возвратов за смену нет вообще — 0 и без пометки (не выдумываем «частично»)', () async {
      final r = await repo([
        {'id': 'r9', 'amount': '99.00', 'cashier': 'u1', 'created_at': opened.subtract(const Duration(days: 2)).toIso8601String()},
      ]).returns(shift);
      expect(r!.count, 0);
      expect(r.unattributed, 0);
    });

    test('смена известна серверу — прежний путь по смене, безымянных нет', () async {
      final r = await repo([
        {'id': 'r1', 'amount': '30.00', 'shift': 'sh1', 'created_at': now},
        {'id': 'r2', 'amount': '5.00', 'shift': 'sh2', 'created_at': now},
      ]).returns(shift);
      expect(r!.count, 1);
      expect(r.unattributed, 0);
    });

    test('breakdown + лента: пометка «Частично», расчёт по строкам не строится, сумма возвратов не выдумана', () async {
      final b = await repo([
        {'id': 'r1', 'amount': '30.00', 'created_at': now},
        {'id': 'r2', 'amount': '45.00', 'created_at': now},
      ]).breakdown(shift);
      expect(b.returnsUnattributed, 2);
      expect(b.returnsCash, isNull);
      expect(b.calcExpected, isNull);
      expect(b.matches, isFalse);
      final text = shiftTapeDoc(ru, null, shift, b).toText();
      expect(text, contains('Частично: 2 возврата'));
      expect(text, isNot(contains('Возвраты смены не загружены')), reason: 'это не «нет связи»');
      expect(text, isNot(contains('Возвраты · ')), reason: 'суммы возвратов нет — не выдумываем');
      expect(text, contains('Должно быть в кассе: 570 сом'), reason: 'главное число — сервера');
      final ky = shiftTapeDoc(AppLocalizationsKy(), null, shift, b).toText();
      expect(ky, contains('Жарым-жартылай'));
    });

    test('отчёт смены сам считает возвраты (returns_total) — безымянные уже учтены сервером, пометки нет', () {
      final b = ShiftBreakdown.build(
        shift: shift,
        report: {'expected_cash': '570', 'returns_total': '30', 'returns_count': '1', 'debt_payments_cash': '0'},
        flows: [_flow('pos_sale_return', 'expense', 30)],
        returns: (total: 0, count: 0),
        returnsUnattributed: 2,
      );
      expect(b.returnsUnattributed, 0);
      expect(b.returnsTotal, 30);
    });
  });
}
