// 2026-10-26 (голос-управление, В2): модель таблицы на экране во время звонка. Таблицу собирает ПРОГРАММА из данных
// инструмента (колонки, строки, итоги, форматы), а не ИИ: ИИ таблицу не зачитывает, а называет одно-два главных числа
// (раздел 4 ТЗ). Здесь только данные и правила (без виджетов): сортировка, фильтр, «покажи ещё», экспорт в текст,
// хранилище таблиц звонка. Панель на экране — пакет В4, она читает эту модель.
import 'package:flutter/foundation.dart';

/// Текст на нужном языке (ru, ky, uz, en, tr); незнакомый язык — русский. Как `Tr.T` кассы, порядок — как в ТЗ.
String tr5(String lang, String ru, String ky, String uz, String en, String tr) => switch (lang) {
  'ky' => ky,
  'uz' => uz,
  'en' => en,
  'tr' => tr,
  _ => ru,
};

/// Как показывать значение ячейки (раздел 4.3 ТЗ).
enum CellKind { text, money, qty, percent, date, time, code, status }

/// Значение ячейки со своим видом: нужно, когда в одной колонке разные виды (карточка «поле — значение») или у количества
/// своя единица (шт, кг). Обычная ячейка — просто число, строка или DateTime.
class Cell {
  final Object? v;
  final CellKind kind;
  final String? unit;
  const Cell(this.v, this.kind, {this.unit});
}

Object? cellValue(Object? o) => o is Cell ? o.v : o;

class CallColumn {
  final String key;
  final String title;
  final CellKind kind;

  /// Числовая колонка: выравнивается вправо, по ней считаются «Итого» и числовой фильтр.
  final bool numeric;
  final String? unit;

  /// Процент со знаком «+12 %» (по умолчанию да); «доля 40 %» — без знака.
  final bool signed;

  /// Колонка суммируется в «Итого».
  final bool sum;
  CallColumn(this.key, this.title, this.kind, {bool? numeric, this.unit, this.signed = true, this.sum = false})
    : numeric = numeric ?? (kind == CellKind.money || kind == CellKind.qty || kind == CellKind.percent);
}

/// Условие «только где …»: по колонке (или по любому тексту строки) — больше, меньше, равно, содержит.
class CallFilter {
  /// Номер колонки; -1 — искать слово в любой текстовой колонке.
  final int col;

  /// gt, gte, lt, lte, eq, ne, contains.
  final String op;
  final Object value;
  const CallFilter(this.col, this.op, this.value);

  static const _ops = {'gt', 'gte', 'lt', 'lte', 'eq', 'ne', 'contains'};

  /// Разбор того, что ИИ передал голосом: `column` + `value` (с оператором впереди: «>1000», «<=5», «=Кола») и необязательный
  /// `op`. Слова «больше / more than / артык / katta / fazla» тоже понимаются. Пустое условие — null.
  static CallFilter? parse(CallTable t, String? column, String value, {String? op}) {
    var v = value.trim();
    if (v.isEmpty) return null;
    var o = (op ?? '').trim().toLowerCase();
    if (!_ops.contains(o)) o = '';
    if (o.isEmpty) {
      final m = RegExp(r'^(>=|<=|!=|≥|≤|>|<|=)\s*(.*)$').firstMatch(v);
      if (m != null) {
        o = switch (m.group(1)) {
          '>=' || '≥' => 'gte',
          '<=' || '≤' => 'lte',
          '!=' => 'ne',
          '>' => 'gt',
          '<' => 'lt',
          _ => 'eq',
        };
        v = m.group(2)!.trim();
      }
    }
    if (o.isEmpty) {
      final w = RegExp(
        r'^(больше|более|свыше|выше|меньше|менее|ниже|more than|greater than|over|above|less than|under|below|артык|көп|аз|katta|ko.p|kam|fazla|az)(?=\s)\s*(?:чем|than|dan|den)?\s*(.*)$',
        caseSensitive: false,
      ).firstMatch(v);
      if (w != null) {
        const gt = {'больше', 'более', 'свыше', 'выше', 'more than', 'greater than', 'over', 'above', 'артык', 'көп', 'katta', 'fazla'};
        final word = w.group(1)!.toLowerCase();
        o = word.startsWith('ko') || gt.contains(word) ? 'gt' : 'lt';
        v = w.group(2)!.trim();
        if (v.isEmpty) return null;
      }
    }
    var col = (column == null || column.trim().isEmpty) ? -1 : t.resolveColumn(column);
    final n = _parseNum(v);
    if (col < 0 && n != null && o.isNotEmpty && o != 'contains') {
      // «только где больше 1000», колонка не названа — первая денежная (потом любая числовая).
      col = t.columns.indexWhere((c) => c.kind == CellKind.money);
      if (col < 0) col = t.columns.indexWhere((c) => c.numeric);
    }
    if (col >= 0 && t.columns[col].numeric) {
      if (n == null) return null;
      return CallFilter(col, o.isEmpty ? 'eq' : o, n);
    }
    return CallFilter(col, 'contains', _fold(v));
  }

