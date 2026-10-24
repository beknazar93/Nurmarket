import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/vision.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';

import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'advisor_call.dart';
import 'analytics.dart';
import '../../core/util/audit.dart';

/// 13.10, владелец: «ИИ-ассистента, то есть Нур Советника, тоже в мобилку» (доска Т23). Чат с ИИ (тот же ключ Gemini, что
/// у советника на кассе); перед первым вопросом программа собирает сводку магазина — выручка и чеки сегодня, вчера, за
/// неделю, лучшие товары недели, что заканчивается, кто должен — и передаёт её ИИ: он отвечает цифрами магазина, а не
/// догадками. Ничего не меняет в магазине — только отвечает.
class AdvisorScreen extends ConsumerStatefulWidget {
  const AdvisorScreen({super.key});
  @override
  ConsumerState<AdvisorScreen> createState() => _AdvisorScreenState();
}

/// 2026-10-22 (советник мобилки): реплика чата. [raw] — текст для ИИ (с кодами [Д1]), [text] — для экрана (с именами);
/// ошибки ([error]) в историю для ИИ не уходят, у них кнопка «Повторить».
class _Msg {
  final bool user;
  final String text;
  final String raw;
  final bool error;
  final bool noKey;
  const _Msg(this.user, this.text, {String? raw, this.error = false, this.noKey = false}) : raw = raw ?? text;
}

/// 2026-10-22 (советник мобилки): понятный текст ошибки вместо тишины — нет ключа → куда его добавить, иначе причина.
String advisorErrorText(AppLocalizations l, Object e) => switch (e) {
  VisionException(noKey: true) => l.advisorNoKey,
  VisionException(:final message) => l.advisorError(message),
  ApiException(:final message) => l.advisorError(message),
  _ => l.advisorError('$e'),
};

/// 2026-10-22 (советник мобилки): коды должников [Д1] в ответе ИИ → настоящие имя и телефон (только на этом телефоне),
/// как OwnerAiContext.RevealCodes на ПК; только со скобками — «витамин Д3» не трогается.
String revealDebtorCodes(String answer, Map<int, String> names) => names.isEmpty
    ? answer
    : answer.replaceAllMapped(RegExp(r'\[Д(\d{1,3})\]'), (m) => names[int.tryParse(m.group(1)!)] ?? m.group(0)!);

class _AdvisorScreenState extends ConsumerState<AdvisorScreen> {
  final List<_Msg> _messages = [];
  final _input = TextEditingController();
  final _scroll = ScrollController();
  String? _context;
  DateTime _contextAt = DateTime(2000);
  bool _busy = false;
  // 2026-10-22 (советник мобилки): этап ожидания — «Собираю данные…» → «Спрашиваю ИИ…».
  String? _stage;
  Map<int, String> _debtorNames = const {};

  @override
  void dispose() {
    _input.dispose();
    _scroll.dispose();
    super.dispose();
  }

