// 2026-10-27 (расследование «ИИ неправильно открывает разделы и долго думает»): ЛОКАЛЬНЫЙ маршрутизатор навигации без ИИ.
// Короткая команда «открой / перейди / зайди / ач / och / open / aç …» разбирается здесь за миллисекунды по реестру экранов
// (screen_registry.dart), а не через Gemini: в чате Советника и в звонке это ПЕРВЫЙ путь, модель — запасной. Причины прежних
// ошибок, которые закрывает файл: (1) модель выбирала ключ экрана вслепую (в схеме были только ключи без названий), (2) при двух
// равных экранах не было вариантов — только «не нашла», (3) разговорные слова и падежи («приёмки», «накладную», «кассой») не
// совпадали с основой в 5 букв, (4) в чате Советника навигации не было вовсе — ИИ писал «открыла», ничего не открыв.
//
// Правила разбора (коротко): нужен глагол-просьба; «покажи / show / көрсөт / ko'rsat / göster» — мягкий (открывает, только если
// сказано «раздел / вкладка / экран» или назван сам экран по названию; «покажи выручку» остаётся вопросом к ИИ); лишние слова
// («э-э», «пожалуйста», «раздел», предлоги) отбрасываются; остаток, который не объяснён ни экраном, ни параметром, — это не
// короткая команда, и она уходит к ИИ; при ничьей — не более 4 вариантов кнопками; родитель и потомок («склад» + «приёмка»)
// не ничья — берётся потомок. Файл без Flutter и без сети.
import '../../api/session_store.dart';
import 'screen_registry.dart';

enum NavKind {
  /// Не навигация: пусть отвечает ИИ.
  none,

  /// Однозначно открыть [NavDecision.def] с [NavDecision.args].
  open,

  /// Несколько подходящих экранов — показать кнопки ([NavDecision.options], не больше 4).
  choose,

  /// Просьба открыть, но экран не понят («открой что-нибудь»): подсказать, что можно открыть.
  unknown,

  /// Открыть чек по номеру или последний.
  check,

  /// Открыть карточку товара по названию / штрихкоду.
  product,

  /// Такого раздела нет, но есть подсказка (например «Списание» — в карточке товара).
  hint,
}

class NavDecision {
  final NavKind kind;
  final ScreenDef? def;
  final Map<String, String> args;
  final List<ScreenDef> options;
  final String? query;
  final String? checkNumber;
  final bool checkLast;
  final String? hint;

  /// Разбор уверенный: сказан сильный глагол, экран один и заметно лучше остальных. Только такой разбор может поправить выбор
  /// модели в звонке ([NavDecision.supports]).
  final bool confident;

  /// Все экраны, подходящие под слова пользователя (для проверки «модель выбрала то, о чём просили»).
  final Set<String> matchedKeys;
  const NavDecision(
    this.kind, {
    this.def,
    this.args = const {},
    this.options = const [],
    this.query,
    this.checkNumber,
    this.checkLast = false,
    this.hint,
    this.confident = false,
    this.matchedKeys = const {},
  });

  static const none = NavDecision(NavKind.none);

  /// Слова пользователя вообще допускают экран [key] (хоть как-то называют его).
  bool supports(String key) => matchedKeys.contains(key);

  @override
  String toString() => 'NavDecision($kind, ${def?.key}, $args, ${options.map((e) => e.key).toList()})';
}

// ---------------------------------------------------------------------------------------------------------------------
// Слова
// ---------------------------------------------------------------------------------------------------------------------

Set<String> _fold(String words) => {for (final w in words.split(RegExp(r'\s+'))) if (w.isNotEmpty) ...foldTokens(w)};

/// Сильные глаголы: просьба открыть.
final _strong = _fold(
  // ru
  'открой откройте открыть открывай открывайте перейди перейдите перейти переходи зайди зайдите зайти заходи '
  // ky
  'ач ачып ачкыла ачкылачы ачыңыз ачсаң ачсаңыз ачсанбы аччы ачайын '
  // uz
  'och ochib ochgin oching ochsang ochsangiz ochaylik oting kiring '
  // en
  'open launch navigate goto '
  // tr
  'aç aççın açın açar açarmısın gidelim git geçin',
);

/// Мягкие глаголы: «покажи» — часто вопрос к данным, а не просьба открыть экран.
final _soft = _fold(
  'покажи покажите показать показывай '
  'көрсөт көрсөтчү көрсөтүңүз көрсөтсөңүз '
  "ko'rsat ko'rsating ko'rsatib "
  'show '
  'göster gösterin göstermisin',
);