  static double? _parseNum(String s) => double.tryParse(s.replaceAll(RegExp('[   ]'), '').replaceAll(',', '.'));

  String describe(CallTable t) {
    final c = col >= 0 ? t.columns[col].title : '';
    final sign = switch (op) {
      'gt' => '>',
      'gte' => '≥',
      'lt' => '<',
      'lte' => '≤',
      'ne' => '≠',
      'eq' => '=',
      _ => '~',
    };
    return col >= 0 ? '$c $sign $value' : '$value';
  }

  bool matches(CallTable t, int row) {
    if (col >= 0 && t.columns[col].numeric) {
      final x = _asNum(cellValue(t.rows[row][col]));
      if (x == null) return false;
      final y = (value as num).toDouble();
      return switch (op) {
        'gt' => x > y,
        'gte' => x >= y,
        'lt' => x < y,
        'lte' => x <= y,
        'ne' => (x - y).abs() > 1e-9,
        _ => (x - y).abs() < 1e-9,
      };
    }
    final needle = value.toString();
    final cols = col >= 0 ? [col] : [for (var i = 0; i < t.columns.length; i++) if (!t.columns[i].numeric) i];
    for (final i in cols) {
      final raw = cellValue(t.rows[row][i]);
      if (raw == null) continue;
      final shown = t.names.isEmpty ? '$raw' : t.reveal('$raw');
      if (_fold(shown).contains(needle)) return true;
    }
    return false;
  }
}

String _fold(String s) => s.toLowerCase().replaceAll('ё', 'е').trim();

double? _asNum(Object? o) => o is num ? o.toDouble() : (o is String ? double.tryParse(o.replaceAll(',', '.')) : null);

/// Таблица или карточка на экране звонка. Неизменяемая: сортировка, фильтр и «ещё» возвращают новую таблицу с тем же id.
class CallTable {
  /// Сколько строк видно сразу и сколько добавляет «покажи ещё» (раздел 4.2 ТЗ).
  static const pageSize = 20;

  /// Сколько строк хранится (остальное обрезается, `totalRows` помнит исходное число).
  static const maxRows = 500;

  final String id;
  final String title;
  final String? subtitle;
  final List<CallColumn> columns;

  /// Значения как числа, строки, DateTime или [Cell] — не готовый текст (его делает [cellText]).
  final List<List<Object?>> rows;

  /// Что открывает нажатие на строку (id товара, чека, клиента): параллельно [rows]; null — строка не открывается.
  final List<String?> refs;

  /// «Итого» по колонкам (ключ колонки → значение). Считается по ВСЕМ строкам до обрезки.
  final Map<String, Object?> totals;
  final int totalRows;
  final String source;
  final DateTime at;
  final String? sortKey;
  final bool desc;
  final CallFilter? filter;
  final String? objectRoute;

  /// Язык подписей и форматов (по последней реплике пользователя в момент сборки).
  final String lang;

  /// Сколько строк показывать сейчас.
  final int shown;

  /// Карточка «поле — значение» (один товар, один чек): без сортировки и «ещё».
  final bool card;

  /// Код → что показать на экране ([Д1] → «Асан (тел. …)»). В Google уходят только коды (раздел 4.3 ТЗ).
  final Map<String, String> names;

  /// Пометка под таблицей («проверено частично», «только первые 100 смен»); null — нет.
  final String? note;

  CallTable({
    required this.id,
    required this.title,
    this.subtitle,
    required this.columns,
    required this.rows,
    List<String?>? refs,
    this.totals = const {},
    int? totalRows,
    this.source = '',
    DateTime? at,
    this.sortKey,
    this.desc = false,
    this.filter,
    this.objectRoute,
    this.lang = 'ru',
    this.shown = pageSize,
    this.card = false,
    this.names = const {},
    this.note,
  }) : refs = refs ?? List<String?>.filled(rows.length, null),
       totalRows = totalRows ?? rows.length,
       at = at ?? DateTime.now();

