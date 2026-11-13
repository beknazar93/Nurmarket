import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/util/price_limits.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';

/// 2026-11-11, ответ Умара (сервер NurCRM): пределы цен — продажа/опт ≤ 9 999 999,999; закупка ≤ 9 999 999 999,9999.
/// Ровно предел — можно, предел + 0,001 — нельзя.
void main() {
  test('границы: ровно предел проходит, предел + шаг — нет', () {
    expect(PriceLimits.saleExceeds(9999999.999), isFalse);
    expect(PriceLimits.saleExceeds(10000000.0), isTrue);
    expect(PriceLimits.saleExceeds(0), isFalse);
    expect(PriceLimits.purchaseExceeds(9999999999.9999), isFalse);
    expect(PriceLimits.purchaseExceeds(10000000000.0), isTrue);
    expect(PriceLimits.saleExceeds(double.nan), isTrue);
    expect(PriceLimits.saleExceeds(double.infinity), isTrue);
  });

  test('problemKind: продажа и опт — один предел, закупка — свой', () {
    expect(PriceLimits.problemKind(sale: 9999999.999, wholesale: 9999999.999, purchase: 9999999999.9999), isNull);
    expect(PriceLimits.problemKind(sale: 10000000), 'sale');
    expect(PriceLimits.problemKind(wholesale: 10000000), 'sale');
    expect(PriceLimits.problemKind(purchase: 10000000000), 'purchase');
    // закупка 20 млн — в пределах закупки, но продажа выше своего предела
    expect(PriceLimits.problemKind(sale: 20000000, purchase: 20000000), 'sale');
    expect(PriceLimits.problemKind(purchase: 20000000), isNull);
  });

  testWidgets('текст называет предел (ru и en)', (tester) async {
    late BuildContext ctx;
    await tester.pumpWidget(Localizations(locale: const Locale('en'), delegates: const [DefaultWidgetsLocalizations.delegate], child: Builder(builder: (c) {
      ctx = c;
      return const SizedBox();
    })));
    await tester.pumpAndSettle();
    expect(PriceLimits.saleText(ctx), 'The price cannot be more than 9 999 999,999 som');
    expect(PriceLimits.purchaseText(ctx), contains('9 999 999 999,9999'));
    expect(PriceLimits.problem(ctx, sale: 10000000), PriceLimits.saleText(ctx));
    expect(PriceLimits.problem(ctx, purchase: 10000000000), PriceLimits.purchaseText(ctx));
    expect(PriceLimits.problem(ctx, sale: 1, purchase: 1), isNull);

    await tester.pumpWidget(Localizations(locale: const Locale('ru'), delegates: const [DefaultWidgetsLocalizations.delegate], child: Builder(builder: (c) {
      ctx = c;
      return const SizedBox();
    })));
    await tester.pumpAndSettle();
    expect(PriceLimits.saleText(ctx), 'Цена не может быть больше 9 999 999,999 сом');
  });

  test('массовая загрузка: строка с ценой выше предела не загружается', () {
    expect(const ImportRow(name: 'Вода', price: 9999999.999, cost: 25).problems, isEmpty);
    expect(const ImportRow(name: 'Вода', price: 10000000, cost: 25).problems, [RowProblem.priceTooBig]);
    expect(const ImportRow(name: 'Вода', price: 30, cost: 10000000000).problems, [RowProblem.priceTooBig]);
    expect(const ImportRow(name: 'Вода', price: 30, cost: 9999999999.9999).problems, isEmpty);
  });
}
