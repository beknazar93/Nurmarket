// 2026-10-26 (голос-управление, В1): инструменты навигации звонка Нур Советника — open_screen, open_product, open_check,
// open_client, open_order, go_back, go_home, list_screens (группа «Н», раздел 3.3 ТЗ). Слов-просьбы не требуют: навигация
// ничего не меняет (ворота намерения — только у действий). Право — как у самого экрана (Session.canRoute): недоступные
// инструменты не объявляются (AssistantTool.allowed), а недоступный экран внутри open_screen отвечает denied и перехода нет.
//
// Сам переход делает [NavHost] — маленький интерфейс, который в приложении реализует оболочка над go_router (пакет В4),
// а в тестах — фейк. Реестр экранов, синонимы и разбор слов — screen_registry.dart. Подключение: одной строкой
// [registerNavTools] поверх общего ToolRegistry (tool_registry.dart не правится).
import 'dart:async';

import '../../api/session_store.dart';
import '../live_tools.dart';
import 'screen_registry.dart';
import 'tool_registry.dart';

/// Товар для навигации: ровно то, что нужно, чтобы показать варианты и открыть карточку (хост хранит настоящий Product).
class NavProduct {
  final String id;
  final String name;
  final String? barcode;
  final double price;
  const NavProduct(this.id, this.name, {this.barcode, this.price = 0});
}

/// Окружение навигации. В приложении — оболочка над go_router и базой каталога (В4), в тестах — фейк.
///
/// Важно для реализации: [openRoute] должен вернуться, как только переход начат, а не когда экран закроют (go_router `push`
/// завершается при возврате назад). Если хост всё же ждёт дольше [hostTimeout], инструмент считает переход начатым.
abstract class NavHost {
  /// Текущий адрес вместе с параметрами («/stock?filter=low»); пусто — неизвестен.
  String get location;
  bool get canGoBack;

  /// Открыть маршрут поверх текущего (push). Адрес уже проверен по праву роли и собран программой.
  Future<void> openRoute(String location);

  /// Экран вне маршрутов: 'printer', 'ai_keys'.
  Future<void> openAux(String key);
  Future<void> goHome();
  Future<void> goBack();

  /// Поиск в каталоге телефона: по штрихкоду — точный (в приложении CodeLookup.find), по названию — searchProducts.
  Future<List<NavProduct>> findProducts({String? query, String? barcode});

  /// Открыть карточку товара (openProductEditor).
  Future<void> openProduct(NavProduct p);

  /// Найти чек (по номеру или последний) и открыть его (/checks/detail с CheckRow). false — не нашёлся.
  Future<bool> openCheck({String? number, bool last = false});
}

// ---------------------------------------------------------------------------------------------------------------------
// Сообщения (ru, ky, uz, en, tr) — как toolMsg в tool_registry.dart; свои, чтобы не править общий файл.
// ---------------------------------------------------------------------------------------------------------------------

