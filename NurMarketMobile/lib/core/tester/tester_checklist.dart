import 'dart:convert';

import 'package:flutter/services.dart' show AssetBundle, rootBundle;

/// 2026-10-27: список функций для проверки тестировщиком — ДАННЫЕ (assets/data/tester_checklist.json), а не код.
/// Новые пункты дописываются в JSON к каждому выпуску: id стабильный («версия/слаг»), тексты сразу на 5 языках.
enum TesterSeverity {
  /// 🔴 деньги и остатки
  money,

  /// 🟡 важное
  important,

  /// ⚪ обычное
  normal;

  static TesterSeverity parse(Object? v) => switch ('$v'.toLowerCase()) {
    'money' || 'high' || 'red' => money,
    'important' || 'medium' || 'yellow' => important,
    _ => normal,
  };
}

class TesterArea {
  final String id;
  final Map<String, String> title;
  const TesterArea(this.id, this.title);
  String name(String lang) => tLocal(title, lang);
}

class TesterItem {
  final String id;
  final String version;
  final String area;
  final TesterSeverity severity;
  final Map<String, String> title;
  final Map<String, String> changed;
  final Map<String, List<String>> steps;

  const TesterItem({
    required this.id,
    required this.version,
    required this.area,
    required this.severity,
    required this.title,
    required this.changed,
    required this.steps,
  });

  String titleIn(String lang) => tLocal(title, lang);
  String changedIn(String lang) => tLocal(changed, lang);
  List<String> stepsIn(String lang) {
    final s = steps[lang];
    if (s != null && s.isNotEmpty) return s;
    return steps['ru'] ?? const [];
  }
}

/// Текст на языке [lang]; нет — по-русски; нет и его — любой первый.
String tLocal(Map<String, String> m, String lang) {
  final t = m[lang];
  if (t != null && t.isNotEmpty) return t;
  final ru = m['ru'];
  if (ru != null && ru.isNotEmpty) return ru;
  return m.values.firstWhere((v) => v.isNotEmpty, orElse: () => '');
}

class TesterChecklist {
  final List<TesterArea> areas;
  final List<TesterItem> items;
  const TesterChecklist(this.areas, this.items);

  static const assetPath = 'assets/data/tester_checklist.json';

  static Future<TesterChecklist> load({AssetBundle? bundle}) async {
    try {
      return parse(await (bundle ?? rootBundle).loadString(assetPath));
    } catch (_) {
      return const TesterChecklist([], []);
    }
  }

  static Map<String, String> _texts(Object? v) {
    if (v is String) return {'ru': v};
    if (v is! Map) return const {};
    return {
      for (final e in v.entries)
        if (e.value is String) '${e.key}': e.value as String,
    };
  }

  static Map<String, List<String>> _steps(Object? v) {
    if (v is! Map) return const {};
    final out = <String, List<String>>{};
    for (final e in v.entries) {
      final x = e.value;
      if (x is List) {
        out['${e.key}'] = [
          for (final s in x)
            if (s is String && s.trim().isNotEmpty) s.trim(),
        ];
      } else if (x is String) {
        out['${e.key}'] = [
          for (final s in x.split('\n'))
            if (s.trim().isNotEmpty) s.trim(),
        ];
      }
    }
    return out;
  }

  /// Разбор JSON; повреждённый пункт пропускается, а не роняет весь список.
  static TesterChecklist parse(String source) {
    Object? data;
    try {
      data = jsonDecode(source);
    } catch (_) {
      return const TesterChecklist([], []); // повреждённый файл — пустой список, а не падение
    }
    if (data is! Map) return const TesterChecklist([], []);
    final areas = <TesterArea>[
      for (final a in (data['areas'] is List ? data['areas'] as List : const []))
        if (a is Map && a['id'] != null) TesterArea('${a['id']}', _texts(a['title'])),
    ];
    final items = <TesterItem>[];
    final seen = <String>{};
    for (final i in (data['items'] is List ? data['items'] as List : const [])) {
      if (i is! Map) continue;
      final id = '${i['id'] ?? ''}';
      final version = '${i['version'] ?? ''}';
      if (id.isEmpty || version.isEmpty || !seen.add(id)) continue;
      final title = _texts(i['title']);
      if (title.isEmpty) continue;
      items.add(
        TesterItem(
          id: id,
          version: version,
          area: '${i['area'] ?? 'other'}',
          severity: TesterSeverity.parse(i['severity']),
          title: title,
          changed: _texts(i['changed']),
          steps: _steps(i['steps']),
        ),
      );
    }
    return TesterChecklist(areas, items);
  }

  /// Версии по убыванию (1.18.10 > 1.18.9).
  List<String> get versions {
    final v = {for (final i in items) i.version}.toList();
    v.sort((a, b) => compareVersions(b, a));
    return v;
  }

  List<TesterItem> forVersion(String version) => [
    for (final i in items)
      if (i.version == version) i,
  ];

  /// Версия по умолчанию: [current], если для неё есть пункты; иначе самая новая из списка.
  String defaultVersion(String current) {
    final v = versions;
    if (v.contains(current)) return current;
    return v.isEmpty ? current : v.first;
  }

  /// Области в порядке JSON, только те, где есть пункты [of]; неизвестная область — в конец («Прочее»).
  List<(TesterArea, List<TesterItem>)> grouped(List<TesterItem> of) {
    final byArea = <String, List<TesterItem>>{};
    for (final i in of) {
      byArea.putIfAbsent(i.area, () => []).add(i);
    }
    final out = <(TesterArea, List<TesterItem>)>[];
    for (final a in areas) {
      final l = byArea.remove(a.id);
      if (l != null) out.add((a, l));
    }
    for (final e in byArea.entries) {
      out.add((TesterArea(e.key, {'ru': e.key}), e.value));
    }
    return out;
  }

  static int compareVersions(String a, String b) {
    List<int> p(String s) => [for (final x in s.split(RegExp(r'[.+-]'))) int.tryParse(x) ?? 0];
    final x = p(a), y = p(b);
    for (var i = 0; i < (x.length > y.length ? x.length : y.length); i++) {
      final d = (i < x.length ? x[i] : 0) - (i < y.length ? y[i] : 0);
      if (d != 0) return d;
    }
    return 0;
  }
}