/// Слова, которые отбрасываются до разбора (вежливость, запинки, предлоги).
final _filler = _fold(
  // ru
  'пожалуйста пожалуста плиз мне нам нужно нужен нужна надо можно можешь можете давай давайте хочу хотел хотела быстро сейчас '
  'скорее ну у э ээ эээ эм эмм м мм а вот так это этот эту эта все ещё еще бы же ли сюда туда там типа короче значит слушай '
  'в во на к ко по до из от за про для о об и с со '
  // ky
  'сураныч сураңыз бир азыр тез мага керек болот го мени эми '
  // uz
  'iltimos menga kerak tezroq hozir endi bir yaxshi mayli ha va uchun '
  // tr
  'lütfen lutfen bana lazım lazim hemen şimdi simdi şu bu ve bi bakalım '
  // en
  'please me the a an to now quickly can you could would just and hey ok okay for up uh um ah hmm into in on of',
);

/// Слова, которые не мешают разбору, но и не объясняют экран («мой профиль»).
final _ignorable = _fold('мой моя мою мои мое мой моё my нур nur что эмне nima qaysi what which ne hangi какой какая какие какое');

/// «Раздел / вкладка / экран» — признак просьбы открыть экран даже при мягком глаголе.
final _sectionNoun = _fold(
  'раздел разделе раздела разделу разделы вкладка вкладку вкладки вкладке экран экране экрана экрану окно страница страницу страницы меню пункт модуль '
  'бөлүм бөлүмүн бөлүмгө бөлүмү бетти бет барак экранды терезе '
  "bo'lim bo'limi bo'limga bo'limni sahifa sahifani ekran ekranni oyna "
  'section tab screen page window menu panel '
  'bölüm bölümü bölüme sekme ekranı sayfa sayfayı pencere',
);

final _stopPhrase = _fold('и в во с к на по от для of the and to ve va жана менен');

/// Основы слов «чек / receipt» (единственное число) для «открой чек 1043».
bool _isCheckWord(String w) => const {'чек', 'чека', 'чеку', 'чекти', 'chek', 'chekni', 'fis', 'fisi', 'receipt'}.contains(w);
bool _isLastWord(String w) => const {'последний', 'последнего', 'последнюю', 'акыркы', 'oxirgi', 'sonuncu', 'son', 'last', 'latest', 'недавний'}.contains(foldText(w));
bool _isProductWord(String w) =>
    const {'товар', 'товары', 'товара', 'товару', 'товарды', 'tovar', 'tovarni', 'urun', 'urunu', 'product', 'продукт'}.contains(w);

/// Подсказки глубоких целей: слово в речи → значение параметра экрана (камера: накладная / этикетки / полка; новый товар:
/// одежда / услуга; сканер: цены).
const _hints = <String, List<(String arg, String value, List<String> stems)>>{
  'smartcam': [
    ('tab', 'invoice', ['накладн', 'invoice', 'faktura', 'irsaliye', 'nakladnoy']),
    ('tab', 'labels', ['этикет', 'ценник', 'label', 'etiket', 'yorliq']),
    ('tab', 'shelf', ['полк', 'shelf', 'raf', 'javon', 'текче', 'tekce']),
  ],
  'scan': [
    ('tab', 'price', ['цен', 'price', 'narx', 'fiyat', 'баа', 'baa']),
  ],
  'new_product': [
    ('tab', 'clothes', ['одежд', 'clothes', 'clothing', 'кийим', 'kiyim', 'giyim', 'giysi']),
    ('tab', 'service', ['услуг', 'service', 'xizmat', 'hizmet', 'кызмат', 'kizmat']),
  ],
};

// ---------------------------------------------------------------------------------------------------------------------
// Совпадение слов
// ---------------------------------------------------------------------------------------------------------------------

int _common(String a, String b) {
  final n = a.length < b.length ? a.length : b.length;
  var i = 0;
  while (i < n && a.codeUnitAt(i) == b.codeUnitAt(i)) {
    i++;
  }
  return i;
}

int _lev(String a, String b, int max) {
  if ((a.length - b.length).abs() > max) return max + 1;
  var prev = List<int>.generate(b.length + 1, (i) => i);
  for (var i = 1; i <= a.length; i++) {
    final cur = List<int>.filled(b.length + 1, 0)..[0] = i;
    var rowMin = cur[0];
    for (var j = 1; j <= b.length; j++) {
      final cost = a.codeUnitAt(i - 1) == b.codeUnitAt(j - 1) ? 0 : 1;
      final v = [prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost].reduce((x, y) => x < y ? x : y);
      cur[j] = v;
      if (v < rowMin) rowMin = v;
    }
    if (rowMin > max) return max + 1;
    prev = cur;
  }
  return prev[b.length];
}