const _m = <String, List<String>>{
  'opened': ['Открыла: {title}{detail}.', 'Ачтым: {title}{detail}.', 'Ochdim: {title}{detail}.', 'Opened: {title}{detail}.', 'Açtım: {title}{detail}.'],
  'here': ['Уже открыт раздел «{title}».', '«{title}» бөлүмү мурунтан эле ачык.', '«{title}» bo\'limi allaqachon ochiq.', '"{title}" is already open.', '"{title}" zaten açık.'],
  'noParam': [
    ' Запрос на этом экране пока не подставляется — введите его на экране.',
    ' Суроо бул экранга азырынча коюлбайт — экрандан өзүңүз киргизиңиз.',
    ' So\'rov bu ekranga hozircha qo\'yilmaydi — ekranda o\'zingiz kiriting.',
    ' The request is not filled in on this screen yet — enter it on the screen.',
    ' İstek bu ekrana henüz yazılmıyor — ekranda kendiniz girin.',
  ],
  'denied': [
    'У вас нет доступа к разделу «{title}».',
    '«{title}» бөлүмүнө кирүүгө укугуңуз жок.',
    '«{title}» bo\'limiga kirish huquqingiz yo\'q.',
    'You have no access to "{title}".',
    '"{title}" bölümüne erişiminiz yok.',
  ],
  'noVoice': [
    'Раздел «{title}» голосом не открывается.',
    '«{title}» бөлүмү үн менен ачылбайт.',
    '«{title}» bo\'limi ovoz bilan ochilmaydi.',
    '"{title}" cannot be opened by voice.',
    '"{title}" sesle açılamaz.',
  ],
  'unknownScreen': [
    'Не нашла такой раздел. Скажите, например: склад, продажа, чеки.',
    'Мындай бөлүм табылган жок. Мисалы: кампа, сатуу, чектер.',
    'Bunday bo\'lim topilmadi. Masalan: ombor, sotuv, cheklar.',
    'No such section. Say, for example: stock, sale, receipts.',
    'Böyle bir bölüm bulunamadı. Örneğin: depo, satış, fişler.',
  ],
  'noCashbox': ['Сначала выберите кассу.', 'Адегенде кассаны тандаңыз.', 'Avval kassani tanlang.', 'Choose a cash register first.', 'Önce kasayı seçin.'],
  'back': ['Вернулась назад.', 'Артка кайттым.', 'Orqaga qaytdim.', 'Went back.', 'Geri döndüm.'],
  'noBack': ['Назад некуда.', 'Артка кайтуучу жер жок.', 'Orqaga qaytadigan joy yo\'q.', 'There is nowhere to go back to.', 'Geri dönülecek yer yok.'],
  'home': ['Открыла главную.', 'Башкы бетти ачтым.', 'Bosh sahifani ochdim.', 'Opened the home screen.', 'Ana sayfayı açtım.'],
  'list': ['Могу открыть: {list}.', 'Ачууга болот: {list}.', 'Ocha olaman: {list}.', 'I can open: {list}.', 'Açabileceklerim: {list}.'],
  'listMore': [', и другие', ', жана башкалар', ', va boshqalar', ', and more', ' ve diğerleri'],
  'needProduct': [
    'Скажите название товара или штрихкод.',
    'Товардын атын же штрихкодун айтыңыз.',
    'Tovar nomini yoki shtrixkodini ayting.',
    'Tell me the product name or barcode.',
    'Ürün adını veya barkodu söyleyin.',
  ],
  'productOpened': ['Открыла карточку товара.', 'Товардын картасын ачтым.', 'Tovar kartasini ochdim.', 'Opened the product card.', 'Ürün kartını açtım.'],
  'productNone': ['Такой товар не найден.', 'Мындай товар табылган жок.', 'Bunday tovar topilmadi.', 'No such product found.', 'Böyle bir ürün bulunamadı.'],
  'productMany': [
    'Нашла несколько товаров ({n}). Какой открыть — скажите номер.',
    'Бир нече товар табылды ({n}). Кайсынысын ачайын — номерин айтыңыз.',
    'Bir nechta tovar topildi ({n}). Qaysi birini ochay — raqamini ayting.',
    'Found several products ({n}). Which one to open — say the number.',
    'Birkaç ürün buldum ({n}). Hangisini açayım — numarasını söyleyin.',
  ],
  'needCheck': [
    'Скажите номер чека или «последний».',
    'Чектин номерин же «акыркы» деп айтыңыз.',
    'Chek raqamini yoki «oxirgi» deb ayting.',
    'Tell me the receipt number or "the last one".',
    'Fiş numarasını veya "sonuncu" deyin.',
  ],
  'checkOpened': ['Открыла чек.', 'Чекти ачтым.', 'Chekni ochdim.', 'Opened the receipt.', 'Fişi açtım.'],
  'checkNone': ['Такой чек не найден.', 'Мындай чек табылган жок.', 'Bunday chek topilmadi.', 'No such receipt found.', 'Böyle bir fiş bulunamadı.'],
  'f.low': ['мало осталось', 'аз калган', 'kam qolgan', 'running low', 'azalanlar'],
  'f.out': ['нет в наличии', 'жок', 'qolmagan', 'out of stock', 'tükenenler'],
  'f.in': ['есть в наличии', 'бар', 'mavjud', 'in stock', 'stokta olanlar'],
  'f.all': ['все', 'баары', 'hammasi', 'all', 'hepsi'],
  'p.today': ['сегодня', 'бүгүн', 'bugun', 'today', 'bugün'],
  'p.week': ['неделя', 'жума', 'hafta', 'week', 'hafta'],
  'p.month': ['месяц', 'ай', 'oy', 'month', 'ay'],
};

String navMsg(String key, String lang, [Map<String, String> params = const {}]) {
  final l = _m[key];
  if (l == null) return key;
  final i = kNavLangs.indexOf(lang);
  var s = l[i < 0 ? 0 : i];
  params.forEach((k, v) => s = s.replaceAll('{$k}', v));
  return s;
}

// ---------------------------------------------------------------------------------------------------------------------
// Инструменты
// ---------------------------------------------------------------------------------------------------------------------