  /// Сборка из полных данных: итоги считаются по всем строкам, потом строки обрезаются до [maxRows].
  /// [totals] — готовые итоги (например, из карточек отчёта сервера); без них считаются колонки с `sum`.
  factory CallTable.build({
    required String id,
    required String title,
    String? subtitle,
    required List<CallColumn> columns,
    required List<List<Object?>> rows,
    List<String?>? refs,
    Map<String, Object?>? totals,
    String source = '',
    DateTime? at,
    String? sortKey,
    bool desc = false,
    String? objectRoute,
    String lang = 'ru',
    bool card = false,
    Map<String, String> names = const {},
    String? note,
    int? shown,
  }) {
    final sums = totals ?? sumColumns(columns, rows);
    final cut = rows.length > maxRows;
    return CallTable(
      id: id,
      title: title,
      subtitle: subtitle,
      columns: columns,
      rows: cut ? rows.sublist(0, maxRows) : rows,
      refs: refs == null ? null : (cut ? refs.sublist(0, maxRows) : refs),
      totals: sums,
      totalRows: rows.length,
      source: source,
      at: at,
      sortKey: sortKey,
      desc: desc,
      objectRoute: objectRoute,
      lang: lang,
      card: card,
      names: names,
      note: note,
      shown: shown == null || shown < 1 ? pageSize : shown,
    );
  }

  /// Суммы колонок с `sum: true` (деньги и штуки). Пусто, если таких нет.
  static Map<String, Object?> sumColumns(List<CallColumn> columns, List<List<Object?>> rows, [Iterable<int>? only]) {
    final out = <String, Object?>{};
    for (var i = 0; i < columns.length; i++) {
      if (!columns[i].sum) continue;
      var s = 0.0;
      for (var r = 0; r < rows.length; r++) {
        if (only != null && !only.contains(r)) continue;
        s += _asNum(cellValue(rows[r][i])) ?? 0;
      }
      out[columns[i].key] = (s * 100).roundToDouble() / 100;
    }
    return out;
  }

  CallTable _with({int? shown, String? sortKey, bool? desc, CallFilter? filter, bool clearFilter = false}) => CallTable(
    id: id,
    title: title,
    subtitle: subtitle,
    columns: columns,
    rows: rows,
    refs: refs,
    totals: totals,
    totalRows: totalRows,
    source: source,
    at: at,
    sortKey: sortKey ?? this.sortKey,
    desc: desc ?? this.desc,
    filter: clearFilter ? null : (filter ?? this.filter),
    objectRoute: objectRoute,
    lang: lang,
    shown: shown ?? this.shown,
    card: card,
    names: names,
    note: note,
  );

  // ---------------------------------------------------------------------------------------------------------------
  // Вид: фильтр → сортировка → первые [shown] строк
  // ---------------------------------------------------------------------------------------------------------------

  /// Номера строк [rows] в порядке показа (после фильтра и сортировки).
  late final List<int> viewIndices = _computeView();

  List<int> _computeView() {
    var idx = [for (var i = 0; i < rows.length; i++) i];
    final f = filter;
    if (f != null) idx = idx.where((i) => f.matches(this, i)).toList();
    final sk = sortKey;
    final c = sk == null ? -1 : columns.indexWhere((x) => x.key == sk);
    if (c >= 0 && !card) {
      final sign = desc ? -1 : 1;
      // Стабильно: равные значения остаются в исходном порядке; пустые — всегда в конце.
      final pos = {for (var k = 0; k < idx.length; k++) idx[k]: k};
      idx.sort((a, b) {
        final x = _sortKey(rows[a][c]);
        final y = _sortKey(rows[b][c]);
        if (x == null || y == null) {
          if (x == null && y == null) return pos[a]!.compareTo(pos[b]!);
          return x == null ? 1 : -1;
        }
        final r = _cmp(x, y);
        return r != 0 ? r * sign : pos[a]!.compareTo(pos[b]!);
      });
    }
    return idx;
  }

  static Object? _sortKey(Object? o) {
    final v = cellValue(o);
    if (v == null) return null;
    if (v is num) return v.toDouble();
    if (v is DateTime) return v;
    final s = '$v'.trim();
    return s.isEmpty ? null : _fold(s);
  }

