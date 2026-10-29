import 'dart:math' as math;

import 'package:intl/intl.dart';

import '../../../l10n/app_localizations.dart';
import '../../util/ui.dart' show money;

// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»): ЛОГИКА инфографики без отрисовки — формат чисел, дельта к прошлому
// периоду, прошлый период, деления оси, шаг подписей, группировка секторов, текстовые описания для TalkBack. Внешний вид
// (цвета, формы, отступы) здесь не задаётся — он по спеке дизайна. Чистые функции: проверяются тестами без экрана.

bool _bad(double v) => v.isNaN || v.isInfinite;

final _dec1 = NumberFormat('#,##0.#', 'ru');

/// Сокращения для крупных чисел: «тыс», «млн», «млрд» на языке приложения.
class CompactWords {
  final String thousand;
  final String million;
  final String billion;
  const CompactWords(this.thousand, this.million, this.billion);
  factory CompactWords.of(AppLocalizations l) => CompactWords(l.infK, l.infM, l.infB);
}

/// 1 234 567 → «1,2 млн». Меньше [fullBelow] — число целиком с пробелами («9 800»). Знак «−» (U+2212) у отрицательных.
/// Не число и бесконечность → «—». Минус ноль не печатается («0»).
String compactNumber(double v, CompactWords w, {double fullBelow = 10000}) {
  if (_bad(v)) return '—';
  final a = v.abs();
  String body;
  if (a < fullBelow) {
    body = money(double.parse(a.toStringAsFixed(2)));
  } else if (a < 1e6) {
    body = '${_dec1.format(a / 1e3)} ${w.thousand}';
  } else if (a < 1e9) {
    body = '${_dec1.format(a / 1e6)} ${w.million}';
  } else {
    body = '${_dec1.format(a / 1e9)} ${w.billion}';
  }
  // «999,96 тыс» после округления превращается в «1 000 тыс» — переводим в следующий разряд
  if (a >= 999950 && a < 1e6) body = '1 ${w.million}';
  if (a >= 999950000 && a < 1e9) body = '1 ${w.billion}';
  final isZero = body == '0';
  return v < 0 && !isZero ? '−$body' : body;
}

/// Сумма с «сом»: «1 250,5 сом». Для плитки и подписей, где место есть.
String moneyWithUnit(double v, String unit) => _bad(v) ? '—' : '${money(v)} $unit';

/// Доля в процентах: «12,5 %». [part] / [total]; total ≤ 0 → «—».
String percentText(double part, double total) {
  if (_bad(part) || _bad(total) || total <= 0) return '—';
  final p = part / total * 100;
  return '${_dec1.format(p)} %';
}

// ------------------------------------------------------------------ дельта к прошлому периоду

enum DeltaKind { up, down, same, fresh, none }

/// Изменение [current] к [previous]. Процент считается от |previous|; previous == 0 и current != 0 — «новое» (процента нет);
/// оба нули — «без изменений»; нет прошлого значения (null) — [DeltaKind.none] (дельту не показываем).
class DeltaInfo {
  final DeltaKind kind;

  /// Процент со знаком (null у fresh/none). Больше 999 % не обрезается здесь — см. [text].
  final double? percent;
  const DeltaInfo(this.kind, this.percent);

  static DeltaInfo of(double current, double? previous) {
    if (previous == null || _bad(current) || _bad(previous)) return const DeltaInfo(DeltaKind.none, null);
    if (previous.abs() < 1e-9) {
      return current.abs() < 1e-9 ? const DeltaInfo(DeltaKind.same, 0) : const DeltaInfo(DeltaKind.fresh, null);
    }
    final p = (current - previous) / previous.abs() * 100;
    if (p.abs() < 0.05) return const DeltaInfo(DeltaKind.same, 0);
    return DeltaInfo(p > 0 ? DeltaKind.up : DeltaKind.down, p);
  }

  /// «▲ +12 %», «▼ −3,5 %», «● 0 %», «▲ >999 %». У fresh — [freshWord] («новое»), у none — пусто.
  String text({String freshWord = ''}) {
    switch (kind) {
      case DeltaKind.none:
        return '';
      case DeltaKind.same:
        return '● 0 %';
      case DeltaKind.fresh:
        return '▲ $freshWord'.trim();
      case DeltaKind.up:
      case DeltaKind.down:
        final p = percent!;
        final a = p.abs();
        final mark = kind == DeltaKind.up ? '▲' : '▼';
        final sign = kind == DeltaKind.up ? '+' : '−';
        return a > 999 ? '$mark >999 %' : '$mark $sign${_dec1.format(a)} %';
    }
  }

