import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/server_capabilities.dart';
import '../../core/util/ui.dart';
import 'marketplace_content.dart';
import 'marketplace_demo.dart';
import 'marketplace_icons.dart';
import 'marketplace_screen.dart';
import 'marketplace_state.dart';
import 'marketplace_strings.dart';

/// 2026-11-27 (Маркетплейс мобилки): страница функции — полное описание «что делает», для кого, как включить, демонстрация «как работает», цена с сервера и действия
/// «Включить» / «Выключить» / «Попробовать N дн.» (те же серверные вызовы, что в кассе, перед включением и выключением — вопрос). Тексты — из content.json (касса).
class MpModuleScreen extends ConsumerStatefulWidget {
  final String id;
  const MpModuleScreen({super.key, required this.id});
  @override
  ConsumerState<MpModuleScreen> createState() => _MpModuleScreenState();
}

class _MpModuleScreenState extends ConsumerState<MpModuleScreen> {
  void _toast(String text) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(text)));
  }

  Future<void> _toggle(MpModuleView v, bool enable, String currency) async {
    final c = context;
    final price = enable ? mpPriceLabel(c, v, currency) : null;
    final ok = await confirmAsk(
      c,
      title: enable ? Ms.confirmEnableTitle(c, v.name) : Ms.confirmDisableTitle(c, v.name),
      message: enable ? (v.included ? Ms.priceTermsIncluded(c) : (price != null && v.monthly != null ? Ms.priceTermsPaid(c, price) : Ms.priceTermsUnknown(c))) : Ms.confirmDisableBody(c),
      ok: enable ? Ms.enable(c) : Ms.disable(c),
      danger: !enable,
    );
    if (!ok || !mounted) return;
    final r = await ref.read(marketplaceProvider.notifier).toggle(v.id, enable);
    if (!mounted) return;
    _toast(r.ok ? (enable ? Ms.doneOn(context, v.name) : Ms.doneOff(context, v.name)) : Ms.describe(context, r));
  }

  Future<void> _trial(MpModuleView v) async {
    final r = await ref.read(marketplaceProvider.notifier).trial(v.id);
    if (!mounted) return;
    _toast(r.ok ? Ms.doneTrial(context, v.name) : Ms.describe(context, r));
  }

  @override
  Widget build(BuildContext context) {
    final st = ref.watch(marketplaceProvider);
    final snap = st.snap;
    final lang = mpLang(context);
    final views = snap == null ? <MpModuleView>[] : buildModuleViews(snap, st.content, lang, features: ServerCapabilities.featureSet);
    final v = views.where((x) => x.id == widget.id).firstOrNull;
    if (v == null) {
      return Scaffold(appBar: ScreenTitle(Ms.title(context)), body: EmptyState(icon: Icons.search_off_rounded, text: Ms.nothingFound(context)));
    }
    final p = context.pal;
    final c = v.content;
    final currency = snap?.currency ?? 'KGS';
    final busy = st.busy.contains(v.id);
    final offline = snap?.offline ?? false;
    final price = mpPriceLabel(context, v, currency);
    final line = v.lineFor(lang);
    final what = c != null && !c.what.isEmpty ? c.what.of(lang) : null;
    final body = what ?? (c != null && !c.short.isEmpty ? c.short.of(lang) : (line ?? Ms.noDescription(context)));
    // «Цена»: условия по правилам кассы (MarketplaceModules.Terms)
    final terms = v.isFeatureCard
        ? switch (v.featureMode) {
            'builtin' => Ms.termsBuiltin(context),
            'service' => Ms.termsService(context),
            'capability' => v.soon ? Ms.termsCapabilitySoon(context) : Ms.termsCapabilityOn(context),
            _ => v.monthly != null ? Ms.priceTermsPaid(context, Ms.priceMonth(context, v.monthly!, currency)) : Ms.termsFiscal(context),
          }
        : v.soon
        ? Ms.soonNote(context)
        : v.linked
        ? Ms.linkedNote(context)
        : v.included
        ? Ms.priceTermsIncluded(context)
        : (v.monthly != null ? Ms.priceTermsPaid(context, Ms.priceMonth(context, v.monthly!, currency)) : Ms.priceTermsUnknown(context));
    final gains = c == null ? const <MpGain>[] : (c.gains.isNotEmpty ? c.gains : c.benefits);
    Widget head(String t) => Padding(
      padding: const EdgeInsets.only(top: 22, bottom: 8),
      child: Text(t, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
    );
    return Scaffold(
      appBar: ScreenTitle(Ms.title(context)), // название функции — крупно под шапкой (в шапке при шрифте ×1,3 длинное название обрезалось бы)
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              MpIconTile(view: v, size: 64),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(v.name, style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w800, height: 1.25)),
                    const SizedBox(height: 6),
                    Wrap(
                      spacing: 10,
                      runSpacing: 6,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        MpStatusChip(view: v),
                        if (price != null) Text(price, style: TextStyle(fontSize: 14, fontWeight: FontWeight.w800, color: p.price)),
                      ],
                    ),
                  ],
                ),
              ),
            ],
          ),
          if (line != null) ...[const SizedBox(height: 14), Text(line, style: TextStyle(fontSize: 15, height: 1.4, color: p.textSoft))],
          if (offline) ...[
            const SizedBox(height: 12),
            Text(Ms.offline(context), style: TextStyle(fontSize: 12.5, color: p.warn, height: 1.3)),
          ],
          const SizedBox(height: 16),
          if (v.canToggle) ...[
            if (v.on)
              OutlinedButton(
                style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                onPressed: busy || offline ? null : () => _toggle(v, false, currency),
                child: busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(Ms.disable(context)),
              )
            else ...[
              FilledButton(
                style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                onPressed: busy || offline ? null : () => _toggle(v, true, currency),
                child: busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(Ms.enable(context)),
              ),
              if (v.trialAvailable) ...[
                const SizedBox(height: 8),
                OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: busy || offline ? null : () => _trial(v),
                  child: Text(Ms.tryDays(context, v.trialDays)),
                ),
              ],
            ],
          ] else if (!v.stateKnown && !v.soon)
            Text(Ms.stateUnknown(context), style: TextStyle(fontSize: 13, color: p.textSoft)),
          head(Ms.whatItDoes(context)),
          Text(body, style: const TextStyle(fontSize: 15, height: 1.45)),
          if (v.need != null) ...[
            head(Ms.need(context)),
            Text(v.need!.of(lang), style: const TextStyle(fontSize: 14.5, height: 1.45)),
          ],
          if (c != null && c.demo.isNotEmpty) ...[
            head(Ms.howItWorks(context)),
            MpDemoPlayer(steps: c.demo, lang: lang),
          ],
          if (gains.isNotEmpty) ...[
            head(Ms.gains(context)),
            for (final g in gains)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Container(
                      width: 40,
                      height: 40,
                      decoration: BoxDecoration(color: p.accentSoft, borderRadius: BorderRadius.circular(12)),
                      child: Icon(moduleIcon('', key: g.icon), size: 22, color: p.action),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(g.title.of(lang), style: const TextStyle(fontSize: 14.5, fontWeight: FontWeight.w800, height: 1.3)),
                          if (!g.text.isEmpty) Padding(padding: const EdgeInsets.only(top: 2), child: Text(g.text.of(lang), style: TextStyle(fontSize: 13.5, height: 1.4, color: p.textSoft))),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
          ],
          if (c != null && c.steps.isNotEmpty) ...[
            head(Ms.howToEnable(context)),
            for (var i = 0; i < c.steps.length; i++)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Container(
                      width: 26,
                      height: 26,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(color: p.action, shape: BoxShape.circle),
                      child: Text('${i + 1}', style: TextStyle(fontSize: 13, fontWeight: FontWeight.w800, color: p.onAction)),
                    ),
                    const SizedBox(width: 10),
                    Expanded(child: Text(c.steps[i].of(lang), style: const TextStyle(fontSize: 14.5, height: 1.4))),
                  ],
                ),
              ),
            if (!v.isFeatureCard) Text(Ms.stepsNote(context), style: TextStyle(fontSize: 12, color: p.textSoft, height: 1.35)),
          ],
          if (c != null && c.audience.isNotEmpty) ...[
            head(Ms.forWhom(context)),
            for (final a in c.audience)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Padding(padding: const EdgeInsets.only(top: 2, right: 8), child: Icon(Icons.check_rounded, size: 18, color: p.success)),
                    Expanded(child: Text(a.of(lang), style: const TextStyle(fontSize: 14.5, height: 1.4))),
                  ],
                ),
              ),
          ],
          head(Ms.priceTitle(context)),
          Text(terms, style: TextStyle(fontSize: 14, height: 1.45, color: p.textSoft)),
        ],
      ),
    );
  }
}
