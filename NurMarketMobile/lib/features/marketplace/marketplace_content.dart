import 'dart:convert';

import 'package:flutter/services.dart' show rootBundle;

/// 2026-11-27 (просьба владельца «И в мобилку тоже Маркетплейс добавь!»): тексты Маркетплейса — из ассета `assets/marketplace/content.json`.
/// Файл собирает `tool/gen_marketplace_content.py` из C#-текстов кассы (MarketplaceModuleContent*, MarketplaceContent.*, MarketplaceDemos*), поэтому тексты в кассе
/// и в мобилке не расходятся; руками файл не правят — перезапускают скрипт. Здесь только чтение: язык выбирается по коду (ru, ky, en, tr, uz), нет перевода — русский.

/// Текст на пяти языках.
class LText {
  final Map<String, String> _m;
  const LText(this._m);

  static const empty = LText({});

  static LText? parse(Object? v) {
    if (v is String) return v.isEmpty ? null : LText({'ru': v});
    if (v is! Map) return null;
    final m = <String, String>{};
    for (final e in v.entries) {
      if (e.value is String && (e.value as String).trim().isNotEmpty) m['${e.key}'] = e.value as String;
    }
    return m.isEmpty ? null : LText(m);
  }

  static List<LText> list(Object? v) => v is List
      ? [for (final x in v) ?LText.parse(x)]
      : const [];

  bool get isEmpty => _m.isEmpty;

  /// Текст на языке [lang]; нет — русский, нет — любой.
  String of(String lang) => _m[lang] ?? _m['ru'] ?? (_m.values.isEmpty ? '' : _m.values.first);

  bool has(String lang) => _m[lang]?.isNotEmpty ?? false;
}

/// Пункт «Что получите» / «Что внутри»: значок (ключ MDI из кассы), заголовок и строка.
class MpGain {
  final String icon;
  final LText title;
  final LText text;
  const MpGain(this.icon, this.title, this.text);
}

/// Часть кадра демонстрации «как работает».
class MpPart {
  /// input / row / btn / note / toggle / total / chips / tiles
  final String kind;
  final LText a; // подпись / текст
  final LText b; // значение / правая часть
  final LText sub;
  final String tone; // normal / good / warn / bad / accent
  final bool hot;
  final bool on; // toggle: включено; btn: акцентная
  final int index; // chips: выбранная; tiles: подсвеченная
  final List<LText> items;
  const MpPart(this.kind, {this.a = LText.empty, this.b = LText.empty, this.sub = LText.empty, this.tone = 'normal', this.hot = false, this.on = false, this.index = -1, this.items = const []});
}

/// Шаг демонстрации: подпись («1. Нажмите…»), название окна и кадр.
class MpStep {
  final LText caption;
  final LText title;
  final List<MpPart> parts;
  const MpStep(this.caption, this.title, this.parts);
}

/// Запись контента одного модуля (или пакета) тарифа.
class MpModuleContent {
  final String id;
  final LText name;
  final String icon;
  final String color; // #RRGGBB или ''
  final LText line; // одна строка «что делает» для плитки
  final LText short; // заголовок-польза страницы
  final LText what; // «Что делает» подробно
  final List<MpGain> benefits; // выгоды (у Сметы, Проката, Ремонта, Trade-in)
  final List<MpGain> gains; // «Что получите»
  final List<LText> steps; // «Как включить»
  final List<LText> audience; // «Для кого»
  final List<MpStep> demo; // демонстрация
  const MpModuleContent({
    required this.id,
    this.name = LText.empty,
    this.icon = '',
    this.color = '',
    this.line = LText.empty,
    this.short = LText.empty,
    this.what = LText.empty,
    this.benefits = const [],
    this.gains = const [],
    this.steps = const [],
    this.audience = const [],
    this.demo = const [],
  });

  /// Есть что показать кроме названия: хоть одна строка описания.
  bool get hasText => !line.isEmpty || !short.isEmpty || !what.isEmpty;

  static List<MpGain> _gains(Object? v) => v is List
      ? [
          for (final x in v)
            if (x is Map && LText.parse(x['title']) != null) MpGain('${x['icon'] ?? ''}', LText.parse(x['title'])!, LText.parse(x['text']) ?? LText.empty),
        ]
      : const [];