  static int _cmp(Object x, Object y) {
    if (x is double && y is double) return x.compareTo(y);
    if (x is DateTime && y is DateTime) return x.compareTo(y);
    return '$x'.compareTo('$y');
  }

  /// Строки, видимые сейчас: первые [shown] из вида.
  List<int> get visibleIndices {
    final v = viewIndices;
    return v.length <= shown ? v : v.sublist(0, shown);
  }

  int get viewCount => viewIndices.length;
  bool get hasMore => !card && viewCount > shown;
  int get remaining => viewCount > shown ? viewCount - shown : 0;

  /// Данные обрезаны до [maxRows]: исходных строк было больше.
  bool get truncated => totalRows > rows.length;

  /// «Итого» для вида: без фильтра — итоги всех данных; с фильтром — по отфильтрованным строкам.
  Map<String, Object?> get viewTotals => filter == null ? totals : sumColumns(columns, rows, viewIndices);

  // ---------------------------------------------------------------------------------------------------------------
  // Управление голосом (раздел 3.3 ТЗ, table_control)
  // ---------------------------------------------------------------------------------------------------------------

  /// Номер колонки по тому, что сказал пользователь: ключ, название, номер («вторая»), начало названия или смысл
  /// («сумма», «количество», «название», «дата»). -1 — не нашли.
  int resolveColumn(String ref) {
    final q = _fold(ref);
    if (q.isEmpty) return -1;
    for (var i = 0; i < columns.length; i++) {
      if (_fold(columns[i].key) == q || _fold(columns[i].title) == q) return i;
    }
    final n = int.tryParse(q);
    if (n != null && n >= 1 && n <= columns.length) return n - 1;
    for (var i = 0; i < columns.length; i++) {
      final t = _fold(columns[i].title);
      if (t.startsWith(q) || q.startsWith(t) && t.length >= 3) return i;
    }
    int first(bool Function(CallColumn c) test) => columns.indexWhere(test);
    bool has(List<String> stems) => stems.any(q.contains);
    if (has(['сумм', 'выруч', 'sum', 'amount', 'total', 'revenue', 'жалпы', 'jami', 'toplam', 'tutar', 'сом', 'цена', 'price', 'баа', 'narx', 'fiyat'])) {
      final i = first((c) => c.kind == CellKind.money);
      if (i >= 0) return i;
    }
    if (has(['колич', 'остат', 'штук', 'qty', 'quantity', 'stock', 'сан', 'калды', 'soni', 'qoldiq', 'adet', 'miktar', 'stok'])) {
      final i = first((c) => c.kind == CellKind.qty);
      if (i >= 0) return i;
    }
    if (has(['назв', 'товар', 'имя', 'алфав', 'name', 'product', 'ат', 'nom', 'ad', 'isim', 'ürün'])) {
      final i = first((c) => c.kind == CellKind.text || c.kind == CellKind.code);
      if (i >= 0) return i;
    }
    if (has(['дата', 'date', 'күн', 'sana', 'tarih', 'врем', 'time', 'убы', 'возраст'])) {
      final i = first((c) => c.kind == CellKind.date || c.kind == CellKind.time);
      if (i >= 0) return i;
    }
    if (has(['процент', '%', 'percent', 'наценк', 'markup', 'үлүш', 'foiz', 'yüzde'])) {
      final i = first((c) => c.kind == CellKind.percent);
      if (i >= 0) return i;
    }
    return -1;
  }

  /// Сортировка. [desc] не указан: числа и даты — от большего, текст — по алфавиту. [toggle] (нажатие на заголовок колонки
  /// на экране) — по той же колонке без направления переворачивает порядок; голосом («отсортируй по сумме») не переворачивает.
  /// null — колонки нет.
  CallTable? sortedBy(String ref, {bool? desc, bool toggle = false}) {
    if (card) return null;
    final c = resolveColumn(ref);
    if (c < 0) return null;
    final col = columns[c];
    final d = desc ?? (toggle && sortKey == col.key ? !this.desc : col.numeric || col.kind == CellKind.date);
    return _with(sortKey: col.key, desc: d, shown: pageSize);
  }

  /// Фильтр «только где …»; пустое значение снимает фильтр. null — условие не разобрано.
  CallTable? filteredBy(String? column, String value, {String? op}) {
    if (card) return null;
    if (value.trim().isEmpty) return _with(clearFilter: true, shown: pageSize);
    final f = CallFilter.parse(this, column, value, op: op);
    if (f == null) return null;
    return _with(filter: f, shown: pageSize);
  }

