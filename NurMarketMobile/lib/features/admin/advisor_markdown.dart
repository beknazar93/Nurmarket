import 'package:flutter/material.dart';

import '../../core/util/ui.dart';

// 2026-10-26 (живой аудит, П11): свой разбор Markdown для ответов Нур Советника — без новых зависимостей.
// Заголовки #, жирный **…**, курсив *…*/_…_, `код`, списки (- * • 1.), цитата >, линия ---, блок ``` и ТАБЛИЦЫ |…| (с прокруткой
// по горизонтали). Было: ответ показывался сырым текстом с «|» и «**».

enum MdKind { heading, para, bullet, numbered, table, rule, code, quote }

/// Кусок текста с оформлением (результат разбора строки).
class MdSpan {
  final String text;
  final bool bold;
  final bool italic;
  final bool code;
  const MdSpan(this.text, {this.bold = false, this.italic = false, this.code = false});
  @override
  String toString() => 'MdSpan($text${bold ? ' b' : ''}${italic ? ' i' : ''}${code ? ' c' : ''})';
}

/// Блок ответа: для таблицы — [header] + [rows] + [aligns] (-1 слева, 0 по центру, 1 справа), иначе — [text].
class MdBlock {
  final MdKind kind;
  final String text;
  final int level; // заголовок 1…6; вложенность списка 0…3
  final String marker; // «12.» у нумерованного
  final List<String> header;
  final List<List<String>> rows;
  final List<int> aligns;
  const MdBlock(this.kind, {this.text = '', this.level = 0, this.marker = '', this.header = const [], this.rows = const [], this.aligns = const []});
}

final _sepRow = RegExp(r'^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$');
final _headingRe = RegExp(r'^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$');
final _bulletRe = RegExp(r'^(\s*)([-*•+])\s+(.*)$');
final _numberedRe = RegExp(r'^(\s*)(\d{1,3}[.)])\s+(.*)$');
final _ruleRe = RegExp(r'^\s*([-*_])(\s*\1){2,}\s*$');

bool _isTableSep(String line) => line.contains('-') && _sepRow.hasMatch(line) && (line.contains('|') || line.contains(':'));

/// Ячейки строки таблицы: крайние «|» отбрасываются, «\|» — это символ «|» внутри ячейки.
List<String> mdSplitRow(String line) {
  var t = line.trim();
  if (t.startsWith('|')) t = t.substring(1);
  if (t.endsWith('|') && !t.endsWith(r'\|')) t = t.substring(0, t.length - 1);
  final cells = <String>[];
  final cur = StringBuffer();
  for (var i = 0; i < t.length; i++) {
    final c = t[i];
    if (c == r'\' && i + 1 < t.length && t[i + 1] == '|') {
      cur.write('|');
      i++;
    } else if (c == '|') {
      cells.add(cur.toString().trim());
      cur.clear();
    } else {
      cur.write(c);
    }
  }
  cells.add(cur.toString().trim());
  return cells;
}

/// Разбор ответа на блоки. Пустые строки разделяют абзацы; подряд идущие строки текста остаются одним абзацем (с переносами).
List<MdBlock> parseMarkdown(String src) {
  final lines = src.replaceAll('\r\n', '\n').replaceAll('\r', '\n').split('\n');
  final out = <MdBlock>[];
  final para = <String>[];
  void flush() {
    if (para.isEmpty) return;
    out.add(MdBlock(MdKind.para, text: para.join('\n')));
    para.clear();
  }

  var i = 0;
  while (i < lines.length) {
    final line = lines[i];
    final trimmed = line.trim();
    if (trimmed.isEmpty) {
      flush();
      i++;
      continue;
    }
    if (trimmed.startsWith('```')) {
      flush();
      final code = <String>[];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        code.add(lines[i]);
        i++;
      }
      i++; // закрывающая ```
      out.add(MdBlock(MdKind.code, text: code.join('\n')));
      continue;
    }
    // таблица: строка с «|» и сразу под ней строка-разделитель «|---|---|»
    if (line.contains('|') && i + 1 < lines.length && _isTableSep(lines[i + 1])) {
      flush();
      final header = mdSplitRow(line);
      final sep = mdSplitRow(lines[i + 1]);
      final rows = <List<String>>[];
      i += 2;
      while (i < lines.length && lines[i].trim().isNotEmpty && lines[i].contains('|')) {
        rows.add(mdSplitRow(lines[i]));
        i++;
      }
      var cols = header.length;
      for (final r in rows) {
        if (r.length > cols) cols = r.length;
      }
      List<String> fit(List<String> r) => [...r, for (var k = r.length; k < cols; k++) ''];
      final aligns = [
        for (var k = 0; k < cols; k++)
          if (k < sep.length && sep[k].endsWith(':') && sep[k].startsWith(':'))
            0
          else if (k < sep.length && sep[k].endsWith(':'))
            1
          else
            -1,
      ];
      out.add(MdBlock(MdKind.table, header: fit(header), rows: [for (final r in rows) fit(r)], aligns: aligns));
      continue;
    }
    final h = _headingRe.firstMatch(line);
    if (h != null) {
      flush();
      out.add(MdBlock(MdKind.heading, text: h.group(2)!, level: h.group(1)!.length));
      i++;
      continue;
    }
    if (_ruleRe.hasMatch(line)) {
      flush();
      out.add(const MdBlock(MdKind.rule));
      i++;
      continue;
    }
    final b = _bulletRe.firstMatch(line);
    if (b != null) {
      flush();
      out.add(MdBlock(MdKind.bullet, text: b.group(3)!, level: (b.group(1)!.length ~/ 2).clamp(0, 3)));
      i++;
      continue;
    }
    final n = _numberedRe.firstMatch(line);
    if (n != null) {
      flush();
      out.add(MdBlock(MdKind.numbered, text: n.group(3)!, marker: n.group(2)!, level: (n.group(1)!.length ~/ 2).clamp(0, 3)));
      i++;
      continue;
    }
    if (trimmed.startsWith('>')) {
      flush();
      out.add(MdBlock(MdKind.quote, text: trimmed.replaceFirst(RegExp(r'^>\s?'), '')));
      i++;
      continue;
    }
    para.add(line.trimRight());
    i++;
  }
  flush();
  return out;
}

