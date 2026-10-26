import '../../core/api/session_store.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'tape_doc.dart';

/// 2026-10-26 (живой аудит, П5): документы денежных шагов — «каждый денежный шаг оставляет документ».
/// Квитанция об оплате долга, ордер внесения/изъятия, документ возврата. Только сборка строк — сами деньги проводит
/// OpsRepository (ключи повтора и идемпотентность не менялись).
String _som(AppLocalizations l, double v) => '${money(v)} ${l.som}';

String methodLabel(AppLocalizations l, String method) => switch (method) {
  'cash' => l.cash,
  'transfer' || 'card' => l.card,
  _ => method,
};

/// Остаток долга после оплаты: долг до оплаты минус принятое, не меньше нуля.
double debtLeftAfter(double debtBefore, double accepted) {
  final v = ((debtBefore - accepted) * 100).round() / 100;
  return v < 0 ? 0 : v;
}

/// Квитанция об оплате долга: клиент, долг до оплаты, принято, сдача, «Останется долга».
TapeDoc debtReceiptDoc(
  AppLocalizations l,
  Session? s, {
  required String clientName,
  String phone = '',
  required double debtBefore,
  required double accepted,
  required String method,
  double change = 0,
  DateTime? at,
}) {
  final left = debtLeftAfter(debtBefore, accepted);
  final when = at ?? DateTime.now();
  return TapeDoc.forSession(
    s,
    title: l.debtReceiptTitle,
    fileName: '${l.debtReceiptTitle} ${tapeDate(when)}',
    meta: [
      (l.dateLabel, tapeDate(when)),
      (l.cashierLabel, s?.userName ?? ''),
      (l.cashbox, s?.cashboxName ?? ''),
      (l.client, clientName),
      (l.phone, phone),
    ],
    items: [
      TapeItem.row(l.debtReceiptBefore, _som(l, debtBefore)),
      TapeItem.row(l.debtReceiptMethod, methodLabel(l, method)),
      const TapeItem.rule(),
      TapeItem.total(l.debtReceiptAccepted, _som(l, accepted), tone: TapeTone.good),
      if (change > 0.004) TapeItem.row(l.change, _som(l, change)),
      const TapeItem.rule(),
      if (left > 0.004) TapeItem.total(l.payDebtLeft(_som(l, left)), '') else TapeItem.note(l.debtReceiptCleared, tone: TapeTone.good),
    ],
  );
}

/// Ордер внесения в кассу или изъятия из неё.
TapeDoc cashOrderDoc(
  AppLocalizations l,
  Session? s, {
  required bool withdrawal,
  required double amount,
  required String reason,
  double? drawerAfter,
  DateTime? at,
}) {
  final when = at ?? DateTime.now();
  final title = withdrawal ? l.cashOut : l.cashIn;
  return TapeDoc.forSession(
    s,
    title: title,
    fileName: '$title ${tapeDate(when)}',
    meta: [(l.dateLabel, tapeDate(when)), (l.cashierLabel, s?.userName ?? ''), (l.cashbox, s?.cashboxName ?? '')],
    items: [
      TapeItem.total(l.amount, '${withdrawal ? '−' : '+'}${_som(l, amount)}', tone: withdrawal ? TapeTone.bad : TapeTone.good),
      if (reason.trim().isNotEmpty) TapeItem.row(l.reason, reason.trim()),
      if (drawerAfter != null) ...[const TapeItem.rule(), TapeItem.row(l.shouldBeInDrawer, _som(l, drawerAfter))],
    ],
  );
}

/// Документ возврата: что вернули, сколько и кому выдано, причина.
TapeDoc returnDoc(
  AppLocalizations l,
  Session? s, {
  required String number,
  required List<(String name, double qty)> items,
  required List<String> splitLines,
  String reason = '',
  DateTime? at,
}) {
  final when = at ?? DateTime.now();
  final title = '${l.returnTitle} №$number';
  return TapeDoc.forSession(
    s,
    title: title,
    fileName: title,
    meta: [(l.dateLabel, tapeDate(when)), (l.cashierLabel, s?.userName ?? ''), (l.cashbox, s?.cashboxName ?? '')],
    items: [
      for (final (name, qty) in items) TapeItem.row(name, qtyText(qty)),
      if (items.isNotEmpty) const TapeItem.rule(),
      for (var i = 0; i < splitLines.length; i++) i == 0 ? TapeItem.total(splitLines[i], '') : TapeItem.note(splitLines[i], tone: TapeTone.normal),
      if (reason.trim().isNotEmpty) TapeItem.row(l.returnReasonLabelShort, reason.trim()),
    ],
  );
}