/// Подключить инструменты навигации к общему реестру (единственная точка соединения с tool_registry.dart).
void registerNavTools(ToolRegistry reg, NavHost host, {ScreenRegistry screens = const ScreenRegistry(), Duration hostTimeout = const Duration(seconds: 2)}) {
  for (final t in navTools(host, screens: screens, hostTimeout: hostTimeout)) {
    reg.register(t);
  }
}

List<AssistantTool> navTools(NavHost host, {ScreenRegistry screens = const ScreenRegistry(), Duration hostTimeout = const Duration(seconds: 2)}) =>
    _NavTools(host, screens, hostTimeout).build();

class _NavTools {
  final NavHost host;
  final ScreenRegistry screens;
  final Duration hostTimeout;
  _NavTools(this.host, this.screens, this.hostTimeout);

  static bool _canClients(Session s) => s.canRoute('/admin/clients');
  static bool _canOrders(Session s) => s.canRoute('/admin/orders');
  static bool _canProduct(Session s) => s.canRoute('/admin/product');
  static bool _canChecks(Session s) => s.canRoute('/checks');

  List<AssistantTool> build() {
    final keys = [
      for (final d in screens.all)
        if (d.exists && d.voiceOpen) d.key,
    ];
    return [
      AssistantTool(
        group: ToolGroup.nav,
        decl: ToolDecl(
          'open_screen',
          'Navigation only, changes nothing. Open an app screen by key when the user asks to open/show a section '
          '(ru: открой склад, ky: кампаны ач, uz: ombor och, en: open stock, tr: depoyu aç). '
          'filter: stock list low|out|in|all. query: search text for search/clients/orders/receipts. '
          'period: summary today|week|month. tab: scan sale|price; new_product product|clothes|service. '
          'Never use it to pay or to change data.',
          parameters: sObj({
            'screen': sStr(desc: 'Screen key', values: keys),
            'filter': sStr(desc: 'Stock filter: low, out, in or all'),
            'query': sStr(desc: 'Text to search for'),
            'period': sStr(desc: 'today, week or month', values: ['today', 'week', 'month']),
            'tab': sStr(desc: 'Sub-mode of the screen'),
          }, required: ['screen']),
        ),
        run: _openScreen,
      ),
      AssistantTool(
        group: ToolGroup.nav,
        allowed: _canProduct,
        needs: 'Склад',
        decl: ToolDecl(
          'open_product',
          'Navigation only. Open a product card by name or barcode (ru: открой товар Кола, ky: Кола товарын ач, uz: Kola tovarini och, '
          'en: open product Cola, tr: Kola ürününü aç). If several products match the result is "ambiguous": ask which one and call again with pick.',
          parameters: sObj({
            'query': sStr(desc: 'Product name as the user said it'),
            'barcode': sStr(desc: 'Barcode digits'),
            'pick': sInt(desc: 'Number of the variant the user chose (1-based)'),
          }),
        ),
        run: _openProduct,
      ),
      AssistantTool(
        group: ToolGroup.nav,
        allowed: _canChecks,
        decl: ToolDecl(
          'open_check',
          'Navigation only. Open a receipt by number, or the last one (ru: открой чек 1043, ky: 1043 чекти ач, uz: 1043 chekni och, en: open receipt 1043, tr: 1043 fişi aç).',
          parameters: sObj({
            'number': sStr(desc: 'Receipt number'),
            'last': sBool(desc: 'true for the most recent receipt'),
          }),
        ),
        run: _openCheck,
      ),
      AssistantTool(
        group: ToolGroup.nav,
        allowed: _canClients,
        needs: 'Клиенты',
        decl: ToolDecl(
          'open_client',
          'Navigation only. Open the clients screen (ru: открой клиента Айбека, ky: Айбек кардарын ач, uz: Aybek mijozini och, en: open client Aibek, tr: müşteri Aybek\'i aç).',
          parameters: sObj({'query': sStr(desc: 'Client name or phone as the user said it')}),
        ),
        run: (r) => _openDef(screens.byKey('clients')!, {'query': r.args['query']}, r),
      ),
      AssistantTool(
        group: ToolGroup.nav,
        allowed: _canOrders,
        needs: 'Заказы',
        decl: ToolDecl(
          'open_order',
          'Navigation only. Open the orders screen (ru: открой заказ 25, ky: 25 заказды ач, uz: 25 buyurtmani och, en: open order 25, tr: 25 siparişi aç).',
          parameters: sObj({'number': sStr(desc: 'Order number')}),
        ),
        run: (r) => _openDef(screens.byKey('orders')!, {'query': r.args['number']}, r),
      ),
      AssistantTool(
        group: ToolGroup.nav,
        decl: const ToolDecl('go_back', 'Navigation only. Go to the previous screen (ru: назад, ky: артка, uz: orqaga, en: go back, tr: geri).'),
        run: (r) async {
          if (!host.canGoBack) return ToolResult(ToolStatus.notFound, navMsg('noBack', r.env.lang), screen: host.location);
          await _callHost(host.goBack);
          return ToolResult(ToolStatus.ok, navMsg('back', r.env.lang), screen: host.location);
        },
      ),
      AssistantTool(
        group: ToolGroup.nav,
        decl: const ToolDecl('go_home', 'Navigation only. Go to the home screen (ru: домой, на главную, ky: башкы бетке, uz: bosh sahifaga, en: go home, tr: ana sayfaya).'),
        run: (r) async {
          await _callHost(host.goHome);
          return ToolResult(ToolStatus.ok, navMsg('home', r.env.lang), screen: '/home');
        },
      ),
      AssistantTool(
        group: ToolGroup.nav,
        decl: ToolDecl(
          'list_screens',
          'Read-only. List the sections this user may open. Call when the user asks what you can open or does not know the name of a section.',
          parameters: sObj({'query': sStr(desc: 'Optional words to narrow the list')}),
        ),
        run: _listScreens,
      ),
    ];
  }