bool _isWordChar(String? c) => c != null && c.isNotEmpty && RegExp(r'[\p{L}\p{N}]', unicode: true).hasMatch(c);

/// Разбор строки: `код`, **жирный**/__жирный__, *курсив*/_курсив_. Незакрытые знаки остаются обычным текстом.
List<MdSpan> parseInline(String s, {bool bold = false, bool italic = false}) {
  final out = <MdSpan>[];
  final buf = StringBuffer();
  void flush() {
    if (buf.isEmpty) return;
    out.add(MdSpan(buf.toString(), bold: bold, italic: italic));
    buf.clear();
  }

  var i = 0;
  while (i < s.length) {
    final c = s[i];
    if (c == r'\' && i + 1 < s.length && '*_`\\|'.contains(s[i + 1])) {
      buf.write(s[i + 1]);
      i += 2;
      continue;
    }
    if (c == '`') {
      final end = s.indexOf('`', i + 1);
      if (end > i + 1) {
        flush();
        out.add(MdSpan(s.substring(i + 1, end), bold: bold, italic: italic, code: true));
        i = end + 1;
        continue;
      }
    }
    if ((s.startsWith('**', i) || s.startsWith('__', i)) && i + 2 < s.length && s[i + 2] != ' ') {
      final mark = s.substring(i, i + 2);
      final end = s.indexOf(mark, i + 2);
      if (end > i + 2 && s[end - 1] != ' ') {
        flush();
        out.addAll(parseInline(s.substring(i + 2, end), bold: true, italic: italic));
        i = end + 2;
        continue;
      }
    }
    if ((c == '*' || c == '_') && i + 1 < s.length && s[i + 1] != ' ' && s[i + 1] != c) {
      // «_» внутри слова (snake_case) и «*» в «2 * 3» курсивом не считаем
      final prev = i > 0 ? s[i - 1] : null;
      if (c == '*' || !_isWordChar(prev)) {
        var end = s.indexOf(c, i + 1);
        while (end != -1 && (s[end - 1] == ' ' || (c == '_' && _isWordChar(end + 1 < s.length ? s[end + 1] : null)))) {
          end = s.indexOf(c, end + 1);
        }
        if (end > i + 1) {
          flush();
          out.addAll(parseInline(s.substring(i + 1, end), bold: bold, italic: true));
          i = end + 1;
          continue;
        }
      }
    }
    buf.write(c);
    i++;
  }
  flush();
  return out;
}

/// Текст без знаков разметки (для проверок и копирования).
String mdPlain(String s) => parseInline(s).map((e) => e.text).join();

final _numCell = RegExp(r'^[-+−–]?\s*[\d\s.,  ]+\s*(%|сом|с|шт\.?|som|kg|кг)?$', caseSensitive: false);
bool _isNumeric(String c) {
  final t = mdPlain(c).trim();
  return t.isNotEmpty && RegExp(r'\d').hasMatch(t) && _numCell.hasMatch(t);
}

/// Ответ Советника: блоки Markdown одним столбцом. Таблица — со своей горизонтальной прокруткой.
class MdView extends StatelessWidget {
  final String text;
  final Color color;
  final double fontSize;
  const MdView(this.text, {super.key, required this.color, this.fontSize = 14});