  Future<String> _storeContext() async {
    if (_context != null && DateTime.now().difference(_contextAt) < const Duration(minutes: 5)) return _context!;
    final repo = ref.read(analyticsRepoProvider);
    // 2026-10-22 (аудит ПС, права как на ПК — AiAdvisorWindow О4): должники — только с правом «Клиенты», склад — с правом «Склад».
    final session = ref.read(sessionProvider);
    final canClients = session?.can(Session.viewClients) ?? false;
    final canProducts = session?.can(Session.viewProducts) ?? false;
    final db = ref.read(databaseProvider);
    final today = DateUtils.dateOnly(DateTime.now());
    String fmt(SalesSummary s) => 'выручка ${money(s.revenue)} сом, чеков ${s.checks}, средний чек ${money(s.avg)}, валовая прибыль ${money(s.profit)}';
    String pct(double cur, double prev) => prev > 0.005 ? '${cur >= prev ? '+' : ''}${((cur - prev) / prev * 100).toStringAsFixed(0)} %' : 'нет сравнения';
    // 2026-10-22 (советник мобилки): все части — одновременно (как OwnerPeriodStats на ПК), у каждой свой срок 15 с;
    // не ответившая часть помечается «не загрузилось», остальные идут в ИИ. Было: по очереди, до 40 с на каждую.
    Future<String> part(String title, Future<String> Function() load) async {
      try {
        return await load().timeout(const Duration(seconds: 15));
      } catch (_) {
        return '$title: сервер не ответил — эти цифры не называй, предложи раздел «Сводка».';
      }
    }

    final yesterday = today.subtract(const Duration(days: 1));
    final dayBefore = today.subtract(const Duration(days: 2));
    final weekFrom = today.subtract(const Duration(days: 6));
    final monthFrom = DateTime(today.year, today.month, 1);
    final names = <int, String>{};
    final parts = await Future.wait([
      // Цифры — из того же отчёта сервера, что «Сводка» (api/main/analytics/market, tab=sales), как у советника на ПК.
      part('Сегодня', () async => 'Сегодня ${_d(today)} (день не закончен): ${fmt(await repo.sales(today, today))}'),
      part('Вчера', () async {
        final (y, b) = await (repo.sales(yesterday, yesterday), repo.sales(dayBefore, dayBefore)).wait;
        return 'Вчера ${_d(yesterday)}: ${fmt(y)}\nПозавчера ${_d(dayBefore)}: ${fmt(b)}\n'
            'Вчера к позавчера (посчитала программа, не пересчитывай): выручка ${pct(y.revenue, b.revenue)}, прибыль ${pct(y.profit, b.profit)}';
      }),
      part('Неделя', () async {
        final w = await repo.sales(weekFrom, today);
        final sb = StringBuffer('7 дней ${_d(weekFrom)}–${_d(today)} (как в «Сводке», с сегодняшним днём): ${fmt(w)}');
        if (w.byDay.isNotEmpty) sb.write('\nВыручка по дням: ${w.byDay.map((d) => '${_d(d.$1)} — ${money(d.$2)}').join('; ')}');
        if (w.payments.isNotEmpty) sb.write('\nОплаты за 7 дней: ${w.payments.entries.map((e) => '${e.key} ${money(e.value)}').join('; ')}');
        return sb.toString();
      }),
      part('Месяц', () async => 'Месяц ${_d(monthFrom)}–${_d(today)}: ${fmt(await repo.sales(monthFrom, today))}'),
      part('Товары', () async {
        final top = (await repo.products(weekFrom, today))..sort((a, b) => b.revenue.compareTo(a.revenue));
        return 'Лучшие товары за 7 дней (название — штук — выручка): ${top.take(15).map((p) => '${p.name} — ${qtyText(p.qty)} — ${money(p.revenue)}').join('; ')}';
      }),
      if (canProducts)
        part('Остатки', () async {
          // 2026-10-22 (аудит С-5): «что заканчивается» как на ПК (OwnerAiContext): по продажам 30 дней — закончились, хотя
          // продавались; запаса меньше чем на 7 дней; «заказать» — на 14 дней продаж. Плюс остаток ≤ 10 (было ≤ 3).
          final (catalog, sold) = await (db.searchProducts('', limit: 20000), repo.products(today.subtract(const Duration(days: 29)), today)).wait;
          final goods = catalog.where((p) => !p.isService).toList();
          final byName = {for (final p in goods) p.name.trim().toLowerCase(): p};
          final soldOut = <String>[];
          final runningLow = <String>[];
          for (final s in sold..sort((a, b) => b.qty.compareTo(a.qty))) {
            final p = byName[s.name.trim().toLowerCase()];
            if (p == null || s.qty <= 0) continue;
            final perDay = s.qty / 30;
            final order = (perDay * 14 - (p.quantity > 0 ? p.quantity : 0)).ceil();
            if (p.quantity <= 0) {
              soldOut.add('${p.name} — продано ${qtyText(s.qty)} за 30 дн. (≈${perDay.toStringAsFixed(1)} в день), заказать ≈$order');
            } else if (p.quantity / perDay < 7) {
              runningLow.add('${p.name} — остаток ${qtyText(p.quantity)} ${p.unit}, хватит ≈${(p.quantity / perDay).toStringAsFixed(1)} дн., заказать ≈$order');
            }
          }
          final low = goods.where((p) => p.quantity <= 10).toList()..sort((a, b) => a.quantity.compareTo(b.quantity));
          return 'ЧТО ЗАКАНЧИВАЕТСЯ (продажи 30 дней — отчёт сервера, остаток — склад):\n'
              '• Продавались, но закончились (${soldOut.length}): ${soldOut.isEmpty ? 'нет' : soldOut.take(30).join('; ')}\n'
              '• Осталось меньше чем на 7 дней (${runningLow.length}): ${runningLow.isEmpty ? 'нет' : runningLow.take(30).join('; ')}\n'
              '• Остаток 10 и меньше (${low.length}): ${low.take(40).map((p) => '${p.name} — ${qtyText(p.quantity)} ${p.unit}').join('; ')}';
        }),
      if (canClients)
        part('Долги', () async {
          final debtors = (await ref.read(opsRepoProvider).debtors())..sort((a, b) => b.debt.compareTo(a.debt));
          if (debtors.isEmpty) return 'ДОЛЖНИКИ: непогашенных долгов нет.';
          // 2026-10-22 (советник мобилки): приватность как на ПК — имена и телефоны клиентов в Google не уходят, только коды [Д1]…;
          // имя и телефон подставляет телефон в ответе (revealDebtorCodes).
          final sb = StringBuffer(
            'ДОЛЖНИКИ (${debtors.length}, всего ${money(debtors.fold(0.0, (s, d) => s + d.debt))} сом; имена скрыты кодами — пиши код в квадратных скобках, программа сама покажет имя и телефон):',
          );
          for (var i = 0; i < debtors.length && i < 30; i++) {
            final d = debtors[i];
            names[i + 1] = d.name.trim().isEmpty ? '[Д${i + 1}]' : '${d.name.trim()}${d.phone.trim().isEmpty ? '' : ' (тел. ${d.phone.trim()})'}';
            sb.write('\n• [Д${i + 1}] — ${money(d.debt)} сом, чеков в долг: ${d.salesCount}');
          }
          if (debtors.length > 30) sb.write('\n(показаны 30 из ${debtors.length} — остальные в разделе «Долги клиентов»)');
          return sb.toString();
        }),
    ]);
    _debtorNames = names;
    _contextAt = DateTime.now();
    return _context = [
      ...parts,
      if (!canClients) 'ДОЛЖНИКИ: у этого пользователя нет права «Клиенты» — список должников не показывай.',
      if (!canProducts) 'СКЛАД: у этого пользователя нет права «Склад» — остатки не показывай.',
    ].join('\n');
  }

