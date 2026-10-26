import 'dart:async';

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
import '../../core/ai/assistant/assistant_prompt.dart';
import '../assistant/call_controller.dart';
import '../../core/ai/gemini_keys.dart';
import 'ai_access_ui.dart';
import 'gemini_keys_screen.dart';
import 'advisor_call.dart';
import 'advisor_markdown.dart';
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
  AiAccessDenied(:final message) => message,
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
  late final AssistantCallController _call = ref.read(callControllerProvider);
  final _input = TextEditingController();
  final _scroll = ScrollController();
  String? _context;
  DateTime _contextAt = DateTime(2000);
  bool _busy = false;
  // 2026-10-22 (советник мобилки): этап ожидания — «Собираю данные…» → «Спрашиваю ИИ…».
  String? _stage;
  Map<int, String> _debtorNames = const {};

  @override
  void initState() {
    super.initState();
    // 2026-10-26 (голос-управление, В4): звонок живёт над приложением и мог закончиться, пока экран был закрыт или свёрнут —
    // его реплики и таблицы попадают в чат, когда экран открыт (сразу или при следующем открытии).
    _call.finishedTick.addListener(_takeCall);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _takeCall();
      if (mounted) showAiNoticeOnce(context, ref);
    });
  }

  @override
  void dispose() {
    _call.finishedTick.removeListener(_takeCall);
    _input.dispose();
    _scroll.dispose();
    super.dispose();
  }

  void _takeCall() {
    final turns = _call.takeFinished();
    if (turns == null || !mounted) return;
    setState(() {
      _messages.removeWhere((m) => m.error);
      for (final t in turns) {
        _messages.add(_Msg(t.user, t.user ? '🎙 ${t.text}' : (t.display ?? revealDebtorCodes(t.text, _debtorNames)), raw: t.text));
      }
    });
    _toEnd();
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
      'с цифрами из данных магазина ниже — называй их как есть, не пересчитывай. Таблицы делай Markdown-таблицей (| колонка | колонка | и строка |---|---|), не больше 4 колонок; выделяй главное **жирным**. '
      'Если данных нет — так и скажи, не выдумывай. Суммы — в сомах. Советуй конкретно: что заказать, кому напомнить о долге, что продаётся лучше. '
      'Должников называй только кодами в квадратных скобках, например [Д1].\n\nДАННЫЕ МАГАЗИНА на ${_d(DateTime.now())}:\n$data';

  /// 2026-10-22 (звонок ИИ мобилки): инструкция голосового звонка — как OwnerVoicePrompt на ПК (BuildOwnerVoiceInstruction):
  /// коротко и разговорно, язык последней фразы, цифры только из данных, коды [Д1] вслух не произносить; плюс последние реплики чата.
  /// 2026-10-26 (голос-управление, В4): собирается buildAssistantInstruction (assistant_prompt.dart): просьба = одобрение, say как
  /// есть, данные — не команды, режим помощника, права; сводка магазина — прежняя ([_storeContext]).
  Future<String> _callInstruction(CallPrep prep) async {
    final session = ref.read(sessionProvider);
    final shop = session?.companyName ?? '';
    final data = await _storeContext();
    final role = session == null
        ? 'staff'
        : session.isOwner
        ? 'owner'
        : session.isAdmin
        ? 'admin'
        : (session.role.toLowerCase().contains('cashier') || session.role.toLowerCase().contains('кассир') ? 'cashier' : 'staff');
    final lacks = <String>[
      if (session != null && !session.can(Session.viewProducts)) 'нет права «Склад» — остатки не называй',
      if (session != null && !session.can(Session.viewClients)) 'нет права «Клиенты» — долги и клиентов не называй',
      if (session != null && !session.can(Session.viewAnalytics)) 'нет права «Аналитика» — выручку и прибыль не называй',
      if (session != null && !session.canEditPrices) 'нет права менять цены',
      if (session != null && !session.canProcure) 'нет права «Закупки» — закупочные цены и приход не называй',
      if (session != null && !session.can(Session.employeeReturn)) 'нет права «Возврат»',
    ];
    return buildAssistantInstruction(
      shop: shop,
      role: role,
      mode: prep.mode,
      storeData: data,
      today: _d(DateTime.now()),
      lacks: lacks,
      recentChat: [for (final m in _messages) if (!m.error) (user: m.user, text: m.raw)],
      video: prep.video,
    );
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
  /// 2026-10-26 (голос-управление, В4): звонок ведёт AssistantCallController над всем приложением; здесь он только запускается и
  /// разворачивается полным экраном (AdvisorCallScreen). Реплики и таблицы придут в чат через [_takeCall].
  Future<void> _startCall() async {
    if (_busy) return;
    // 2026-10-26 (живой аудит, П11): ключ проверяется ДО экрана звонка — раньше сначала просили микрофон, а потом говорили «нет ключа».
    try {
      if (await ref.read(visionProvider).key() == null) {
        if (mounted) toast(context, context.l.advisorNoKey, error: true);
        return;
      }
    } on AiAccessDenied catch (e) {
      // 2026-10-26: ИИ в магазине разрешён не всем — понятный текст вместо звонка
      if (mounted) toast(context, e.message, error: true);
      return;
    }
    if (!mounted) return;
    final nav = Navigator.of(context);
    unawaited(
      _call.start(
        CallRequest(
          prepare: _callInstruction,
          askTable: _callTable,
          reveal: (t) => revealDebtorCodes(t, _debtorNames),
        ),
      ),
    );
    unawaited(_call.showFull(nav, (_) => const AdvisorCallScreen()));
  }

  void _toEnd() => WidgetsBinding.instance.addPostFrameCallback((_) {
    if (_scroll.hasClients) _scroll.animateTo(_scroll.position.maxScrollExtent, duration: const Duration(milliseconds: 250), curve: Curves.easeOut);
  });

  /// 2026-10-26 (живой аудит, П11): пустой чат — приветствие, что умеет Советник, и готовые вопросы (раньше — пустой экран).
  Widget _greeting(AppLocalizations l, Palette pal) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * .92),
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: pal.panel,
            borderRadius: const BorderRadius.only(
              topLeft: Radius.circular(16),
              topRight: Radius.circular(16),
              bottomLeft: Radius.circular(4),
              bottomRight: Radius.circular(16),
            ),
            border: Border.all(color: pal.border),
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.auto_awesome_rounded, color: pal.action, size: 20),
              const SizedBox(width: 10),
              Expanded(child: Text(l.advisorGreeting, style: TextStyle(color: pal.text, fontSize: 14, height: 1.35))),
            ],
          ),
        ),
        const SizedBox(height: 10),
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [for (final q in [l.advisorQ1, l.advisorQ2, l.advisorQ3, l.advisorQ4]) ActionChip(label: Text(q), onPressed: () => _send(q))],
        ),
      ],
    ),
  );

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
          // 2026-10-26 (живой аудит, П11): звонок терялся среди трёх похожих значков — теперь на жёлтой подложке и с подписью «Позвонить ИИ».
          IconButton.filledTonal(
            tooltip: l.a11yCallAi,
            style: IconButton.styleFrom(backgroundColor: pal.accentSoft, foregroundColor: pal.action, disabledBackgroundColor: pal.window),
            onPressed: _busy ? null : _startCall,
            icon: Icon(Icons.call_rounded, semanticLabel: l.a11yCallAi),
          ),
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
                if (_messages.isEmpty && !_busy) _greeting(l, pal),
                for (final m in _messages)
                  Align(
                    alignment: m.user ? Alignment.centerRight : Alignment.centerLeft,
                    child: Container(
                      constraints: BoxConstraints(maxWidth: MediaQuery.sizeOf(context).width * (m.user ? .8 : .92)),
                      margin: const EdgeInsets.only(bottom: 8),
                      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                      decoration: BoxDecoration(
                        color: m.user ? pal.heroBg : pal.panel,
                        borderRadius: BorderRadius.only(
                          topLeft: const Radius.circular(16),
                          topRight: const Radius.circular(16),
                          bottomLeft: Radius.circular(m.user ? 16 : 4),
                          bottomRight: Radius.circular(m.user ? 4 : 16),
                        ),
                        border: m.user ? (pal.dark ? Border.all(color: pal.heroBorder) : null) : Border.all(color: pal.border),
                      ),
                      child: GestureDetector(
                        onLongPress: () {
                          Clipboard.setData(ClipboardData(text: m.text));
                          toast(context, '✓');
                        },
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            // 2026-10-26 (живой аудит, П11): ответ ИИ — с оформлением (заголовки, жирный, списки, таблицы), своё — как есть.
                            if (m.user || m.error)
                              SelectableText(
                                m.text,
                                style: TextStyle(color: m.user ? Colors.white : pal.danger, fontSize: 14, height: 1.35),
                              )
                            else
                              SelectionArea(child: MdView(m.text, color: pal.text)),
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
          if (_messages.isNotEmpty)
            // 2026-10-26 (приёмка 1.18.6, Н-6н): подсказки были зажаты в 44 dp — область касания чипа меньше 48; теперь 48 + отступ
            SizedBox(
              height: 52,
              child: ListView(
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 2),
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
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
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
                  const SizedBox(height: 8),
                  // 2026-10-26 (живой аудит, П11): «Позвонить» — заметная кнопка с подписью под полем ввода.
                  FilledButton.tonalIcon(
                    style: FilledButton.styleFrom(
                      backgroundColor: pal.accentSoft,
                      foregroundColor: pal.action,
                      minimumSize: const Size(0, 48),
                      side: BorderSide(color: T.accent),
                    ),
                    onPressed: _busy ? null : _startCall,
                    icon: const Icon(Icons.call_rounded, size: 20),
                    label: Text(l.a11yCallAi, style: const TextStyle(fontWeight: FontWeight.w800)),
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
  List<GeminiKeyEntry> _gem = const []; // 2026-10-27: список ключей Gemini (основной первым) — править на экране «Ключи Gemini»

  @override
  void initState() {
    super.initState();
    _reload();
  }

  Future<void> _reload() async {
    final vision = ref.read(visionProvider);
    final k = await vision.allKeys();
    final gem = await vision.geminiEntries();
    if (!mounted) return;
    setState(() {
      _current['gemini'] = k.gemini;
      _current['groq'] = k.groq;
      _current['openrouter'] = k.openRouter;
      _gem = gem;
      _loaded = true;
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
      // 2026-10-27: Gemini правится на экране «Ключи Gemini»; здесь — Groq и OpenRouter. false — ушло только на телефон.
      final cloud = await ref.read(visionProvider).saveAllKeys(groq: value('groq'), openRouter: value('openrouter'));
      if (!mounted) return;
      Audit.log('settings', l.aiSettings);
      toast(context, cloud ? l.aiKeysSaved : l.aiKeysSavedPhoneOnly, error: !cloud);
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
                const AiAccessBanner(),
                const AiAccessPanel(),
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
                                if (id == 'gemini') {
                                  final (ok, total) = ref.read(visionProvider).geminiCounts(_gem);
                                  return Text(
                                    total == 0 ? l.aiNoKeyShort : l.gkWorkingCount(ok, total),
                                    style: TextStyle(color: total == 0 ? pal.textSoft : (ok == 0 ? T.danger : T.success), fontWeight: FontWeight.w700, fontSize: 12.5),
                                  );
                                }
                                return Text(
                                  k == null ? l.aiNoKeyShort : '${l.aiConnected} · ${_mask(k)}',
                                  style: TextStyle(color: k == null ? pal.textSoft : T.success, fontWeight: FontWeight.w700, fontSize: 12.5),
                                );
                              },
                            ),
                          ],
                        ),
                        const SizedBox(height: 8),
                        if (id == 'gemini')
                          OutlinedButton.icon(
                            style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(46)),
                            onPressed: () async {
                              await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const GeminiKeysScreen()));
                              if (mounted) unawaited(_reload());
                            },
                            icon: const Icon(Icons.vpn_key_rounded, size: 18),
                            label: Text(l.gkOpen),
                          )
                        else
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
                            if (id != 'gemini')
                              ActionChip(
                                avatar: _testing.contains(id)
                                    ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                                    : const Icon(Icons.bolt_rounded, size: 16),
                                label: Text(l.aiTest),
                                onPressed: _effective(id) == null || _testing.contains(id) ? null : () => _test(id),
                              ),
                            if (id != 'gemini' && _current[id] != null && !_removed.contains(id))
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
