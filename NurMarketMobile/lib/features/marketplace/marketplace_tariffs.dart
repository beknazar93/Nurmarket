import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/api/server_capabilities.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart';
import 'marketplace_data.dart';
import 'marketplace_screen.dart';
import 'marketplace_state.dart';
import 'marketplace_strings.dart';

/// 2026-11-27 (Маркетплейс мобилки): вкладка «Тарифы» — тарифы с серверными ценами (GET public/tariffs/) и сборка «Своё»: отметить функции и пакеты, получить расчёт
/// у сервера (POST users/company/tariff/quote/) и отправить заявку (POST users/company/tariff/custom/ с Idempotency-Key; её отправляет только владелец — как в кассе).
/// Своих цен нет: суммы — только то, что прислал сервер.
class MpTariffsView extends ConsumerStatefulWidget {
  final MpState state;
  const MpTariffsView({super.key, required this.state});
  @override
  ConsumerState<MpTariffsView> createState() => _MpTariffsViewState();
}

class _MpTariffsViewState extends ConsumerState<MpTariffsView> {
  bool _build = false;
  Set<String>? _initial; // функции, включённые сейчас
  Set<String> _sel = {}; // отмеченные функции
  final Set<String> _packs = {};
  String _period = 'month';
  MpQuote? _quote;
  bool _busy = false;
  String _key = const Uuid().v4();
  String? _error;
  String? _sent;
  String? _payUrl;

  void _changed() {
    _quote = null;
    _error = null;
    _sent = null;
    _key = const Uuid().v4(); // другой набор — другая заявка
  }

  List<String>? _modulesBody() {
    final init = _initial;
    if (init == null || (_sel.length == init.length && _sel.containsAll(init))) return null; // набор функций не менялся — поле не шлём
    return _sel.toList()..sort();
  }

  bool get _hasChange => _modulesBody() != null || _packs.isNotEmpty;

