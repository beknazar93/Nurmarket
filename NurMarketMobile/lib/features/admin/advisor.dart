import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/vision.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';

import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_client.dart';
import '../../core/util/ui.dart';
import '../scan/smart_camera.dart';
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

class _AdvisorScreenState extends ConsumerState<AdvisorScreen> {
  final List<({bool user, String text})> _messages = [];
  final _input = TextEditingController();
  final _scroll = ScrollController();
  String? _context;
  DateTime _contextAt = DateTime(2000);
  bool _busy = false;

  Future<String> _storeContext() async {
    if (_context != null && DateTime.now().difference(_contextAt) < const Duration(minutes: 5)) return _context!;
    final repo = ref.read(analyticsRepoProvider);
    final db = ref.read(databaseProvider);
    final today = DateUtils.dateOnly(DateTime.now());
    final sb = StringBuffer();
    String fmt(SalesSummary s) => 'выручка ${money(s.revenue)} сом, чеков ${s.checks}, средний чек ${money(s.avg)}, валовая прибыль ${money(s.profit)}';
    Future<void> part(String title, Future<void> Function() load) async {
      try {
        await load();
      } catch (_) {
        sb.writeln('$title: не загрузилось');
      }
    }

    await part('Сегодня', () async => sb.writeln('Сегодня (${_d(today)}): ${fmt(await repo.sales(today, today))}'));
    final yesterday = today.subtract(const Duration(days: 1));
    await part('Вчера', () async => sb.writeln('Вчера (${_d(yesterday)}): ${fmt(await repo.sales(yesterday, yesterday))}'));
    final weekFrom = today.subtract(const Duration(days: 6));
    await part('Неделя', () async {
      final w = await repo.sales(weekFrom, today);
      sb.writeln('За 7 дней: ${fmt(w)}');
      if (w.byDay.isNotEmpty) sb.writeln('По дням: ${w.byDay.map((d) => '${_d(d.$1)} — ${money(d.$2)}').join('; ')}');
      if (w.payments.isNotEmpty) sb.writeln('Оплаты за неделю: ${w.payments.entries.map((e) => '${e.key} ${money(e.value)}').join('; ')}');
    });
    await part('Товары', () async {
      final top = (await repo.products(weekFrom, today))..sort((a, b) => b.revenue.compareTo(a.revenue));
      sb.writeln(
        'Лучшие товары недели (название — штук — выручка): ${top.take(15).map((p) => '${p.name} — ${qtyText(p.qty)} — ${money(p.revenue)}').join('; ')}',
      );
    });
    await part('Остатки', () async {
      final low = (await db.searchProducts('', limit: 20000)).where((p) => !p.isService && p.quantity <= 3).toList()
        ..sort((a, b) => a.quantity.compareTo(b.quantity));
      sb.writeln('Заканчивается (${low.length}): ${low.take(30).map((p) => '${p.name} — ${qtyText(p.quantity)} ${p.unit}').join('; ')}');
    });
    await part('Долги', () async {
      final debtors = (await ref.read(opsRepoProvider).debtors())..sort((a, b) => b.debt.compareTo(a.debt));
      sb.writeln(
        'Должники (${debtors.length}, всего ${money(debtors.fold(0.0, (s, d) => s + d.debt))}): ${debtors.take(15).map((d) => '${d.name}${d.phone.isEmpty ? '' : ' ${d.phone}'} — ${money(d.debt)}').join('; ')}',
      );
    });
    _contextAt = DateTime.now();
    return _context = sb.toString();
  }

  static String _d(DateTime d) => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}';

  Future<void> _send([String? preset]) async {
    final l = context.l;
    final text = (preset ?? _input.text).trim();
    if (text.isEmpty || _busy) return;
    // 14.10: ключ Groq или OpenRouter тоже подходит — окно ввода ключа только когда нет ни одного.
    if (!await ref.read(visionProvider).hasChatKey()) {
      if (!mounted || !await ensureAiKey(context, ref)) return;
    }
    setState(() {
      _messages.add((user: true, text: text));
      _busy = true;
      _input.clear();
    });
    _toEnd();
    try {
      final shop = ref.read(sessionProvider)?.companyName ?? '';
      final data = await _storeContext();
      final system =
          'Ты — Нур Советник, помощник владельца магазина «$shop» в Кыргызстане. Отвечай по-русски (или на языке вопроса), коротко и по делу, '
          'с цифрами из данных магазина ниже. Таблицы делай простым списком строк «название — число». Если данных нет — так и скажи, не выдумывай. '
          'Суммы — в сомах. Советуй конкретно: что заказать, кому напомнить о долге, что продаётся лучше.\n\nДАННЫЕ МАГАЗИНА на ${_d(DateTime.now())}:\n$data';
      final answer = await ref.read(visionProvider).chat(system, _messages);
      if (mounted) setState(() => _messages.add((user: false, text: answer)));
    } on VisionException catch (e) {
      if (mounted) setState(() => _messages.add((user: false, text: e.noKey ? l.aiKeyNeeded : e.message)));
    } finally {
      if (mounted) setState(() => _busy = false);
      _toEnd();
    }
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
                        child: SelectableText(m.text, style: TextStyle(color: m.user ? Colors.white : pal.text, fontSize: 14, height: 1.35)),
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
                        Text(l.advisorThinking),
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
