// 2026-11-26: сводка ИИ берёт «Продажи» без таблицы остатков (include_stock=0) — сервер Умара; ответ без tables/products_stock разбирается.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/features/admin/analytics.dart';

class _Api extends ApiClient {
  _Api(this.reply) : super(SessionStore());
  final Map<String, dynamic> reply;
  final queries = <Map<String, dynamic>>[];

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    queries.add({...?query});
    return reply;
  }
}

void main() {
  test('tab=sales просит include_stock=0, tab=products — нет; без tables разбор не падает', () async {
    final api = _Api({
      'cards': {'revenue': '1000', 'transactions': 4, 'gross_profit': '300'},
      'charts': {'sales_dynamics': [{'date': '2026-11-26', 'value': 1000}], 'payment_methods': []},
    });
    final repo = AnalyticsRepository(api);
    final s = await repo.sales(DateTime(2026, 11, 26), DateTime(2026, 11, 26));
    expect(api.queries.single['tab'], 'sales');
    expect(api.queries.single['include_stock'], '0');
    expect(s.revenue, 1000);
    expect(s.checks, 4);
    expect(s.byDay.length, 1);

    await repo.products(DateTime(2026, 11, 20), DateTime(2026, 11, 26));
    expect(api.queries.last['tab'], 'products');
    expect(api.queries.last.containsKey('include_stock'), isFalse);
  });
}
