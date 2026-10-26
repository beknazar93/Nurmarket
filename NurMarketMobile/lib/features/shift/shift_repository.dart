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

  /// 2026-10-22 (мобилка, деньги, К-1): кто открыл смену (cashier/user/opened_by сервера) — своя смена только у этого кассира.
  final String? cashierId;
  final String? cashboxName;

  const ShiftInfo({
    required this.id,
    this.cashboxId,
    this.cashierId,
    this.cashboxName,
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
    cashierId: str(m, 'cashier') ?? str(m, 'cashier_id') ?? str(m, 'user') ?? str(m, 'opened_by'),
    cashboxName: str(m, 'cashbox_name'),
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

class ShiftLookup {
  final ShiftInfo? shift;

  /// Своя смена нашлась на другой кассе — сессия переходит на её кассу.
  final bool movedCashbox;

  /// На этой кассе открыта смена другого кассира (своей нет) — подсказка «откройте свою».
  final bool foreignOnThisCashbox;
  const ShiftLookup(this.shift, {this.movedCashbox = false, this.foreignOnThisCashbox = false});
}

/// Смены: api/construction/shifts/ (ТЗ, раздел 4). Смена своя у кассира на кассе; сервер сам закрывает смены в 01:00.
class ShiftRepository {
  final ApiClient api;
  ShiftRepository(this.api);

  Future<List<Cashbox>> cashboxes() async {
    final data = await api.get('api/construction/cashboxes/');
    return asList(data).map((m) => Cashbox(str(m, 'id') ?? '', str(m, 'name') ?? str(m, 'title') ?? 'Касса')).where((c) => c.id.isNotEmpty).toList();
  }

  /// Открытая смена ЭТОГО кассира на этой кассе; null — своей смены нет (предложить открыть).
  /// 2026-10-22 (мобилка, деньги, К-1): как ShiftHelper.PickOpenShiftId кассы. Раньше бралась первая открытая смена
  /// компании — продажи кассира Б уходили в смену кассира А на другой кассе. Сервер продаёт только в своей смене
  /// кассира и рядом с чужой открыть свою разрешает. Своя смена этого кассира на другой кассе — берётся вместе с кассой
  /// ([ShiftLookup.movedCashbox]); смена другого кассира — никогда.
  Future<ShiftInfo?> openShift(String? cashboxId, {String? cashierId}) async => (await findShift(cashboxId, cashierId: cashierId)).shift;

  Future<ShiftLookup> findShift(String? cashboxId, {String? cashierId}) async {
    final data = await api.get('api/construction/shifts/', query: {'status': 'open'});
    final rows = asList(data).where((m) => (str(m, 'status') ?? 'open') == 'open').map(ShiftInfo.fromJson).where((s) => s.id.isNotEmpty).toList();
    return pickShift(rows, cashboxId: cashboxId, cashierId: cashierId);
  }

  static ShiftLookup pickShift(List<ShiftInfo> rows, {String? cashboxId, String? cashierId}) {
    final me = (cashierId ?? '').trim();
    bool other(ShiftInfo s) => me.isNotEmpty && (s.cashierId ?? '').isNotEmpty && s.cashierId!.trim().toLowerCase() != me.toLowerCase();
    final own = rows.where((s) => !other(s)).toList();
    if (cashboxId == null || cashboxId.isEmpty) return ShiftLookup(own.firstOrNull);
    for (final s in own) {
      if (s.cashboxId == cashboxId) return ShiftLookup(s);
    }
    final foreignHere = rows.any((s) => other(s) && s.cashboxId == cashboxId);
    // Своя смена — на другой кассе (кассу сменили): берём её вместе с кассой. Только если кассир сервером указан.
    if (me.isNotEmpty) {
      for (final s in own) {
        if ((s.cashierId ?? '').trim().toLowerCase() == me.toLowerCase() && (s.cashboxId ?? '').isNotEmpty) {
          return ShiftLookup(s, movedCashbox: true, foreignOnThisCashbox: foreignHere);
        }
      }
    }
    return ShiftLookup(null, foreignOnThisCashbox: foreignHere);
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

  // ------------------------------------------------------------------ 2026-10-26 (живой аудит, П6): разбивка смены

  /// Отчёт смены одним запросом (как у кассы: ServerShiftReport). null — старый сервер без отчёта.
  Future<Map<String, dynamic>?> report(String shiftId) async {
    try {
      final r = await api.get('api/construction/shifts/$shiftId/report/');
      if (r is Map && r['sales_total'] != null) return r.cast<String, dynamic>();
      return null;
    } on ApiException catch (e) {
      if (e.status == 404) return null;
      rethrow;
    }
  }

  /// Возвраты смены (число и сумма). Как касса (ReturnsForShiftAsync): сервер у возврата поле смены пока не заполняет, поэтому
  /// по полю shift, а если его нет ни у кого — по кассиру смены и времени между открытием и «сейчас».
  /// null — не удалось определить (нет кассира смены при пустом поле shift).
  /// 2026-10-26 (приёмка 1.18.6, С-2н): `unattributed` — возвраты за время смены, у которых сервер не прислал ни смены, ни кассира:
  /// чьи они, не известно. Раньше они молча считались «чужими» и возвраты смены выходили «0»; теперь они не выдумываются в сумму,
  /// но и не пропадают — экран пишет «Частично: N возвратов без кассира не учтены».
  Future<({double total, int count, int unattributed})?> returns(ShiftInfo s) async {
    final opened = s.openedAt;
    if (opened == null) return null;
    String d(DateTime x) => '${x.year}-${x.month.toString().padLeft(2, '0')}-${x.day.toString().padLeft(2, '0')}';
    final rows = <Map<String, dynamic>>[];
    final seen = <String>{};
    for (var page = 1; page <= 10; page++) {
      final data = await api.get(
        'api/main/pos/returns/',
        query: {'date_from': d(opened.subtract(const Duration(days: 1))), 'date_to': d(DateTime.now().add(const Duration(days: 1))), if (page > 1) 'page': page},
      );
      var added = 0;
      for (final m in asList(data)) {
        final id = str(m, 'id');
        if (id != null && !seen.add(id)) continue;
        rows.add(m);
        added++;
      }
      if (data is! Map || data['next'] == null || added == 0) break;
    }
    final byShift = rows.where((r) => str(r, 'shift') == s.id).toList();
    List<Map<String, dynamic>> chosen;
    var unattributed = 0;
    if (byShift.isNotEmpty || rows.any((r) => (str(r, 'shift') ?? '').isNotEmpty)) {
      chosen = byShift;
    } else {
      final me = (s.cashierId ?? '').trim().toLowerCase();
      if (me.isEmpty) return null;
      final now = DateTime.now();
      chosen = [];
      for (final r in rows) {
        final at = DateTime.tryParse(str(r, 'created_at') ?? '')?.toLocal();
        if (at != null && (at.isBefore(opened) || at.isAfter(now))) continue; // вне времени смены
        final who = (str(r, 'cashier') ?? '').trim().toLowerCase();
        if (who.isEmpty || at == null) {
          unattributed++; // ни кассира, ни времени — чей возврат, не знаем
        } else if (who == me) {
          chosen.add(r);
        }
      }
    }
    return (total: chosen.fold<double>(0, (a, r) => a + num0(r, 'amount')), count: chosen.length, unattributed: unattributed);
  }

  /// Всё, что нужно для строк «Итоги смены» и X/Z-отчёта. Каждый запрос — отдельно: нет связи с одним из них — часть цифр
  /// остаётся неизвестной ([ShiftBreakdown.partial]), а не выдумывается.
  Future<ShiftBreakdown> breakdown(ShiftInfo shift) async {
    Map<String, dynamic>? report;
    List<Map<String, dynamic>>? flows;
    ({double total, int count, int unattributed})? returnsFigure;
    var partial = false;
    try {
      report = await this.report(shift.id);
    } on ApiException {
      partial = true;
    }
    try {
      flows = await getAllPages(api, 'api/construction/cashflows/', query: {'shift': shift.id}, pageSize: 200, maxPages: 10);
    } on ApiException {
      partial = true;
    }
    try {
      returnsFigure = await returns(shift);
    } on ApiException {
      partial = true;
    }
    return ShiftBreakdown.build(
      shift: shift,
      report: report,
      flows: flows,
      returns: returnsFigure == null ? null : (total: returnsFigure.total, count: returnsFigure.count),
      returnsUnattributed: returnsFigure?.unattributed ?? 0,
      partial: partial,
    );
  }
}

/// 2026-10-26 (живой аудит, П6): разбивка смены «как у ПК». «Должно быть в кассе» сервера — главное число; свой расчёт
/// (начало + наличные − возвраты нал + оплата долгов нал + внесения − изъятия) считается ТОЛЬКО когда известны все части, и
/// показывается рядом, если разошёлся с сервером. Неизвестные части (`null`) не заменяются нулём и не выдумываются.
class ShiftBreakdown {
  final double opening;
  final double cashSales;
  final double nonCashSales;
  final double debtSales;
  final double revenue;
  final int salesCount;

  /// «Должно быть в кассе» по серверу (expected_cash отчёта смены, иначе из самой смены).
  final double serverExpected;

  /// Внесения и изъятия (cashflows смены, source_kind shift_drawer_*). null — журнал денег не загружен.
  final double? inflow;
  final double? outflow;

  /// Возвраты смены: сумма, число и сколько из них выдано наличными (cashflows pos_sale_return). null — неизвестно.
  final double? returnsTotal;
  final int? returnsCount;
  final double? returnsCash;

  /// Оплата долгов наличными и предоплата по долгу (внесено при продаже в долг) — отчёт смены сервера; null — сервер не прислал.
  final double? debtPaymentsCash;
  final double? prepayCash;
  final double? prepayNonCash;

  /// Какой-то из запросов не дошёл (нет связи) — часть цифр не загружена.
  final bool partial;

  /// 2026-10-26 (приёмка 1.18.6, С-2н): сколько возвратов за время смены сервер отдал без смены и без кассира — они в сумму
  /// возвратов не входят (чьи — не известно); при >0 расчёт по строкам не строится, а на ленте стоит пометка «частично».
  final int returnsUnattributed;

  const ShiftBreakdown({
    this.opening = 0,
    this.cashSales = 0,
    this.nonCashSales = 0,
    this.debtSales = 0,
    this.revenue = 0,
    this.salesCount = 0,
    this.serverExpected = 0,
    this.inflow,
    this.outflow,
    this.returnsTotal,
    this.returnsCount,
    this.returnsCash,
    this.debtPaymentsCash,
    this.prepayCash,
    this.prepayNonCash,
    this.partial = false,
    this.returnsUnattributed = 0,
  });

  static double _r(double v) => (v * 100).roundToDouble() / 100;

  static ShiftBreakdown build({
    required ShiftInfo shift,
    Map<String, dynamic>? report,
    List<Map<String, dynamic>>? flows,
    ({double total, int count})? returns,
    int returnsUnattributed = 0,
    bool partial = false,
  }) {
    double? rn(String k) => report == null || report[k] == null ? null : num0(report, k);
    double? inflow;
    double? outflow;
    var retCash = 0.0;
    var hasRetFlows = false;
    if (flows != null) {
      var inSum = 0.0;
      var outSum = 0.0;
      for (final f in flows) {
        final kind = (str(f, 'source_kind') ?? '').toLowerCase();
        final type = (str(f, 'type') ?? '').toLowerCase();
        final amount = num0(f, 'amount');
        if (kind.startsWith('shift_drawer')) {
          if (type == 'expense' || kind.endsWith('outflow')) {
            outSum += amount;
          } else {
            inSum += amount;
          }
        } else if (kind == 'pos_sale_return') {
          hasRetFlows = true;
          final pm = (str(f, 'payment_method') ?? 'cash').toLowerCase();
          if (pm == 'cash') retCash += amount;
        }
      }
      inflow = inSum;
      outflow = outSum;
    }
    // Сумма возвратов: отчёт смены (когда сервер начнёт её считать), иначе список возвратов.
    final repReturns = rn('returns_total');
    double? retTotal;
    int? retCount;
    if (repReturns != null && repReturns > 0.004) {
      retTotal = repReturns;
      retCount = rn('returns_count')?.round() ?? returns?.count;
    } else if (returns != null) {
      retTotal = returns.total;
      retCount = returns.count;
    }
    // сумма и число — от самого отчёта смены: тогда «безымянные» возвраты уже учтены сервером
    final unattributed = repReturns != null && repReturns > 0.004 ? 0 : returnsUnattributed;
    double? retCashOut;
    if (unattributed > 0) {
      retCashOut = null; // часть возвратов неизвестна — сколько из них наличными, сказать нельзя
    } else if (retTotal != null) {
      if (retTotal <= 0.004) {
        retCashOut = 0;
      } else if (hasRetFlows) {
        retCashOut = retCash;
      }
    }
    // 2026-10-26 (приёмка 1.18.6, С-1н): «Наличные» и «Безнал» — вместе с предоплатой по долгу, как на ПК (CashWithPrepayment):
    // cash_sales / noncash_sales отчёта сервера уже с предоплатой; если отчёт их не прислал, а предоплата известна — прибавляем её
    // к продажам смены. Раньше предоплата терялась, и «расчёт по строкам» ложно «расходился с сервером» на её сумму.
    final cashWithPrepay = rn('cash_sales') ?? shift.cashSales + (rn('debt_prepayments_cash') ?? 0);
    final nonCashWithPrepay = rn('noncash_sales') ?? shift.nonCashSales + (rn('debt_prepayments_noncash') ?? 0);
    return ShiftBreakdown(
      opening: shift.openingCash,
      cashSales: cashWithPrepay,
      nonCashSales: nonCashWithPrepay,
      debtSales: shift.debtSales,
      revenue: shift.salesTotal,
      salesCount: shift.salesCount,
      serverExpected: rn('expected_cash') ?? shift.expectedCash,
      inflow: inflow,
      outflow: outflow,
      returnsTotal: retTotal,
      returnsCount: retCount,
      returnsCash: retCashOut,
      debtPaymentsCash: rn('debt_payments_cash'),
      prepayCash: rn('debt_prepayments_cash'),
      prepayNonCash: rn('debt_prepayments_noncash'),
      partial: partial,
      returnsUnattributed: unattributed,
    );
  }

  /// Все части расчёта известны.
  bool get calcKnown => inflow != null && outflow != null && returnsCash != null && debtPaymentsCash != null && returnsUnattributed == 0;

  /// «Должно быть» по строкам; null — не хватает части (см. [calcKnown]).
  double? get calcExpected => !calcKnown ? null : _r(opening + cashSales - returnsCash! + debtPaymentsCash! + inflow! - outflow!);

  /// Расчёт минус сервер; null — расчёта нет.
  double? get diff => calcExpected == null ? null : _r(calcExpected! - serverExpected);

  bool get matches => diff != null && diff!.abs() < 0.01;
}
