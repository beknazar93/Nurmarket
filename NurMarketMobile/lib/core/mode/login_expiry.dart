import 'dart:convert';

/// 2026-11-20 (Простой режим, М17): предупреждения «не потерять продажу» — срок входа и старые неотправленные чеки.
/// Чистые функции (без сети и базы): срок входа читается из `exp` токена обновления, чеки — по времени создания в очереди.
/// Схема базы не меняется.

/// Когда истекает токен (поле `exp` JWT); null — токен не JWT или без срока.
DateTime? jwtExpiry(String? token) {
  try {
    final parts = (token ?? '').split('.');
    if (parts.length != 3) return null;
    final json = jsonDecode(utf8.decode(base64Url.decode(base64Url.normalize(parts[1])))) as Map;
    final exp = json['exp'];
    if (exp is! num) return null;
    return DateTime.fromMillisecondsSinceEpoch(exp.toInt() * 1000);
  } catch (_) {
    return null;
  }
}

enum SimpleWarningKind { loginExpires, loginExpired, oldSalesSoon, oldSalesLate }

class SimpleWarning {
  final SimpleWarningKind kind;

  /// Когда истекает вход (для loginExpires) или возраст самого старого чека в сутках (для oldSales*).
  final DateTime? at;
  final int days;
  final int count;
  const SimpleWarning(this.kind, {this.at, this.days = 0, this.count = 0});
}

/// За сколько до конца входа предупреждать.
const loginWarnBefore = Duration(days: 2);

/// Сервер принимает офлайн-чек без подтверждения владельца до 7 суток; предупреждаем с 5-х суток.
const oldSaleSoon = Duration(days: 5);
const oldSaleLate = Duration(days: 7);

List<SimpleWarning> simpleWarnings({required DateTime now, DateTime? loginExpiry, Iterable<DateTime> pendingSales = const []}) {
  final out = <SimpleWarning>[];
  if (loginExpiry != null) {
    if (!loginExpiry.isAfter(now)) {
      out.add(SimpleWarning(SimpleWarningKind.loginExpired, at: loginExpiry));
    } else if (loginExpiry.difference(now) <= loginWarnBefore) {
      out.add(SimpleWarning(SimpleWarningKind.loginExpires, at: loginExpiry));
    }
  }
  DateTime? oldest;
  var soon = 0, late = 0;
  for (final d in pendingSales) {
    final age = now.difference(d);
    if (age >= oldSaleLate) {
      late++;
    } else if (age >= oldSaleSoon) {
      soon++;
    } else {
      continue;
    }
    if (oldest == null || d.isBefore(oldest)) oldest = d;
  }
  if (late > 0) {
    out.add(SimpleWarning(SimpleWarningKind.oldSalesLate, count: late + soon, days: now.difference(oldest!).inDays));
  } else if (soon > 0) {
    out.add(SimpleWarning(SimpleWarningKind.oldSalesSoon, count: soon, days: now.difference(oldest!).inDays));
  }
  return out;
}