  Future<void> _callHost(Future<void> Function() f) async {
    // Переход начат — этого достаточно: go_router push завершается только при возврате назад.
    await f().timeout(hostTimeout, onTimeout: () {});
  }

  // ------------------------------------------------------------------------------------------------ open_screen

  Future<ToolResult> _openScreen(ToolRequest r) async {
    final lang = r.env.lang;
    final raw = '${r.args['screen'] ?? ''}'.trim();
    final def = raw.isEmpty ? null : screens.resolve(raw);
    if (def == null) return ToolResult(ToolStatus.notFound, navMsg('unknownScreen', lang));
    return _openDef(def, {for (final k in const ['filter', 'query', 'period', 'tab']) k: r.args[k]}, r);
  }

  /// Общий путь открытия экрана: право → параметры → адрес → переход через хост.
  Future<ToolResult> _openDef(ScreenDef def, Map<String, Object?> rawArgs, ToolRequest r) async {
    final lang = r.env.lang;
    final s = r.env.session;
    final title = def.title(lang);
    if (!def.exists || !def.voiceOpen) {
      return ToolResult(ToolStatus.denied, navMsg('noVoice', lang, {'title': title}));
    }
    // Право проверяется при каждом вызове ещё раз (сессия могла смениться); go_router redirect → /denied — последняя линия.
    if (!def.allowedFor(s)) {
      return ToolResult(ToolStatus.denied, navMsg('denied', lang, {'title': title}));
    }
    if (def.route != null && s.cashboxId == null) {
      return ToolResult(ToolStatus.error, navMsg('noCashbox', lang));
    }

    // Параметры: допустимые уходят в адрес, неизвестные значения отбрасываются, а не ломают вызов.
    final query = <String, String>{};
    final applied = <String, String>{};
    var notLive = false;
    for (final p in def.params) {
      final v = normalizeNavArg(p, rawArgs[p.arg]);
      if (v == null) continue;
      query[p.url] = v;
      if (p.live) {
        applied[p.arg] = v;
      } else {
        notLive = true;
      }
    }

    final String where;
    if (def.route != null) {
      where = Uri(path: def.route, queryParameters: query.isEmpty ? null : query).toString();
      if (host.location == where) {
        return ToolResult(ToolStatus.ok, navMsg('here', lang, {'title': title}), screen: where, data: {'screen': def.key, 'already': true});
      }
      await _callHost(() => host.openRoute(where));
    } else {
      where = def.aux!;
      await _callHost(() => host.openAux(def.aux!));
    }

    final details = <String>[
      if (applied['filter'] != null) navMsg('f.${applied['filter']}', lang),
      if (applied['period'] != null) navMsg('p.${applied['period']}', lang),
    ];
    final detail = details.isEmpty ? '' : ' (${details.join(', ')})';
    final say = navMsg('opened', lang, {'title': title, 'detail': detail}) + (notLive ? navMsg('noParam', lang) : '');
    return ToolResult(
      ToolStatus.ok,
      say,
      screen: where,
      data: {'screen': def.key, 'route': where, if (applied.isNotEmpty) 'applied': applied},
    );
  }

  // ------------------------------------------------------------------------------------------------ open_product

  static int? _pickOf(Object? v) {
    if (v is num) return v.toInt();
    if (v is String) return int.tryParse(v.trim());
    return null;
  }

  static String _fmtPrice(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);