  /// Хорошо ли это для денег: рост — хорошо, кроме показателей, где рост плох ([inverse]: долги, возвраты, расходы).
  /// null — нейтрально (без изменений / нет данных).
  bool? isGood({bool inverse = false}) {
    switch (kind) {
      case DeltaKind.up:
      case DeltaKind.fresh:
        return !inverse;
      case DeltaKind.down:
        return inverse;
      case DeltaKind.same:
      case DeltaKind.none:
        return null;
    }
  }

  /// Для TalkBack: «Выше на 12 % к прошлой неделе».
  String semantics(AppLocalizations l, String vs) {
    switch (kind) {
      case DeltaKind.none:
        return '';
      case DeltaKind.same:
        return l.infDeltaSameSem(vs);
      case DeltaKind.fresh:
        return '${l.infDeltaNew} $vs';
      case DeltaKind.up:
        return l.infDeltaUpSem(_dec1.format(percent!.abs()), vs);
      case DeltaKind.down:
        return l.infDeltaDownSem(_dec1.format(percent!.abs()), vs);
    }
  }
}

// ------------------------------------------------------------------ периоды

/// Прошлый период той же длины, что [from]…[to] (включительно). Режимы: today → вчера; week → 7 дней до начала;
/// month → тот же отрезок прошлого календарного месяца (с 1-го по то же число, не дальше конца месяца);
/// иначе (свой) — столько же дней сразу перед [from].
({DateTime from, DateTime to}) previousPeriod(String period, DateTime from, DateTime to) {
  final f = DateTime(from.year, from.month, from.day);
  final t = DateTime(to.year, to.month, to.day);
  final days = t.difference(f).inDays + 1;
  switch (period) {
    case 'today':
      final y = DateTime(t.year, t.month, t.day - 1);
      return (from: y, to: y);
    case 'month':
      final pm = DateTime(f.year, f.month - 1, 1);
      final last = DateTime(f.year, f.month, 0).day;
      return (from: pm, to: DateTime(pm.year, pm.month, math.min(t.day, last)));
    default:
      return (from: DateTime(f.year, f.month, f.day - days), to: DateTime(f.year, f.month, f.day - 1));
  }
}

/// Какая подпись «к …» подходит режиму: today/week/month/свой.
String versusLabel(AppLocalizations l, String period) => switch (period) {
  'today' => l.infVsYesterday,
  'week' => l.infVsPrevWeek,
  'month' => l.infVsPrevMonth,
  _ => l.infVsPrevPeriod,
};

// ------------------------------------------------------------------ оси и подписи

/// «Красивые» деления оси: [count] шагов от min(0, минимум) до max(0, максимум). Всегда включает 0; все значения равны или
/// нули → [0, 1]-подобный ряд, чтобы ось не схлопывалась.
List<double> niceTicks(double minV, double maxV, {int count = 3}) {
  var lo = math.min(0.0, _bad(minV) ? 0 : minV);
  var hi = math.max(0.0, _bad(maxV) ? 0 : maxV);
  if (hi - lo < 1e-9) hi = lo + 1;
  final raw = (hi - lo) / count;
  final mag = math.pow(10, (math.log(raw) / math.ln10).floor()).toDouble();
  final norm = raw / mag;
  final step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
  lo = (lo / step).floorToDouble() * step;
  hi = (hi / step).ceilToDouble() * step;
  final out = <double>[];
  for (var v = lo; v <= hi + step * 1e-6; v += step) {
    out.add(double.parse(v.toStringAsFixed(6)));
  }
  return out;
}

/// Через сколько столбцов подписывать ось X, чтобы подписи не наезжали: слот столбца [slotWidth], ширина подписи [labelWidth]
/// (с запасом). 1 — подписать все; всегда ≥ 1.
int labelStep(int count, double slotWidth, double labelWidth, {double gap = 6}) {
  if (count <= 1 || slotWidth <= 0) return 1;
  final need = labelWidth + gap;
  return math.max(1, (need / slotWidth).ceil());
}

/// Какие номера столбцов подписывать: каждый [labelStep]-й, последний всегда (если не наезжает на предыдущий).
List<int> labelIndices(int count, int step) {
  if (count <= 0) return const [];
  final out = <int>[for (var i = 0; i < count; i += step) i];
  final last = count - 1;
  if (out.last != last) {
    if (last - out.last >= (step + 1) ~/ 2) {
      out.add(last);
    } else {
      out[out.length - 1] = last;
    }
  }
  return out;
}