  TextSpan _spans(BuildContext context, String s, TextStyle base) => TextSpan(
    style: base,
    children: [
      for (final p in parseInline(s))
        TextSpan(
          text: p.text,
          style: TextStyle(
            fontWeight: p.bold ? FontWeight.w800 : null,
            fontStyle: p.italic ? FontStyle.italic : null,
            fontFamily: p.code ? 'monospace' : null,
            backgroundColor: p.code ? context.pal.accentSoft : null,
          ),
        ),
    ],
  );

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final base = TextStyle(color: color, fontSize: fontSize, height: 1.35);
    final blocks = parseMarkdown(text);
    final children = <Widget>[];
    for (var k = 0; k < blocks.length; k++) {
      final b = blocks[k];
      final gap = k == 0 ? 0.0 : (b.kind == MdKind.bullet || b.kind == MdKind.numbered) && (blocks[k - 1].kind == b.kind) ? 2.0 : 8.0;
      Widget w;
      switch (b.kind) {
        case MdKind.heading:
          final size = switch (b.level) {
            1 => fontSize + 4,
            2 => fontSize + 2.5,
            3 => fontSize + 1,
            _ => fontSize,
          };
          w = Text.rich(_spans(context, b.text, base.copyWith(fontSize: size, fontWeight: FontWeight.w800)));
        case MdKind.para:
          w = Text.rich(_spans(context, b.text, base));
        case MdKind.bullet:
        case MdKind.numbered:
          w = Padding(
            padding: EdgeInsets.only(left: 4.0 + b.level * 14),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: b.kind == MdKind.bullet ? 16 : 26,
                  child: Text(b.kind == MdKind.bullet ? '•' : b.marker, style: base.copyWith(fontWeight: FontWeight.w700)),
                ),
                Expanded(child: Text.rich(_spans(context, b.text, base))),
              ],
            ),
          );
        case MdKind.quote:
          w = Container(
            padding: const EdgeInsets.only(left: 10),
            decoration: BoxDecoration(border: Border(left: BorderSide(color: pal.border, width: 3))),
            child: Text.rich(_spans(context, b.text, base.copyWith(color: pal.textSoft))),
          );
        case MdKind.rule:
          w = Divider(height: 8, color: pal.border);
        case MdKind.code:
          w = Container(
            width: double.infinity,
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(color: pal.window, borderRadius: BorderRadius.circular(8), border: Border.all(color: pal.border)),
            child: SingleChildScrollView(scrollDirection: Axis.horizontal, child: Text(b.text, style: base.copyWith(fontFamily: 'monospace', fontSize: fontSize - 1))),
          );
        case MdKind.table:
          w = MdTable(block: b, style: base);
      }
      children.add(Padding(padding: EdgeInsets.only(top: gap), child: w));
    }
    return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: children);
  }
}

/// Таблица ответа: шапка жирная на подложке, строки с тонкими линиями, числа — по правому краю; шире экрана — прокрутка вбок.
class MdTable extends StatelessWidget {
  final MdBlock block;
  final TextStyle style;
  const MdTable({super.key, required this.block, required this.style});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final cols = block.header.length;
    final numeric = [
      for (var c = 0; c < cols; c++)
        block.aligns[c] == 1 || (block.rows.isNotEmpty && block.rows.every((r) => r[c].trim().isEmpty || _isNumeric(r[c]))),
    ];
    TextAlign align(int c) => block.aligns[c] == 0 ? TextAlign.center : (numeric[c] ? TextAlign.right : TextAlign.left);
    Widget cell(String t, int c, {bool head = false}) => ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 220),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
        child: Text.rich(
          TextSpan(
            style: style.copyWith(fontSize: (style.fontSize ?? 14) - 0.5, fontWeight: head ? FontWeight.w800 : null),
            children: [
              for (final p in parseInline(t))
                TextSpan(
                  text: p.text,
                  style: TextStyle(
                    fontWeight: (p.bold || head) ? FontWeight.w800 : null,
                    fontStyle: p.italic ? FontStyle.italic : null,
                    fontFamily: p.code ? 'monospace' : null,
                  ),
                ),
            ],
          ),
          textAlign: align(c),
        ),
      ),
    );
    final line = BorderSide(color: pal.border);
    return Container(
      decoration: BoxDecoration(borderRadius: BorderRadius.circular(8), border: Border.all(color: pal.border)),
      clipBehavior: Clip.antiAlias,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Table(
          defaultColumnWidth: const IntrinsicColumnWidth(),
          border: TableBorder(horizontalInside: line),
          children: [
            TableRow(
              decoration: BoxDecoration(color: pal.accentSoft),
              children: [for (var c = 0; c < cols; c++) cell(block.header[c], c, head: true)],
            ),
            for (var r = 0; r < block.rows.length; r++)
              TableRow(
                decoration: r.isOdd ? BoxDecoration(color: pal.window.withValues(alpha: .5)) : null,
                children: [for (var c = 0; c < cols; c++) cell(block.rows[r][c], c)],
              ),
          ],
        ),
      ),
    );
  }
}