  static String _d(DateTime d) => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}';

  Future<void> _send([String? preset]) async {
    final text = (preset ?? _input.text).trim();
    if (text.isEmpty || _busy) return;
    setState(() {
      _messages.add(_Msg(true, text));
      _input.clear();
    });
    await _answer();
  }

  /// 2026-10-22 (советник мобилки): ответ на последний вопрос; «Повторить» у ошибки зовёт его снова. Любая ошибка — текстом
  /// в чате (было: ловилась только VisionException, остальное пропадало молча — «ничего не происходит»).
  Future<void> _answer() async {
    final l = context.l;
    setState(() {
      _messages.removeWhere((m) => m.error);
      _busy = true;
      _stage = l.advisorStageData;
    });
    _toEnd();
    try {
      final vision = ref.read(visionProvider);
      // 14.10: ключ Groq или OpenRouter тоже подходит. 2026-10-22: нет ни одного — понятное сообщение, где добавить ключ.
      if (!await vision.hasChatKey()) throw const VisionException('no key', noKey: true);
      final shop = ref.read(sessionProvider)?.companyName ?? '';
      final data = await _storeContext();
      if (mounted) setState(() => _stage = l.advisorStageAsk);
      final system = _system(shop, data);
      final history = [for (final m in _messages) if (!m.error) (user: m.user, text: m.raw)];
      final answer = await vision.chat(system, history);
      if (mounted) setState(() => _messages.add(_Msg(false, revealDebtorCodes(answer, _debtorNames), raw: answer)));
    } catch (e) {
      if (mounted) {
        setState(() => _messages.add(_Msg(false, advisorErrorText(l, e), error: true, noKey: e is VisionException && e.noKey)));
      }
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _stage = null;
        });
      }
      _toEnd();
    }
  }

  String _system(String shop, String data) =>
      'Ты — Нур Советник, помощник владельца магазина «$shop» в Кыргызстане. Отвечай на языке вопроса, коротко и по делу, '
      'с цифрами из данных магазина ниже — называй их как есть, не пересчитывай. Таблицы делай простым списком строк «название — число». '
      'Если данных нет — так и скажи, не выдумывай. Суммы — в сомах. Советуй конкретно: что заказать, кому напомнить о долге, что продаётся лучше. '
      'Должников называй только кодами в квадратных скобках, например [Д1].\n\nДАННЫЕ МАГАЗИНА на ${_d(DateTime.now())}:\n$data';

  /// 2026-10-22 (звонок ИИ мобилки): инструкция голосового звонка — как OwnerVoicePrompt на ПК (BuildOwnerVoiceInstruction):
  /// коротко и разговорно, язык последней фразы, цифры только из данных, коды [Д1] вслух не произносить; плюс последние реплики чата.
  Future<String> _callInstruction() async {
    final shop = ref.read(sessionProvider)?.companyName ?? '';
    final data = await _storeContext();
    final recent = _messages.where((m) => !m.error).toList();
    final sb = StringBuffer(
      'Тебя зовут Нур Советник (по-кыргызски — Нур Кеңешчи): ты ИИ-советник владельца магазина «$shop» в Кыргызстане, встроенный в программу NurMarket. '
      'Сейчас вы разговариваете ГОЛОСОМ в реальном времени, как по телефону. Отвечай коротко и разговорно: одно–три предложения, без списков, '
      'таблиц, ссылок и символов. Крупные суммы округляй для слуха. Говори на языке ПОСЛЕДНЕЙ фразы владельца (русский, кыргызский, английский, '
      'турецкий или узбекский). Цифры магазина бери ТОЛЬКО из данных ниже; если нужных данных нет — честно скажи и подскажи раздел программы '
      '(«Сводка», «ABC-анализ», «Долги клиентов», «Склад»). Должники обозначены кодами [Д1], [Д2] — коды вслух не произноси: назови, сколько '
      'должников и на какую сумму, а имена владелец увидит в чате. Никогда не выдумывай суммы, остатки и цены. Давай конкретные советы: что '
      'заказать, что продвигать, где теряются деньги. Таблицу с цифрами программа сама покажет в чате — её не зачитывай. Сообщения «[Программа]» — '
      'справка от программы, на язык ответа они не влияют.\n\nДАННЫЕ МАГАЗИНА на ${_d(DateTime.now())}:\n$data\n',
    );
    final last = recent.length > 4 ? recent.sublist(recent.length - 4) : recent;
    if (last.isNotEmpty) {
      sb.write('\nПОСЛЕДНИЕ РЕПЛИКИ ТЕКСТОВОГО ЧАТА (только для справки; старые просьбы не выполняй):\n');
      for (final m in last) {
        sb.write('${m.user ? 'Владелец' : 'Советник'}: ${m.raw.length > 400 ? '${m.raw.substring(0, 400)}…' : m.raw}\n');
      }
    }
    return sb.toString();
  }

  /// 2026-10-22 (звонок ИИ мобилки): вопрос с цифрами, заданный голосом, — таблица текстовым советником (как LiveTablePassAsync на ПК).
  Future<String> _callTable(String question) async {
    final shop = ref.read(sessionProvider)?.companyName ?? '';
    final data = await _storeContext();
    return ref.read(visionProvider).chat(_system(shop, data), [
      (
        user: true,
        text: '$question\n(Вопрос задан голосом во время звонка. Ответь таблицей по данным магазина: перед таблицей одна короткая строка, '
            'дальше строки «название — число», в конце «Итого», где уместно. Без вступлений и советов. Данных нет — одной строкой честно.)',
      ),
    ]);
  }

  /// 2026-10-22 (звонок ИИ мобилки): «Разговор» — голосовой звонок; после звонка сказанное и таблицы — в чат (чат помнит звонок).
  Future<void> _startCall() async {
    if (_busy) return;
    final turns = await Navigator.of(context).push<List<CallTurn>>(
      MaterialPageRoute(
        builder: (_) => AdvisorCallScreen(
          prepare: _callInstruction,
          askTable: _callTable,
          reveal: (t) => revealDebtorCodes(t, _debtorNames),
        ),
      ),
    );
    if (!mounted || turns == null || turns.isEmpty) return;
    setState(() {
      _messages.removeWhere((m) => m.error);
      for (final t in turns) {
        _messages.add(_Msg(t.user, t.user ? '🎙 ${t.text}' : revealDebtorCodes(t.text, _debtorNames), raw: t.text));
      }
    });
    _toEnd();
  }

  void _toEnd() => WidgetsBinding.instance.addPostFrameCallback((_) {
    if (_scroll.hasClients) _scroll.animateTo(_scroll.position.maxScrollExtent, duration: const Duration(milliseconds: 250), curve: Curves.easeOut);
  });

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(
        l.advisorTitle,
        subtitle: l.advisorOnline,
        actions: [
          // 2026-10-22 (звонок ИИ мобилки): владелец — «и голосовой звонок в ИИ в мобилке тоже добавь!».
          IconButton(tooltip: l.callTitle, onPressed: _busy ? null : _startCall, icon: const Icon(Icons.call_rounded)),
          // 14.10, владелец: «Нур Советник — настройка и добавление ИИ».
          IconButton(
            tooltip: l.aiSettings,
            onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiSettingsScreen())),
            icon: const Icon(Icons.tune_rounded),
          ),
          IconButton(
            tooltip: l.advisorNew,
            onPressed: () => setState(() {
              _messages.clear();
              _context = null;
            }),
            icon: const Icon(Icons.add_comment_outlined),
          ),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: ListView(
              controller: _scroll,
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
              children: [
                for (final m in _messages)
                  Align(
                    alignment: m.user ? Alignment.centerRight : Alignment.centerLeft,
                    child: Container(
                      constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * (m.user ? .8 : .92)),
                      margin: const EdgeInsets.only(bottom: 8),
                      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                      decoration: BoxDecoration(
                        color: m.user ? T.navy : pal.panel,
                        borderRadius: BorderRadius.only(
                          topLeft: const Radius.circular(16),
                          topRight: const Radius.circular(16),
                          bottomLeft: Radius.circular(m.user ? 16 : 4),
                          bottomRight: Radius.circular(m.user ? 4 : 16),
                        ),
                        border: m.user ? null : Border.all(color: pal.border),
                      ),
                      child: GestureDetector(
                        onLongPress: () {
                          Clipboard.setData(ClipboardData(text: m.text));
                          toast(context, '✓');
                        },
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            SelectableText(
                              m.text,
                              style: TextStyle(color: m.user ? Colors.white : (m.error ? T.danger : pal.text), fontSize: 14, height: 1.35),
                            ),
                            // 2026-10-22 (советник мобилки): у ошибки — «Повторить» (и «Настройка ИИ», если ключа нет).
                            if (m.error)
                              Padding(
                                padding: const EdgeInsets.only(top: 8),
                                child: Wrap(
                                  spacing: 6,
                                  runSpacing: 6,
                                  children: [
                                    ActionChip(
                                      avatar: const Icon(Icons.refresh_rounded, size: 16),
                                      label: Text(l.retry),
                                      onPressed: _busy ? null : _answer,
                                    ),
                                    if (m.noKey)
                                      ActionChip(
                                        avatar: const Icon(Icons.tune_rounded, size: 16),
                                        label: Text(l.aiSettings),
                                        onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiSettingsScreen())),
                                      ),
                                  ],
                                ),
                              ),
                          ],
                        ),
                      ),
                    ),
                  ),
                if (_busy)
                  Padding(
                    padding: const EdgeInsets.all(8),
                    child: Row(
                      children: [
                        const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)),
                        const SizedBox(width: 10),
                        Flexible(child: Text(_stage ?? l.advisorThinking)),
                      ],
                    ),
                  ),
              ],
            ),
          ),
          SizedBox(
            height: 44,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              children: [
                for (final q in [l.advisorQ1, l.advisorQ2, l.advisorQ3, l.advisorQ4])
                  Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: ActionChip(label: Text(q), onPressed: _busy ? null : () => _send(q)),
                  ),
              ],
            ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 6, 16, 10),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _input,
                      minLines: 1,
                      maxLines: 4,
                      textInputAction: TextInputAction.send,
                      onSubmitted: (_) => _send(),
                      decoration: InputDecoration(hintText: l.advisorAsk),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton.filled(
                    style: IconButton.styleFrom(backgroundColor: T.accent, foregroundColor: T.navy, minimumSize: const Size(52, 52)),
                    onPressed: _busy ? null : () => _send(),
                    icon: const Icon(Icons.arrow_upward_rounded),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// 14.10, владелец: «Нур Советник — настройка и добавление ИИ». Три поставщика, как у кассы (AiProviders): Gemini —
/// основной, Groq и OpenRouter — запасные. Видно, какой подключён; ключ можно вставить, проверить, убрать; сохраняется
/// на телефоне и в NurCRM (работает и на кассе, и в программе владельца).
class AiSettingsScreen extends ConsumerStatefulWidget {
  const AiSettingsScreen({super.key});
  @override
  ConsumerState<AiSettingsScreen> createState() => _AiSettingsScreenState();
}

class _AiSettingsScreenState extends ConsumerState<AiSettingsScreen> {
  static const _providers = [
    ('gemini', 'Gemini (Google)', 'https://aistudio.google.com/apikey'),
    ('groq', 'Groq', 'https://console.groq.com/keys'),
    ('openrouter', 'OpenRouter', 'https://openrouter.ai/keys'),
  ];
  final _edit = {for (final p in _providers) p.$1: TextEditingController()};
  final Map<String, String?> _current = {};
  final Map<String, String?> _result = {};
  final Set<String> _testing = {};
  final Set<String> _removed = {};
  bool _loaded = false;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    ref.read(visionProvider).allKeys().then((k) {
      if (!mounted) return;
      setState(() {
        _current['gemini'] = k.gemini;
        _current['groq'] = k.groq;
        _current['openrouter'] = k.openRouter;
        _loaded = true;
      });
    });
  }

  static String _mask(String k) => k.length <= 8 ? '••••' : '${k.substring(0, 4)}…${k.substring(k.length - 4)}';

  String? _effective(String id) => _edit[id]!.text.trim().isNotEmpty ? _edit[id]!.text.trim() : (_removed.contains(id) ? null : _current[id]);

  Future<void> _test(String id) async {
    final k = _effective(id);
    if (k == null) return;
    setState(() => _testing.add(id));
    final err = await ref.read(visionProvider).testKey(id, k);
    if (mounted) {
      setState(() {
        _testing.remove(id);
        _result[id] = err ?? '';
      });
    }
  }

  Future<void> _save() async {
    final l = context.l;
    String? value(String id) => _edit[id]!.text.trim().isNotEmpty ? _edit[id]!.text.trim() : (_removed.contains(id) ? '' : null);
    setState(() => _saving = true);
    try {
      await ref.read(visionProvider).saveAllKeys(gemini: value('gemini'), groq: value('groq'), openRouter: value('openrouter'));
      if (!mounted) return;
      Audit.log('settings', l.aiSettings);
      toast(context, l.aiKeysSaved);
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(l.aiSettings),
      body: !_loaded
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
              children: [
                Text(l.aiSettingsHint, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                const SizedBox(height: 12),
                for (final (id, title, url) in _providers) ...[
                  Panel(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Row(
                          children: [
                            Expanded(
                              child: Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
                            ),
                            Builder(
                              builder: (_) {
                                final k = _effective(id);
                                return Text(
                                  k == null ? l.aiNoKeyShort : '${l.aiConnected} · ${_mask(k)}',
                                  style: TextStyle(color: k == null ? pal.textSoft : T.success, fontWeight: FontWeight.w700, fontSize: 12.5),
                                );
                              },
                            ),
                          ],
                        ),
                        const SizedBox(height: 8),
                        TextField(
                          controller: _edit[id],
                          obscureText: true,
                          decoration: InputDecoration(hintText: l.aiPasteKey, isDense: true),
                          onChanged: (_) => setState(() => _result.remove(id)),
                        ),
                        if (_result[id] != null)
                          Padding(
                            padding: const EdgeInsets.only(top: 6),
                            child: Text(
                              _result[id]!.isEmpty ? l.aiTestOk : _result[id]!,
                              style: TextStyle(color: _result[id]!.isEmpty ? T.success : T.danger, fontWeight: FontWeight.w600, fontSize: 12.5),
                            ),
                          ),
                        const SizedBox(height: 6),
                        Wrap(
                          spacing: 6,
                          runSpacing: 6,
                          children: [
                            ActionChip(
                              avatar: const Icon(Icons.open_in_new_rounded, size: 16),
                              label: Text(l.aiGetKey),
                              onPressed: () => launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication),
                            ),
                            ActionChip(
                              avatar: _testing.contains(id)
                                  ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                                  : const Icon(Icons.bolt_rounded, size: 16),
                              label: Text(l.aiTest),
                              onPressed: _effective(id) == null || _testing.contains(id) ? null : () => _test(id),
                            ),
                            if (_current[id] != null && !_removed.contains(id))
                              ActionChip(
                                avatar: const Icon(Icons.delete_outline_rounded, size: 16),
                                label: Text(l.aiRemoveKey),
                                onPressed: () => setState(() {
                                  _removed.add(id);
                                  _edit[id]!.clear();
                                  _result.remove(id);
                                }),
                              ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 8),
                ],
                const SizedBox(height: 8),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: _saving ? null : _save,
                  child: _saving ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.save),
                ),
              ],
            ),
    );
  }
}