  CallTable withoutFilter() => _with(clearFilter: true, shown: pageSize);

  /// «Покажи ещё»: следующие [n] строк.
  CallTable more([int n = pageSize]) => _with(shown: shown + n);

  // ---------------------------------------------------------------------------------------------------------------
  // Текст ячеек
  // ---------------------------------------------------------------------------------------------------------------

  /// Коды [Д1] в тексте → имена для экрана ([names]); незнакомые коды остаются как есть.
  String reveal(String text) {
    if (names.isEmpty) return text;
    return text.replaceAllMapped(RegExp(r'\[[ДСК]\d{1,3}\]'), (m) => names[m.group(0)] ?? m.group(0)!);
  }

  /// Готовый текст ячейки для экрана. [revealNames] false — как для Google (коды остаются кодами).
  String cellText(int row, int col, {bool revealNames = true}) {
    final raw = rows[row][col];
    final c = columns[col];
    final v = raw is Cell ? raw.v : raw;
    final kind = raw is Cell ? raw.kind : c.kind;
    final unit = raw is Cell ? raw.unit : c.unit;
    final text = formatCell(v, kind, lang, unit: unit, signed: c.signed, year: at.year);
    return revealNames && (kind == CellKind.code || kind == CellKind.text) ? reveal(text) : text;
  }

  /// Число в ячейке отрицательное (на экране красным, не только минусом).
  bool isNegative(int row, int col) {
    final raw = rows[row][col];
    final kind = raw is Cell ? raw.kind : columns[col].kind;
    if (kind != CellKind.money && kind != CellKind.qty && kind != CellKind.percent) return false;
    final n = _asNum(cellValue(raw));
    return n != null && n < -0.004;
  }

  /// Итог колонки как текст; null — в итогах её нет.
  String? totalText(int col) {
    final key = columns[col].key;
    final t = viewTotals;
    if (!t.containsKey(key)) return null;
    final c = columns[col];
    return formatCell(t[key], c.kind, lang, unit: c.unit, signed: c.signed, year: at.year);
  }

  bool isTotalNegative(int col) {
    final n = _asNum(viewTotals[columns[col].key]);
    return n != null && n < -0.004 && columns[col].numeric;
  }

  List<String> get headers => [for (final c in columns) c.title];

  /// Строки готовым текстом: [all] — все строки вида (для PDF и «Поделиться»), иначе только видимые.
  List<List<String>> formattedRows({bool all = false, bool revealNames = true}) {
    final idx = all ? viewIndices : visibleIndices;
    return [
      for (final r in idx) [for (var c = 0; c < columns.length; c++) cellText(r, c, revealNames: revealNames)],
    ];
  }

  /// Строка «Итого» готовым текстом; null — итогов нет или это карточка.
  List<String>? totalsRow() {
    if (card) return null;
    final t = viewTotals;
    if (t.isEmpty) return null;
    final label = tr5(lang, 'Итого', 'Жыйынтык', 'Jami', 'Total', 'Toplam');
    var labelled = false;
    return [
      for (var c = 0; c < columns.length; c++)
        totalText(c) ?? (!labelled && !columns[c].numeric ? (() {
          labelled = true;
          return label;
        })() : ''),
    ];
  }