/// Подписывать ли значения над столбцами: при плотных столбцах — нет (только выбранный и лучший).
bool showBarValues(int count, double slotWidth, double valueLabelWidth) => count <= 14 && slotWidth >= valueLabelWidth + 2;

// ------------------------------------------------------------------ секторы кольца

class Slice {
  final String key;
  final String label;
  final double value;
  const Slice(this.key, this.label, this.value);
}

/// Секторы кольца: нули и отрицательные отбрасываются, сортировка по убыванию, сверх [maxSlices] — в «Прочее» ([otherLabel],
/// ключ `_other`). Если «Прочее» оказалось единственным лишним — оставляем его отдельным сектором как есть.
List<Slice> groupSlices(List<Slice> src, {int maxSlices = 6, String otherLabel = ''}) {
  final pos = [
    for (final s in src)
      if (!_bad(s.value) && s.value > 0) s,
  ]..sort((a, b) => b.value.compareTo(a.value));
  if (pos.length <= maxSlices) return pos;
  final head = pos.take(maxSlices - 1).toList();
  final rest = pos.skip(maxSlices - 1).fold<double>(0, (a, s) => a + s.value);
  return [...head, Slice('_other', otherLabel, rest)];
}

/// Углы секторов в долях оборота (0…1), сумма долей = 1; пусто при нулевой сумме.
List<double> sliceFractions(List<Slice> s) {
  final total = s.fold<double>(0, (a, x) => a + x.value);
  if (total <= 0) return const [];
  return [for (final x in s) x.value / total];
}

/// Целые проценты секторов, в сумме ровно 100 (метод наибольшего остатка) — чтобы в легенде не было «33 + 33 + 33 = 99».
List<int> wholePercents(List<double> fractions) {
  if (fractions.isEmpty) return const [];
  final raw = [for (final f in fractions) f * 100];
  final base = [for (final r in raw) r.floor()];
  var left = 100 - base.fold<int>(0, (a, b) => a + b);
  final order = List<int>.generate(raw.length, (i) => i)..sort((a, b) => (raw[b] - base[b]).compareTo(raw[a] - base[a]));
  for (final i in order) {
    if (left <= 0) break;
    base[i]++;
    left--;
  }
  return base;
}

// ------------------------------------------------------------------ столбцы: статистика и описание

/// Статистика ряда столбцов (для подсказок и описания TalkBack).
class BarStats {
  final double total;
  final double max;
  final double min;
  final int maxIndex;
  final int minIndex;
  final int count;
  const BarStats(this.total, this.max, this.min, this.maxIndex, this.minIndex, this.count);

  static BarStats of(List<double> v) {
    var total = 0.0;
    var mx = double.negativeInfinity, mn = double.infinity;
    var mxi = -1, mni = -1;
    for (var i = 0; i < v.length; i++) {
      final x = _bad(v[i]) ? 0.0 : v[i];
      total += x;
      if (x > mx) {
        mx = x;
        mxi = i;
      }
      if (x < mn) {
        mn = x;
        mni = i;
      }
    }
    if (v.isEmpty) return const BarStats(0, 0, 0, -1, -1, 0);
    return BarStats(total, mx, mn, mxi, mni, v.length);
  }
}

/// Текстовое описание столбчатой диаграммы для TalkBack: «Выручка по дням: максимум 12 300 сом (Вт 21.10), минимум 0 сом
/// (Ср 22.10), всего 40 000 сом». Одна точка — «Выручка по дням: Вт 21.10 — 12 300 сом». Пусто — «…: нет данных».
String describeBars(AppLocalizations l, {required String name, required List<String> labels, required List<double> values, required String unit}) {
  if (values.isEmpty) return l.infDescEmpty(name);
  final st = BarStats.of(values);
  String lab(int i) => i >= 0 && i < labels.length ? labels[i] : '';
  if (values.length == 1) return l.infDescOne(name, lab(0), money(values[0]), unit);
  return l.infDescBars(name, money(st.max), unit, lab(st.maxIndex), money(st.min), lab(st.minIndex), money(st.total));
}

/// Описание кольца: «Способы оплаты: Наличные 50 %, Карта 30 %, Перевод 20 %».
String describeSlices(AppLocalizations l, {required String name, required List<Slice> slices}) {
  if (slices.isEmpty) return l.infDescEmpty(name);
  final pct = wholePercents(sliceFractions(slices));
  final parts = [for (var i = 0; i < slices.length; i++) '${slices[i].label} ${pct[i]} %'];
  return '$name: ${parts.join(', ')}';
}