  Future<ToolResult> _openProduct(ToolRequest r) async {
    final lang = r.env.lang;
    var barcode = '${r.args['barcode'] ?? ''}'.trim();
    var query = '${r.args['query'] ?? ''}'.trim();
    if (barcode.isNotEmpty) barcode = barcodeFromText(barcode) ?? barcode.replaceAll(RegExp(r'\s+'), '');
    if (barcode.isEmpty && query.isNotEmpty) {
      final b = barcodeFromText(query);
      if (b != null) {
        barcode = b;
        query = '';
      }
    }
    if (barcode.isEmpty && query.isEmpty) return ToolResult(ToolStatus.clarify, navMsg('needProduct', lang));

    List<NavProduct> hits;
    if (barcode.isNotEmpty) {
      hits = await host.findProducts(barcode: barcode);
    } else {
      final raw = await host.findProducts(query: query);
      // Слова запроса — начала слов названия («нан» не находит «Финансы»); если так не осталось ничего (поиск мог сработать
      // по артикулу или коду) — оставляем то, что нашла база.
      final strict = raw.where((p) => productNameMatchesQuery(p.name, query)).toList();
      hits = strict.isEmpty ? raw : strict;
    }
    // Без дублей и в стабильном порядке (по названию), чтобы «вторая» означала один и тот же товар между вызовами.
    final seen = <String>{};
    hits = [
      for (final p in hits)
        if (seen.add(p.id)) p,
    ]..sort((a, b) {
        final c = foldText(a.name).compareTo(foldText(b.name));
        return c != 0 ? c : a.id.compareTo(b.id);
      });
    if (hits.isEmpty) return ToolResult(ToolStatus.notFound, navMsg('productNone', lang));

    NavProduct? chosen;
    if (hits.length == 1) {
      chosen = hits.first;
    } else {
      final pick = _pickOf(r.args['pick']);
      if (pick != null && pick >= 1 && pick <= hits.length) {
        chosen = hits[pick - 1];
      } else if (query.isNotEmpty) {
        // Название сказано целиком и совпало ровно с одним товаром — вопрос не нужен.
        final q = foldTokens(query).join(' ');
        final exact = hits.where((p) => foldTokens(p.name).join(' ') == q).toList();
        if (exact.length == 1) chosen = exact.first;
      }
    }
    if (chosen == null) {
      final shown = hits.take(5).toList();
      return ToolResult(
        ToolStatus.ambiguous,
        navMsg('productMany', lang, {'n': '${hits.length}'}),
        options: [
          for (var i = 0; i < shown.length; i++) {'n': i + 1, 'title': shown[i].name, 'price': shown[i].price},
        ],
        data: {'total': hits.length},
      );
    }
    final c = chosen;
    await _callHost(() => host.openProduct(c));
    return ToolResult(
      ToolStatus.ok,
      navMsg('productOpened', lang),
      screen: 'product',
      data: {'id': c.id, 'name': c.name, 'price': _fmtPrice(c.price), 'barcode': ?c.barcode},
    );
  }

  // ------------------------------------------------------------------------------------------------ open_check

  Future<ToolResult> _openCheck(ToolRequest r) async {
    final lang = r.env.lang;
    final number = '${r.args['number'] ?? ''}'.trim().replaceAll(RegExp(r'^[#№]+'), '');
    final last = r.args['last'] == true;
    if (number.isEmpty && !last) return ToolResult(ToolStatus.clarify, navMsg('needCheck', lang));
    final ok = await host.openCheck(number: number.isEmpty ? null : number, last: number.isEmpty && last);
    if (!ok) return ToolResult(ToolStatus.notFound, navMsg('checkNone', lang));
    return ToolResult(ToolStatus.ok, navMsg('checkOpened', lang), screen: '/checks/detail');
  }

  // ------------------------------------------------------------------------------------------------ list_screens

  Future<ToolResult> _listScreens(ToolRequest r) async {
    final lang = r.env.lang;
    var list = screens.openable(r.env.session);
    final q = '${r.args['query'] ?? ''}'.trim();
    if (q.isNotEmpty) {
      final keys = screens.find(q).map((m) => m.def.key).toSet();
      list = [
        for (final d in list)
          if (keys.contains(d.key)) d,
      ];
    }
    final titles = [for (final d in list) d.title(lang)];
    const sayMax = 12;
    final head = titles.take(sayMax).join(', ');
    final say = navMsg('list', lang, {'list': head + (titles.length > sayMax ? navMsg('listMore', lang) : '')});
    return ToolResult(
      ToolStatus.ok,
      say,
      data: {
        'screens': [for (final d in list) '${d.key}:${d.title(lang)}'],
      },
    );
  }
}