  /// Подпись вида под заголовком: «отсортировано по …», «фильтр …», «показано 20 из 85».
  String viewNote() {
    final parts = <String>[];
    final sk = sortKey;
    if (sk != null && !card) {
      final c = columns.where((x) => x.key == sk);
      if (c.isNotEmpty) parts.add('${tr5(lang, 'по', 'боюнча', 'bo\'yicha', 'by', 'göre')}: ${c.first.title} ${desc ? '↓' : '↑'}');
    }
    final f = filter;
    if (f != null) parts.add('${tr5(lang, 'фильтр', 'чыпка', 'filtr', 'filter', 'filtre')}: ${f.describe(this)}');
    if (!card) {
      final total = filter == null ? totalRows : viewCount;
      final shownN = visibleIndices.length;
      if (total > shownN) parts.add(tr5(lang, 'показано $shownN из $total', '$total ичинен $shownN көрсөтүлдү', '$total tadan $shownN ko\'rsatildi', 'showing $shownN of $total', '$total içinden $shownN gösteriliyor'));
    }
    return parts.join(' · ');
  }

  /// Текст для чата после звонка и для «Поделиться»: заголовок, строки «а — б — в», итого. Максимум [maxRows] строк.
  String toPlainText({int maxRows = 60, bool revealNames = true}) {
    final sb = StringBuffer(title);
    if (subtitle != null && subtitle!.isNotEmpty) sb.write(' · $subtitle');
    final idx = viewIndices;
    if (card) {
      for (final r in idx) {
        sb.write('\n${cellText(r, 0, revealNames: revealNames)}: ${cellText(r, 1, revealNames: revealNames)}');
      }
    } else {
      sb.write('\n${headers.join(' — ')}');
      for (final r in idx.take(maxRows)) {
        sb.write('\n${[for (var c = 0; c < columns.length; c++) cellText(r, c, revealNames: revealNames)].join(' — ')}');
      }
      if (idx.length > maxRows) sb.write('\n… ${tr5(lang, 'ещё', 'дагы', 'yana', 'more', 'daha')} ${idx.length - maxRows}');
      final tr = totalsRow();
      if (tr != null) sb.write('\n${tr.where((e) => e.isNotEmpty).join(' — ')}');
    }
    if (note != null && note!.isNotEmpty) sb.write('\n$note');
    return sb.toString();
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Для модели (Google): коротко и с кодами
  // ---------------------------------------------------------------------------------------------------------------

  /// Краткая справка для поля `data` ответа инструмента: имя таблицы, колонки, сколько строк, первые [preview] строк
  /// (коды вместо имён, текст обрезан до [clip] символов). Всю таблицу модель не получает — её видит пользователь.
  Map<String, dynamic> modelBrief({int preview = 5, int clip = 40}) {
    final idx = viewIndices;
    String cut(String s) => s.length > clip ? '${s.substring(0, clip - 1)}…' : s;
    return {
      'id': id,
      'title': cut(title),
      'columns': [for (final c in columns) c.key],
      'rows': filter == null ? totalRows : idx.length,
      'shown': visibleIndices.length,
      if (card) 'card': true,
      'preview': [
        for (final r in idx.take(preview))
          [for (var c = 0; c < columns.length; c++) cut(cellText(r, c, revealNames: false))],
      ],
      if (viewTotals.isNotEmpty && !card)
        'totals': {
          for (var c = 0; c < columns.length; c++) columns[c].key: ?totalText(c),
        },
    };
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Форматы
  // ---------------------------------------------------------------------------------------------------------------

  /// Форматы (раздел 4.3 ТЗ): деньги «12 345 сом» (неразрывный пробел, без копеек, если их нет; копейки — «12 345,50»),
  /// количество «2,5 кг», проценты «+12 %», даты «26.10», время «14:05», статусы словами на языке. Отрицательные — с минусом
  /// (красным их делает панель по [isNegative]).
  static String formatCell(Object? v, CellKind kind, String lang, {String? unit, bool signed = true, int? year}) {
    if (v == null) return '—';
    switch (kind) {
      case CellKind.money:
        final n = _asNum(v);
        if (n == null) return '$v';
        return '${_number(n, lang, maxDecimals: 2, padTo2: true)} ${moneyUnit(lang)}';
      case CellKind.qty:
        final n = _asNum(v);
        if (n == null) return '$v';
        final u = unit == null || unit.isEmpty ? '' : ' ${unitWord(unit, lang)}';
        return '${_number(n, lang, maxDecimals: 3)}$u';
      case CellKind.percent:
        final n = _asNum(v);
        if (n == null) return '$v';
        final r = n.abs() >= 10 ? n.roundToDouble() : (n * 10).roundToDouble() / 10;
        final body = _number(r, lang, maxDecimals: 1);
        return '${signed && r > 0 ? '+' : ''}$body %';
      case CellKind.date:
        final d = v is DateTime ? v : DateTime.tryParse('$v');
        if (d == null) return '$v';
        final dd = d.day.toString().padLeft(2, '0');
        final mm = d.month.toString().padLeft(2, '0');
        return year != null && d.year != year ? '$dd.$mm.${(d.year % 100).toString().padLeft(2, '0')}' : '$dd.$mm';
      case CellKind.time:
        final d = v is DateTime ? v : DateTime.tryParse('$v');
        if (d == null) return '$v';
        return '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
      case CellKind.status:
        return statusWord('$v', lang);
      case CellKind.code:
      case CellKind.text:
        return '$v';
    }
  }

  static String moneyUnit(String lang) => switch (lang) {
    'uz' => "so'm",
    'en' || 'tr' => 'som',
    _ => 'сом',
  };

  static String _number(double n, String lang, {required int maxDecimals, bool padTo2 = false}) {
    final group = switch (lang) {
      'en' => ',',
      'tr' => '.',
      _ => ' ',
    };
    final dec = lang == 'en' ? '.' : ',';
    final neg = n < 0;
    var s = n.abs().toStringAsFixed(maxDecimals);
    var intPart = s;
    var frac = '';
    final dot = s.indexOf('.');
    if (dot >= 0) {
      intPart = s.substring(0, dot);
      frac = s.substring(dot + 1);
    }
    // Хвостовые нули не нужны; деньги с копейками — всегда две цифры («12 345,50»).
    frac = frac.replaceFirst(RegExp(r'0+$'), '');
    if (padTo2 && frac.isNotEmpty && frac.length < 2) frac = frac.padRight(2, '0');
    final buf = StringBuffer();
    for (var i = 0; i < intPart.length; i++) {
      if (i > 0 && (intPart.length - i) % 3 == 0) buf.write(group);
      buf.write(intPart[i]);
    }
    final isZero = buf.toString() == '0' && frac.isEmpty;
    return '${neg && !isZero ? '-' : ''}$buf${frac.isEmpty ? '' : '$dec$frac'}';
  }

  /// Единицы товара (в базе они по-русски: шт, кг, л, м) на языке пользователя; незнакомая — как есть.
  static String unitWord(String unit, String lang) {
    final u = unit.trim().toLowerCase().replaceAll('.', '');
    switch (u) {
      case 'шт':
      case 'штука':
      case 'pcs':
        return tr5(lang, 'шт', 'даана', 'dona', 'pcs', 'adet');
      case 'кг':
      case 'kg':
        return tr5(lang, 'кг', 'кг', 'kg', 'kg', 'kg');
      case 'г':
      case 'g':
        return tr5(lang, 'г', 'г', 'g', 'g', 'g');
      case 'л':
      case 'l':
        return tr5(lang, 'л', 'л', 'l', 'L', 'L');
      case 'м':
      case 'm':
        return tr5(lang, 'м', 'м', 'm', 'm', 'm');
      case 'уп':
        return tr5(lang, 'уп', 'таңгак', 'qadoq', 'pack', 'paket');
      default:
        return unit;
    }
  }

  /// Статус словами на языке пользователя (раздел 4.3 ТЗ); неизвестный — как пришёл.
  static String statusWord(String key, String lang) {
    final k = key.trim().toLowerCase();
    switch (k) {
      case 'new':
        return tr5(lang, 'новый', 'жаңы', 'yangi', 'new', 'yeni');
      case 'pending':
        return tr5(lang, 'ожидает', 'күтүүдө', 'kutilmoqda', 'pending', 'bekliyor');
      case 'in_progress':
      case 'processing':
        return tr5(lang, 'в работе', 'иште', 'jarayonda', 'in progress', 'işlemde');
      case 'done':
      case 'completed':
        return tr5(lang, 'сделано', 'бүттү', 'bajarildi', 'done', 'tamamlandı');
      case 'canceled':
      case 'cancelled':
        return tr5(lang, 'отменён', 'жокко чыгарылды', 'bekor qilindi', 'canceled', 'iptal');
      case 'open':
        return tr5(lang, 'открыта', 'ачык', 'ochiq', 'open', 'açık');
      case 'closed':
        return tr5(lang, 'закрыта', 'жабык', 'yopiq', 'closed', 'kapalı');
      case 'cash':
        return tr5(lang, 'наличные', 'накталай', 'naqd', 'cash', 'nakit');
      case 'card':
        return tr5(lang, 'карта', 'карта', 'karta', 'card', 'kart');
      case 'transfer':
      case 'qr':
      case 'mbank':
        return tr5(lang, 'перевод/QR', 'которуу/QR', "o'tkazma/QR", 'transfer/QR', 'havale/QR');
      case 'debt':
        return tr5(lang, 'в долг', 'карызга', 'qarzga', 'on credit', 'borç');
      case 'mixed':
        return tr5(lang, 'смешанная', 'аралаш', 'aralash', 'mixed', 'karma');
      case 'in':
        return tr5(lang, 'внесение', 'киргизүү', 'kirim', 'cash in', 'giriş');
      case 'out':
        return tr5(lang, 'изъятие', 'алуу', 'chiqim', 'cash out', 'çıkış');
      case 'returned':
      case 'refund':
        return tr5(lang, 'возврат', 'кайтаруу', 'qaytarish', 'returned', 'iade');
    }
    return key;
  }
}

/// Таблицы текущего звонка (до [keep], раздел 4.1 ТЗ): текущая, «‹ 2/4 ›», закреплённые, размер текста, экспорт.
/// Панель на экране (В4) слушает [ChangeNotifier]; инструменты чтения кладут сюда таблицы.
class TableStore extends ChangeNotifier {
  final int keep;
  TableStore({this.keep = 10});

  final List<CallTable> _history = [];
  final Set<String> _pinned = {};
  String? _currentId;
  int _n = 0;
  double _textScale = 1.0;
  bool _closed = true;

  /// Экспорт «в чат / PDF / поделиться» делает экран: обработчик вернёт true, если сделал. Нет обработчика — инструмент
  /// честно скажет, что экспорта сейчас нет.
  Future<bool> Function(String action, CallTable table)? onExport;

  String nextId() => 't${++_n}';

  List<CallTable> get history => List.unmodifiable(_history);
  CallTable? get current => byId(_currentId);

  /// Номер текущей в истории (с 1) и сколько всего — «‹ 2/4 ›».
  int get position => _currentId == null ? 0 : _history.indexWhere((t) => t.id == _currentId) + 1;
  int get count => _history.length;
  Set<String> get pinnedIds => Set.unmodifiable(_pinned);
  bool isPinned(String id) => _pinned.contains(id);
  double get textScale => _textScale;

  /// Панель скрыта (закрыта пользователем); новая таблица её открывает.
  bool get isClosed => _closed;

  CallTable? byId(String? id) {
    if (id == null) return null;
    for (final t in _history) {
      if (t.id == id) return t;
    }
    return null;
  }

  /// Новая таблица становится текущей. Закреплённые остаются рядом вкладками; лишние старые (не закреплённые) уходят.
  void add(CallTable t) {
    _history.removeWhere((x) => x.id == t.id);
    _history.add(t);
    _currentId = t.id;
    _closed = false;
    while (_history.length > keep) {
      final i = _history.indexWhere((x) => !_pinned.contains(x.id) && x.id != _currentId);
      if (i < 0) break;
      _history.removeAt(i);
    }
    notifyListeners();
  }

  /// Заменить таблицу после сортировки, фильтра или «ещё» (тот же id).
  void replace(CallTable t) {
    final i = _history.indexWhere((x) => x.id == t.id);
    if (i < 0) return add(t);
    _history[i] = t;
    notifyListeners();
  }

  bool show(String id) {
    if (byId(id) == null) return false;
    _currentId = id;
    _closed = false;
    notifyListeners();
    return true;
  }

  /// «‹ ›»: предыдущая (-1) или следующая (+1).
  void step(int delta) {
    final p = position;
    if (p == 0) return;
    final i = (p - 1 + delta).clamp(0, _history.length - 1);
    _currentId = _history[i].id;
    notifyListeners();
  }

  void pin(String id) {
    if (byId(id) == null) return;
    _pinned.add(id);
    notifyListeners();
  }

  void unpin(String id) {
    if (_pinned.remove(id)) notifyListeners();
  }

  /// «✕»: панель закрывается, таблица остаётся в истории (её можно вернуть «покажи таблицу»).
  void close() {
    _closed = true;
    notifyListeners();
  }

  /// Размер текста 1.0 → 1.3 → 1.6 (раздел 4.2 ТЗ); [bigger] false — обратно.
  double resize(bool bigger) {
    const steps = [1.0, 1.3, 1.6];
    var i = steps.indexOf(_textScale);
    if (i < 0) i = 0;
    i = (i + (bigger ? 1 : -1)).clamp(0, steps.length - 1);
    _textScale = steps[i];
    notifyListeners();
    return _textScale;
  }

  /// Коды [Д1] в тексте → имена: сначала текущая таблица, потом остальные от новых к старым.
  String reveal(String text) {
    var out = text;
    final order = [?current, ..._history.reversed.where((t) => t.id != _currentId)];
    for (final t in order) {
      out = t.reveal(out);
    }
    return out;
  }

  /// Новый звонок: всё сначала.
  void reset() {
    _history.clear();
    _pinned.clear();
    _currentId = null;
    _n = 0;
    _textScale = 1.0;
    _closed = true;
    notifyListeners();
  }
}