  static MpPart? _part(Map p) {
    final k = '${p['t']}';
    LText t(String key) => LText.parse(p[key]) ?? LText.empty;
    final tone = '${p['tone'] ?? 'normal'}';
    final hot = p['hot'] == true;
    switch (k) {
      case 'input':
        return MpPart('input', a: t('label'), b: t('value'), hot: hot);
      case 'row':
        return MpPart('row', a: t('text'), b: t('right'), sub: t('sub'), tone: tone, hot: hot);
      case 'btn':
        return MpPart('btn', a: t('text'), hot: hot, on: p['primary'] != false);
      case 'note':
        return MpPart('note', a: t('text'), tone: tone);
      case 'toggle':
        return MpPart('toggle', a: t('label'), on: p['on'] == true, hot: hot);
      case 'total':
        return MpPart('total', a: t('label'), b: t('value'), hot: hot);
      case 'chips':
        return MpPart('chips', index: (p['selected'] as num?)?.toInt() ?? -1, items: LText.list(p['items']));
      case 'tiles':
        return MpPart('tiles', index: (p['hot'] as num?)?.toInt() ?? -1, items: LText.list(p['items']));
    }
    return null;
  }

  factory MpModuleContent.fromJson(String id, Map<String, dynamic> j) => MpModuleContent(
    id: id,
    name: LText.parse(j['name']) ?? LText.empty,
    icon: '${j['icon'] ?? ''}',
    color: '${j['color'] ?? ''}',
    line: LText.parse(j['line']) ?? LText.empty,
    short: LText.parse(j['short']) ?? LText.empty,
    what: LText.parse(j['what']) ?? LText.empty,
    benefits: _gains(j['benefits']),
    gains: _gains(j['gains']),
    steps: LText.list(j['steps']),
    audience: LText.list(j['audience']),
    demo: [
      if (j['demo'] is List)
        for (final s in j['demo'] as List)
          if (s is Map)
            MpStep(LText.parse(s['caption']) ?? LText.empty, LText.parse(s['title']) ?? LText.empty, [
              if (s['parts'] is List)
                for (final p in s['parts'] as List)
                  if (p is Map && _part(p) != null) _part(p)!,
            ]),
    ],
  );
}

/// Карточка функции, которой сервер как модуля компании пока не отдаёт (бот-продавец, ЭСФ, онлайн-ККМ, рассылки, 1С, QR…) — как MarketplaceFeatureCards кассы.
/// [mode]: capability — функцию открывает сервер признаком [flag] (есть — «Доступно», нет — «Скоро»); fiscal — онлайн-ККМ (режим включает NurCRM по заявке);
/// builtin — входит в программу без доплаты; service — услуга поддержки.
class MpFeature {
  final String id;
  final String mode;
  final String? flag;
  final LText need;
  const MpFeature(this.id, this.mode, this.flag, this.need);
}

/// Все тексты Маркетплейса: модули, пакеты тарифа, карточки функций и названия групп.
class MarketplaceContent {
  final Map<String, MpModuleContent> modules;
  final Map<String, MpModuleContent> packs;
  final Map<String, LText> groups;
  final Map<String, MpFeature> features;
  const MarketplaceContent(this.modules, this.packs, this.groups, [this.features = const {}]);

  static const emptyContent = MarketplaceContent({}, {}, {});

  factory MarketplaceContent.fromJson(Map<String, dynamic> j) {
    Map<String, MpModuleContent> mods(Object? v) => {
      if (v is Map)
        for (final e in v.entries)
          if (e.value is Map) '${e.key}': MpModuleContent.fromJson('${e.key}', (e.value as Map).cast<String, dynamic>()),
    };
    return MarketplaceContent(mods(j['modules']), mods(j['packs']), {
      if (j['groups'] is Map)
        for (final e in (j['groups'] as Map).entries)
          if (LText.parse(e.value) != null) '${e.key}': LText.parse(e.value)!,
    }, {
      if (j['features'] is Map)
        for (final e in (j['features'] as Map).entries)
          if (e.value is Map) '${e.key}': MpFeature('${e.key}', '${(e.value as Map)['mode'] ?? 'builtin'}', (e.value as Map)['flag'] as String?, LText.parse((e.value as Map)['need']) ?? LText.empty),
    });
  }

  MpModuleContent? module(String id) => modules[id];
  MpModuleContent? pack(String code) => packs[code] ?? packs[code.replaceAll('_', '')];

  static const assetPath = 'assets/marketplace/content.json';
  static MarketplaceContent? _cached;

  /// Тексты из ассета (читаются один раз); испорченный или отсутствующий файл — пустой набор: раздел покажет названия и описания сервера, а не упадёт.
  static Future<MarketplaceContent> load() async {
    final c = _cached;
    if (c != null) return c;
    try {
      final raw = await rootBundle.loadString(assetPath);
      return _cached = MarketplaceContent.fromJson((jsonDecode(raw) as Map).cast<String, dynamic>());
    } catch (_) {
      return emptyContent;
    }
  }

  /// Только для тестов: подложить набор вместо ассета (null — сбросить).
  static void setForTests(MarketplaceContent? c) => _cached = c;
}
