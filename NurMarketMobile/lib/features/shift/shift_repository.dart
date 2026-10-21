import '../../core/api/api_client.dart';

class Cashbox {
  final String id;
  final String name;
  const Cashbox(this.id, this.name);
}

/// Открытая смена кассира и её итоги (поля — как читает касса: ShiftBalanceHelper).
class ShiftInfo {
  final String id;
  final String? cashboxId;
  final DateTime? openedAt;
  final double openingCash;
  final double expectedCash;
  final double salesTotal;
  final double cashSales;
  final double nonCashSales;
  final double debtSales;
  final int salesCount;

  const ShiftInfo({
    required this.id,
    this.cashboxId,
    this.openedAt,
    this.openingCash = 0,
    this.expectedCash = 0,
    this.salesTotal = 0,
    this.cashSales = 0,
    this.nonCashSales = 0,
    this.debtSales = 0,
    this.salesCount = 0,
  });

  static double _first(Map<String, dynamic> m, List<String> keys) {
    for (final k in keys) {
      if (m[k] != null) return num0(m, k);
    }
    return 0;
  }

  static ShiftInfo fromJson(Map<String, dynamic> m) => ShiftInfo(
    id: str(m, 'id') ?? '',
    cashboxId: str(m, 'cashbox') ?? str(m, 'cashbox_id'),
    openedAt: DateTime.tryParse(str(m, 'opened_at') ?? '')?.toLocal(),
    openingCash: _first(m, ['opening_cash', 'start_cash']),
    expectedCash: _first(m, ['expected_cash', 'current_cash', 'cash_balance']),
    salesTotal: _first(m, ['sales_total', 'total_sales']),
    cashSales: _first(m, ['cash_sales_total', 'cash_sales']),
    nonCashSales: _first(m, ['noncash_sales_total', 'non_cash_sales_total', 'non_cash_sales']),
    debtSales: _first(m, ['debt_sales_total', 'debt_total']),
    salesCount: num0(m, 'sales_count').round(),
  );
}

/// Смены: api/construction/shifts/ (ТЗ, раздел 4). Смена своя у кассира на кассе; сервер сам закрывает смены в 01:00.
class ShiftRepository {
  final ApiClient api;
  ShiftRepository(this.api);

  Future<List<Cashbox>> cashboxes() async {
    final data = await api.get('api/construction/cashboxes/');
    return asList(data).map((m) => Cashbox(str(m, 'id') ?? '', str(m, 'name') ?? str(m, 'title') ?? 'Касса')).where((c) => c.id.isNotEmpty).toList();
  }

  /// Открытая смена этой кассы; null — смены нет.
  Future<ShiftInfo?> openShift(String? cashboxId) async {
    final data = await api.get('api/construction/shifts/', query: {'status': 'open'});
    final rows = asList(data).where((m) => (str(m, 'status') ?? 'open') == 'open').map(ShiftInfo.fromJson).where((s) => s.id.isNotEmpty).toList();
    if (rows.isEmpty) return null;
    if (cashboxId != null) {
      for (final s in rows) {
        if (s.cashboxId == cashboxId) return s;
      }
    }
    return rows.first;
  }

  Future<ShiftInfo> open(String cashboxId, double openingCash) async {
    final cash = openingCash.toStringAsFixed(2);
    try {
      return ShiftInfo.fromJson((await api.post('api/construction/shifts/open/', body: {'cashbox': cashboxId, 'opening_cash': cash})) as Map<String, dynamic>);
    } on ApiException catch (e) {
      if (e.status != 400) rethrow;
      return ShiftInfo.fromJson(
        (await api.post('api/construction/shifts/open/', body: {'cashbox_id': cashboxId, 'opening_cash': cash})) as Map<String, dynamic>,
      );
    }
  }

  Future<void> close(String shiftId, double? closingCash) =>
      api.post('api/construction/shifts/$shiftId/close/', body: {if (closingCash != null) 'closing_cash': closingCash.toStringAsFixed(2)});
}
