import '../../core/api/session_store.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../ops/tape_doc.dart';
import 'shift_repository.dart';

/// 2026-10-26 (живой аудит, П6): строки итогов смены — одни и те же на экране «Закрыть смену», в X-отчёте и в Z-итоге.
/// Деньги: «Должно быть в кассе» — число сервера; расчёт по строкам показывается рядом, только если известны все части и
/// он разошёлся с сервером (см. [ShiftBreakdown]). Неизвестное не заменяется нулём.
String _som(AppLocalizations l, double v) => '${money(v)} ${l.som}';
String _plus(AppLocalizations l, double v) => '+${money(v)} ${l.som}';
String _minus(AppLocalizations l, double v) => '−${money(v)} ${l.som}';

/// Продажи: наличные / безнал / в долг, выручка, возвраты.
List<TapeItem> shiftSalesItems(AppLocalizations l, ShiftBreakdown b) {
  final prepay = (b.prepayCash ?? 0) + (b.prepayNonCash ?? 0);
  return [
    TapeItem.heading('${l.shSalesBlock} · ${l.checksCount(b.salesCount)}'),
    TapeItem.row(l.cash, _som(l, b.cashSales)),
    TapeItem.row(l.shNonCash, _som(l, b.nonCashSales)),
    if (b.debtSales > 0.004) TapeItem.row(l.shDebt, _som(l, b.debtSales)),
    if (prepay > 0.004) TapeItem.note('${l.shPrepayIncluded}: ${_som(l, prepay)}'),
    const TapeItem.rule(),
    TapeItem.total(l.revenue, _som(l, b.revenue)),
    if (b.returnsTotal == null && b.returnsUnattributed == 0)
      TapeItem.note(l.shReturnsUnknown, tone: TapeTone.warn)
    else if (b.returnsTotal != null && b.returnsTotal! > 0.004)
      TapeItem.row('${l.returnsTitle} · ${l.returnsCountN(b.returnsCount ?? 0)}', _minus(l, b.returnsTotal!), tone: TapeTone.bad),
    // 2026-10-26 (приёмка 1.18.6, С-2н): возвраты без кассира и смены в ответе сервера — не выдумываем сумму, но и не молчим
    if (b.returnsUnattributed > 0) TapeItem.note(l.shReturnsPartial(b.returnsUnattributed), tone: TapeTone.warn),
  ];
}

/// Деньги в кассе: начало, наличные, оплата долгов, возвраты, внесения/изъятия → «Должно быть в кассе».
List<TapeItem> shiftCashItems(AppLocalizations l, ShiftBreakdown b) {
  final calc = b.calcExpected;
  return [
    TapeItem.heading(l.shCashBlock),
    TapeItem.row(l.openingCashLabel, _som(l, b.opening)),
    TapeItem.row(l.cash, _plus(l, b.cashSales)),
    if (b.debtPaymentsCash != null && b.debtPaymentsCash! > 0.004) TapeItem.row(l.shDebtPayments, _plus(l, b.debtPaymentsCash!)),
    if (b.returnsCash != null && b.returnsCash! > 0.004) TapeItem.row(l.shReturnsCash, _minus(l, b.returnsCash!), tone: TapeTone.bad),
    if ((b.inflow ?? 0) > 0.004) TapeItem.row(l.cashIn, _plus(l, b.inflow!)),
    if ((b.outflow ?? 0) > 0.004) TapeItem.row(l.cashOut, _minus(l, b.outflow!), tone: TapeTone.bad),
    const TapeItem.rule(),
    TapeItem.total(l.shouldBeInDrawer, _som(l, b.serverExpected)),
    if (calc != null && !b.matches) ...[
      TapeItem.row(l.shCalcRow, _som(l, calc), tone: TapeTone.warn),
      TapeItem.note(l.shMismatch(money(b.diff!.abs())), tone: TapeTone.warn),
    ] else if (calc == null)
      TapeItem.note(l.shNoSplit),
    if (b.partial) TapeItem.note(l.shPartial, tone: TapeTone.warn),
  ];
}

/// X-отчёт (промежуточный, [z] = false) или Z-итог закрытой смены.
TapeDoc shiftTapeDoc(
  AppLocalizations l,
  Session? s,
  ShiftInfo shift,
  ShiftBreakdown b, {
  bool z = false,
  double? counted,
  DateTime? closedAt,
  int pending = 0,
  int failed = 0,
}) {
  final title = z ? l.zReport : l.xReport;
  final box = shift.cashboxName ?? s?.cashboxName ?? '';
  return TapeDoc.forSession(
    s,
    title: title,
    fileName: '$title ${tapeDate(closedAt ?? DateTime.now())}',
    meta: [
      if (shift.openedAt != null) (l.shOpened, tapeDate(shift.openedAt!)),
      if (z && closedAt != null) (l.shClosed, tapeDate(closedAt)),
      (l.cashierLabel, s?.userName ?? ''),
      (l.cashbox, box),
    ],
    items: [
      ...shiftSalesItems(l, b),
      const TapeItem.rule(),
      ...shiftCashItems(l, b),
      if (counted != null) ...[
        const TapeItem.rule(),
        TapeItem.row(l.shCounted, _som(l, counted)),
        TapeItem.total(
          l.shDifference,
          counted - b.serverExpected > 0.004 ? _plus(l, counted - b.serverExpected) : (counted - b.serverExpected < -0.004 ? _minus(l, b.serverExpected - counted) : _som(l, 0)),
          tone: (counted - b.serverExpected).abs() < 0.005 ? TapeTone.good : TapeTone.bad,
        ),
      ],
      if (!z) TapeItem.note(l.xReportNote),
      if (pending > 0) TapeItem.note(l.closeShiftPending(pending), tone: TapeTone.warn),
      if (failed > 0) TapeItem.note(l.closeShiftFailed(failed), tone: TapeTone.warn),
    ],
  );
}