  Future<void> _calc() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final q = await ref.read(marketplaceRepoProvider).quote(_packs.toList()..sort(), _modulesBody(), _period);
      if (mounted) setState(() => _quote = q);
    } catch (e) {
      if (mounted) setState(() => _error = _errText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _send() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final o = await ref.read(marketplaceRepoProvider).submitCustom(_packs.toList()..sort(), _modulesBody(), _period, _key);
      if (mounted) {
        setState(() {
          _sent = Ms.requestSent(context);
          _payUrl = o.payUrl;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _error = _errText(e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  String _errText(Object e) {
    if (e is ApiException && e.status == 403) return Ms.ownerOnly(context);
    if (e is ApiException && e.message.trim().isNotEmpty && e.status != null && e.status! < 500) return e.message;
    return Ms.requestFail(context);
  }

  @override
  Widget build(BuildContext context) {
    final st = widget.state;
    final snap = st.snap;
    final p = context.pal;
    final lang = mpLang(context);
    if (st.loading && snap == null) return const Center(child: CircularProgressIndicator());
    if (snap == null || (snap.tariffs.plans.isEmpty && snap.tariffs.packs.isEmpty && snap.modules.isEmpty)) {
      return EmptyState(icon: Icons.cloud_off_rounded, text: Ms.loadFail(context), actionLabel: Ms.retry(context), onAction: () => ref.read(marketplaceProvider.notifier).load());
    }
    final isOwner = ref.watch(sessionProvider)?.isOwner ?? false;
    final currency = snap.currency;
    final views = buildModuleViews(snap, st.content, lang, features: ServerCapabilities.featureSet).where((v) => !v.soon && !v.linked && !v.isFeatureCard).toList();
    if (_initial == null) {
      _initial = {for (final v in views) if (v.on) v.id};
      _sel = {..._initial!};
    }
    final cur = ServerCapabilities.plan.code;
    return RefreshIndicator(
      onRefresh: () => ref.read(marketplaceProvider.notifier).load(),
      child: ListView(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 28),
        children: [
          if (snap.offline) Padding(padding: const EdgeInsets.only(bottom: 10), child: Text(Ms.offline(context), style: TextStyle(fontSize: 12.5, color: p.warn, height: 1.3))),
          for (final plan in snap.tariffs.plans.where((x) => x.code != 'custom'))
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Panel(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(child: Text(pickName(lang, plan.names, null, plan.code), style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800))),
                        if (plan.code == cur)
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                            decoration: BoxDecoration(color: p.successSoft, borderRadius: BorderRadius.circular(20)),
                            child: Text(Ms.yourPlan(context), style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: p.success)),
                          ),
                      ],
                    ),
                    const SizedBox(height: 6),
                    Text(plan.price == null ? Ms.priceTbd(context) : Ms.priceMonth(context, plan.price!, currency), style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800, color: p.price)),
                    if (plan.yearPrice != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text(Ms.yearOffer(context, Ms.money(context, plan.yearPrice!, currency), 10), style: TextStyle(fontSize: 13, color: p.textSoft))),
                    if (plan.trialDays > 0) Padding(padding: const EdgeInsets.only(top: 4), child: Text(Ms.trialDaysLabel(context, plan.trialDays), style: TextStyle(fontSize: 13, color: p.textSoft))),
                  ],
                ),
              ),
            ),
          const SizedBox(height: 6),
          Panel(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.tune_rounded, color: p.action),
                    const SizedBox(width: 10),
                    Expanded(child: Text(Ms.customTitle(context), style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800))),
                  ],
                ),
                const SizedBox(height: 6),
                Text(Ms.customHint(context), style: TextStyle(fontSize: 13.5, height: 1.4, color: p.textSoft)),
                if (!isOwner) Padding(padding: const EdgeInsets.only(top: 8), child: Text(Ms.ownerOnly(context), style: TextStyle(fontSize: 13, color: p.warn, height: 1.35))),
                const SizedBox(height: 12),
                OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: () => setState(() => _build = !_build),
                  child: Text(_build ? Ms.cancel(context) : Ms.customTitle(context), maxLines: 2, textAlign: TextAlign.center),
                ),
              ],
            ),
          ),
          if (_build) ..._builder(context, views, snap, st, lang, currency, isOwner),
        ],
      ),
    );
  }

  List<Widget> _builder(BuildContext context, List<MpModuleView> views, MpSnapshot snap, MpState st, String lang, String currency, bool isOwner) {
    final p = context.pal;
    Widget head(String t) => Padding(
      padding: const EdgeInsets.only(top: 18, bottom: 6),
      child: Text(t, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
    );
    final q = _quote;
    return [
      head(Ms.tabFeatures(context)),
      for (final v in views)
        CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          controlAffinity: ListTileControlAffinity.leading,
          value: _sel.contains(v.id),
          onChanged: (x) => setState(() {
            x == true ? _sel.add(v.id) : _sel.remove(v.id);
            _changed();
          }),
          title: Text(v.name, style: const TextStyle(fontSize: 14.5, fontWeight: FontWeight.w700)),
          subtitle: Text(
            [v.lineFor(lang), if (v.on) Ms.inPlan(context) else (v.monthly == null ? null : Ms.priceMonth(context, v.monthly!, currency))].whereType<String>().join('\n'),
            style: TextStyle(fontSize: 12.5, height: 1.35, color: p.textSoft),
          ),
        ),
      if (snap.tariffs.packs.isNotEmpty) ...[
        head(Ms.packsTitle(context)),
        for (final k in snap.tariffs.packs)
          CheckboxListTile(
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            value: _packs.contains(k.code),
            onChanged: (x) => setState(() {
              x == true ? _packs.add(k.code) : _packs.remove(k.code);
              _changed();
            }),
            title: Text(pickName(lang, k.names, st.content.pack(k.code)?.name, k.code), style: const TextStyle(fontSize: 14.5, fontWeight: FontWeight.w700)),
            subtitle: Text(
              [
                if (st.content.pack(k.code)?.line.isEmpty == false) st.content.pack(k.code)!.line.of(lang) else (k.description[lang] ?? k.description['ru']),
                Ms.priceMonth(context, k.monthlyFee, currency),
              ].whereType<String>().where((s) => s.isNotEmpty).join('\n'),
              style: TextStyle(fontSize: 12.5, height: 1.35, color: p.textSoft),
            ),
          ),
      ],
      const SizedBox(height: 12),
      SegmentedButton<String>(
        showSelectedIcon: false,
        style: SegmentedButton.styleFrom(minimumSize: const Size(0, 48)),
        segments: [
          ButtonSegment(value: 'month', label: Text(Ms.periodMonth(context), maxLines: 1, overflow: TextOverflow.ellipsis)),
          ButtonSegment(value: 'year', label: Text(Ms.periodYear(context), maxLines: 1, overflow: TextOverflow.ellipsis)),
        ],
        selected: {_period},
        onSelectionChanged: (v) => setState(() {
          _period = v.first;
          _changed();
        }),
      ),
      const SizedBox(height: 12),
      if (!_hasChange) Text(Ms.nothingSelected(context), style: TextStyle(fontSize: 13, color: p.textSoft)),
      FilledButton(
        style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
        onPressed: _busy || !_hasChange ? null : _calc,
        child: _busy && q == null ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(Ms.calcBtn(context)),
      ),
      if (q != null) ...[
        const SizedBox(height: 12),
        Panel(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (q.base != null) _line(context, Ms.baseLine(context), Ms.priceMonth(context, q.base!, currency)),
              for (final m in q.extra) _line(context, '+ ${pickName(lang, m.names, null, m.id)}', Ms.priceMonth(context, m.monthly, currency)),
              const Divider(height: 20),
              if (_period == 'year' && q.totalYear != null) ...[
                _line(context, Ms.totalYear(context), Ms.priceYear(context, q.totalYear!, currency), bold: true),
                if (q.yearPaidMonths != null) Padding(padding: const EdgeInsets.only(top: 4), child: Text('${q.yearPaidMonths} / 12', style: TextStyle(fontSize: 12.5, color: p.textSoft))),
              ] else if (q.totalMonth != null)
                _line(context, Ms.totalMonth(context), Ms.priceMonth(context, q.totalMonth!, currency), bold: true),
            ],
          ),
        ),
        const SizedBox(height: 12),
        if (isOwner)
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
            onPressed: _busy || _sent != null ? null : _send,
            child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(Ms.sendBtn(context)),
          )
        else
          Text(Ms.ownerOnly(context), style: TextStyle(fontSize: 13, color: p.warn, height: 1.35)),
      ],
      if (_error != null) Padding(padding: const EdgeInsets.only(top: 10), child: Text(_error!, style: TextStyle(fontSize: 13.5, color: p.danger, height: 1.35))),
      if (_sent != null) ...[
        Padding(padding: const EdgeInsets.only(top: 10), child: Text(_sent!, style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: p.success, height: 1.35))),
        if (_payUrl != null && _payUrl!.isNotEmpty)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: OutlinedButton(
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
              onPressed: () {
                final u = Uri.tryParse(_payUrl!);
                if (u != null && (u.scheme == 'https' || u.scheme == 'http')) launchUrl(u, mode: LaunchMode.externalApplication);
              },
              child: Text(Ms.openPay(context)),
            ),
          ),
      ],
    ];
  }

  Widget _line(BuildContext context, String a, String b, {bool bold = false}) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 3),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(child: Text(a, style: TextStyle(fontSize: bold ? 15 : 14, fontWeight: bold ? FontWeight.w800 : FontWeight.w500, height: 1.3))),
        const SizedBox(width: 8),
        Text(b, style: TextStyle(fontSize: bold ? 16 : 14, fontWeight: bold ? FontWeight.w800 : FontWeight.w600)),
      ],
    ),
  );
}