/// Насколько слово речи [w] подходит слову-образцу [p]: 6 — то же слово; 5 — образец с окончанием («складе» ~ «склад»);
/// 4 — другая форма («смену» ~ «смены», «приёмки» ~ «приёмка»); 2 — опечатка расшифровки (одна-две буквы); 0 — нет.
/// Короткие образцы (≤ 3 букв) — только целиком.
int wordQuality(String w, String p) {
  if (w == p) return 6;
  if (p.length <= 3 || w.length < 3) return 0;
  final n = _common(w, p);
  if (n >= 3 && n >= p.length - 2 && n >= w.length - 4) return n == p.length ? 5 : 4;
  if (p.length >= 5 && w.length >= p.length - 1) {
    final head = w.length > p.length ? w.substring(0, p.length) : w;
    if (_lev(head, p, p.length >= 9 ? 2 : 1) <= (p.length >= 9 ? 2 : 1)) return 2;
  }
  return 0;
}

class _Hit {
  final ScreenDef def;
  int score = 0;
  bool viaTitle = false;
  final Set<int> explained = {};
  _Hit(this.def);
}

// ---------------------------------------------------------------------------------------------------------------------
// Разбор
// ---------------------------------------------------------------------------------------------------------------------

/// Короткая команда «открой …» → решение. [session] нужна, чтобы при ничьей не предлагать закрытые для роли экраны.
/// Длинные фразы (больше 12 слов) — не команда, их разбирает ИИ.
NavDecision parseNavCommand(String text, {Session? session, ScreenRegistry screens = const ScreenRegistry(), List<ScreenDef>? candidates}) {
  final words = foldTokens(text);
  if (words.isEmpty || words.length > 12) return NavDecision.none;

  // 1. глагол
  var strong = false;
  var soft = false;
  final used = <int>{};
  for (var i = 0; i < words.length; i++) {
    final w = words[i];
    if (_strong.contains(w)) {
      strong = true;
      used.add(i);
    } else if (_soft.contains(w)) {
      soft = true;
      used.add(i);
    } else if (i + 1 < words.length && ((w == 'go' && words[i + 1] == 'to') || (w == 'switch' && words[i + 1] == 'to') || (w == 'take' && words.length > i + 2 && words[i + 1] == 'me' && words[i + 2] == 'to'))) {
      strong = true;
      used.add(i);
    }
  }
  if (!strong && !soft) return NavDecision.none;

  // 2. содержательные слова
  var section = false;
  final content = <String>[];
  final pos = <int>[]; // номер в words
  for (var i = 0; i < words.length; i++) {
    if (used.contains(i)) continue;
    final w = words[i];
    if (_sectionNoun.contains(w)) section = true; // слово остаётся в разборе: «bosh sahifa», «ana sayfa», «башкы бет» — это названия
    if (_filler.contains(w)) continue;
    content.add(w);
    pos.add(i);
  }
  if (content.isEmpty) return strong ? const NavDecision(NavKind.unknown) : NavDecision.none;

  // 3. чек по номеру / последний
  final checkAt = content.indexWhere(_isCheckWord);
  if (checkAt >= 0 && strong) {
    final num = content.firstWhere((w) => RegExp(r'^\d+$').hasMatch(w), orElse: () => '');
    final last = content.any(_isLastWord);
    if (num.isNotEmpty || last) return NavDecision(NavKind.check, checkNumber: num.isEmpty ? null : num, checkLast: num.isEmpty && last);
  }

  // 5. очки экранов
  final pool = candidates ?? screens.all.where((d) => d.exists && d.voiceOpen).toList();
  final hits = <_Hit>[];
  for (final d in pool) {
    final h = _Hit(d);
    for (final ph in d.phrases) {
      final toks = [for (final t in ph.tokens) if (t.length > 1 && !_stopPhrase.contains(t)) t];
      if (toks.isEmpty) continue;
      final taken = <int>{};
      var sum = 0;
      var all = true;
      for (final t in toks) {
        var best = 0;
        var bestI = -1;
        for (var i = 0; i < content.length; i++) {
          if (taken.contains(i)) continue;
          final q = wordQuality(content[i], t);
          if (q > best) {
            best = q;
            bestI = i;
          }
        }
        if (best == 0) {
          all = false;
          break;
        }
        taken.add(bestI);
        sum += best;
      }
      if (!all) continue;
      final sc = (sum + (toks.length - 1) * 4) * ph.weight; // длинная фраза точнее короткой: +4 за каждое слово сверх первого
      h.explained.addAll(taken);
      if (sc > h.score) {
        h.score = sc;
        h.viaTitle = ph.weight == 2;
      } else if (sc == h.score && ph.weight == 2) {
        h.viaTitle = true;
      }
    }
    if (h.score > 0) hits.add(h);
  }
  // родитель и потомок, названные разными словами («на складе приёмку»): родитель — только контекст, открываем потомка
  final context = <int>{};
  hits.removeWhere((h) {
    final isParent = hits.any((o) => o.def.parent == h.def.key && o.score >= 4 && !o.explained.every(h.explained.contains) && !h.explained.every(o.explained.contains));
    if (isParent) context.addAll(h.explained);
    return isParent;
  });
  hits.sort((a, b) => b.score.compareTo(a.score));

  // 4. карточка товара: «открой товар кола» (слово «товар» — единственное, что объяснил экран)
  final productAt = content.indexWhere(_isProductWord);
  if (productAt >= 0 && strong && content.length > 1 && (hits.isEmpty || hits.first.explained.every((i) => i == productAt))) {
    final rest = [for (var i = 0; i < content.length; i++) if (i != productAt && !_ignorable.contains(content[i]) && !_sectionNoun.contains(content[i])) content[i]];
    if (rest.isNotEmpty) return NavDecision(NavKind.product, query: rest.join(' '));
  }
  final matched = {for (final h in hits) h.def.key};

  if (hits.isEmpty) {
    // «Списание» — отдельного экрана нет: списывают в карточке товара
    if (content.any((w) => w.startsWith('списан') || w.startsWith('spisan') || w.startsWith('write') || w.startsWith('hurda') || w.startsWith('hisobdan') || w.startsWith('жокко'))) {
      return const NavDecision(NavKind.hint, hint: 'writeoff');
    }
    if (content.length <= 3 && strong) return const NavDecision(NavKind.unknown);
    return NavDecision.none;
  }

  // 6. кто в лидерах
  final best = hits.first;
  var group = [for (final h in hits) if (h.score >= 4 && h.score * 10 >= best.score * 9) h]; // ничья — второй не слабее 9/10 первого
  if (group.isEmpty) group = [best];
  // родитель и потомок — не ничья («на складе приёмка»): берём потомка
  group = [
    for (final h in group)
      if (!group.any((o) => o.def.parent == h.def.key)) h,
  ];
  // закрытые для роли экраны в вариантах не показываем, если есть доступные
  if (session != null && group.length > 1) {
    final allowed = [for (final h in group) if (h.def.allowedFor(session)) h];
    if (allowed.isNotEmpty) group = allowed;
  }
  if (group.length > 4) group = group.sublist(0, 4);
  if (group.length > 1) {
    // мягкий глагол и ничья — не наше дело, это скорее вопрос
    if (soft && !strong && !section) return NavDecision(NavKind.none, matchedKeys: matched);
    return NavDecision(NavKind.choose, options: [for (final h in group) h.def], matchedKeys: matched);
  }

  final pick = group.first;
  if (soft && !strong && !section && !pick.viaTitle) return NavDecision(NavKind.none, matchedKeys: matched);
  if (best.score < 4) {
    // единственная догадка по опечатке — переспросить кнопкой
    return NavDecision(NavKind.choose, options: [pick.def], matchedKeys: matched);
  }

  // 7. параметры экрана и остаток
  final d = pick.def;
  final args = <String, String>{};
  final eaten = <int>{...pick.explained, ...context};
  for (final p in d.params) {
    if (p.arg == 'filter') {
      final v = stockFilterFromWords(content);
      if (v != null) args['filter'] = v;
    } else if (p.arg == 'period') {
      final v = periodFromWords(content);
      if (v != null) args['period'] = v;
    }
  }
  // слова фильтра и периода — тоже объяснены
  if (args.containsKey('filter') || args.containsKey('period')) {
    for (var i = 0; i < content.length; i++) {
      if (stockFilterFromWords([content[i]]) != null || periodFromWords([content[i]]) != null) eaten.add(i);
    }
  }
  for (final hint in _hints[d.key] ?? const <(String, String, List<String>)>[]) {
    for (var i = 0; i < content.length; i++) {
      if (hint.$3.any(content[i].startsWith) && !args.containsKey(hint.$1)) {
        args[hint.$1] = hint.$2;
        eaten.add(i);
      }
    }
  }
  final rest = [for (var i = 0; i < content.length; i++) if (!eaten.contains(i) && !_ignorable.contains(content[i]) && !_sectionNoun.contains(content[i])) content[i]];
  String? query;
  if (rest.isNotEmpty) {
    final hasQuery = d.params.any((p) => p.arg == 'query');
    if (!hasQuery || rest.length > 3) return NavDecision(NavKind.none, matchedKeys: matched); // не короткая команда — решает ИИ
    query = rest.join(' ');
  }
  final margin = hits.length < 2 || hits[1].score * 10 < best.score * 9;
  return NavDecision(
    NavKind.open,
    def: d,
    args: {...args, 'query': ?query},
    confident: strong && margin && best.score >= 4 && rest.isEmpty,
    matchedKeys: matched,
  );
}
