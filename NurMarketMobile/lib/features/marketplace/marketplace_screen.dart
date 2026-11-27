import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/assistant/knowledge_tools.dart' show findModule;
import '../../core/api/server_capabilities.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'marketplace_data.dart';
import 'marketplace_icons.dart';
import 'marketplace_module_screen.dart';
import 'marketplace_state.dart';
import 'marketplace_strings.dart';
import 'marketplace_tariffs.dart';

/// 2026-11-27 (просьба владельца «И в мобилку тоже Маркетплейс добавь!»): раздел «Маркетплейс» — функции (модули) компании по группам-сферам с поиском,
/// страница функции с описанием и демонстрацией, включение / выключение / проба теми же серверными вызовами, что в кассе, тарифы и «Своё».
/// Раздел для владельца и администратора (меняют модули только они, как в кассе); кассиру — «нет доступа». Без связи — последний сохранённый ответ.

String mpLang(BuildContext c) => Localizations.localeOf(c).languageCode;

/// Подпись состояния: «Подключён» / «Входит в вашу сферу» / «Проба до …» / «Доступен» / «Скоро».
String mpStatusLabel(BuildContext c, MpModuleView v) {
  if (v.soon) return Ms.statusSoon(c);
  if (v.featureMode != null) {
    return switch (v.featureMode) {
      'builtin' => Ms.included(c),
      'service' => Ms.supportService(c),
      'capability' => Ms.availableNow(c),
      _ => Ms.statusAvail(c), // fiscal: режим включает NurCRM по заявке
    };
  }
  if (v.on) {
    if (v.onTrial) return v.trialUntil != null ? Ms.trialUntil(c, v.trialUntil!.toLocal()) : Ms.trialPeriod(c);
    if (v.included) return Ms.includedSphere(c);
    return Ms.statusOn(c);
  }
  return Ms.statusAvail(c);
}

/// Цена к показу: у включённого, у «входит в сферу» и у «привязано» цены нет; нет цены сервера — «Цена уточняется».
String? mpPriceLabel(BuildContext c, MpModuleView v, String currency) {
  if (v.soon || v.linked || v.on || v.included) return null;
  if (v.featureMode == 'builtin' || v.featureMode == 'service' || v.featureMode == 'capability') return null;
  final m = v.monthly;
  return m == null ? Ms.priceTbd(c) : Ms.priceMonth(c, m, currency);
}

class MarketplaceScreen extends ConsumerStatefulWidget {
  /// 2026-11-27 (доступы советника): ключ или название функции (?module=…) — страница функции открывается сразу после загрузки списка
  /// («открой Маркетплейс → бот-продавец»); не нашлась — название подставляется в поиск.
  final String? openModule;
  const MarketplaceScreen({super.key, this.openModule});
  @override
  ConsumerState<MarketplaceScreen> createState() => _MarketplaceScreenState();
}

enum _Filter { all, on, available }

class _MarketplaceScreenState extends ConsumerState<MarketplaceScreen> {
  final _find = TextEditingController();
  _Filter _filter = _Filter.all;
  int _tab = 0;
  bool _moduleOpened = false;

  @override
  void dispose() {
    _find.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(sessionProvider);
    // кассиру раздел закрыт (и в маршруте, см. Session.canRoute) — тут запасная проверка
    if (s != null && !s.isAdmin) {
      return Scaffold(
        appBar: ScreenTitle(Ms.title(context)),
        body: EmptyState(icon: Icons.lock_outline_rounded, text: Ms.describe(context, const MpChangeResult(MpChange.forbidden))),
      );
    }
    final st = ref.watch(marketplaceProvider);
    return Scaffold(
      appBar: ScreenTitle(Ms.title(context), subtitle: s?.companyName),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
            child: SegmentedButton<int>(
              showSelectedIcon: false,
              style: SegmentedButton.styleFrom(minimumSize: const Size(0, 48)),
              segments: [
                ButtonSegment(value: 0, label: Text(Ms.tabFeatures(context), maxLines: 1, overflow: TextOverflow.ellipsis)),
                ButtonSegment(value: 1, label: Text(Ms.tabTariffs(context), maxLines: 1, overflow: TextOverflow.ellipsis)),
              ],
              selected: {_tab},
              onSelectionChanged: (v) => setState(() => _tab = v.first),
            ),
          ),
          Expanded(child: _tab == 0 ? _features(context, st) : MpTariffsView(state: st)),
        ],
      ),
    );
  }

  Widget _features(BuildContext context, MpState st) {
    final p = context.pal;
    final lang = mpLang(context);
    final snap = st.snap;
    if (st.loading && snap == null) return const Center(child: CircularProgressIndicator());
    final views = snap == null ? <MpModuleView>[] : buildModuleViews(snap, st.content, lang, features: ServerCapabilities.featureSet);
    if (snap == null || (views.isEmpty && snap.offline)) {
      return EmptyState(icon: Icons.cloud_off_rounded, text: Ms.loadFail(context), actionLabel: Ms.retry(context), onAction: () => ref.read(marketplaceProvider.notifier).load());
    }
    final q = _find.text.trim().toLowerCase();
    bool match(MpModuleView v) {
      if (_filter == _Filter.on && !v.on) return false;
      if (_filter == _Filter.available && (v.on || v.soon)) return false;
      if (q.isEmpty) return true;
      return v.name.toLowerCase().contains(q) || (v.lineFor(lang) ?? '').toLowerCase().contains(q);
    }

    final want = widget.openModule?.trim() ?? '';
    if (want.isNotEmpty && !_moduleOpened && views.isNotEmpty) {
      _moduleOpened = true;
      final m = findModule(views, want);
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        if (m.view != null) {
          Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => MpModuleScreen(id: m.view!.id)));
        } else {
          setState(() => _find.text = want); // не нашлась однозначно — покажем плитки с этим словом
        }
      });
    }
    final shown = views.where(match).toList();
    final mine = companyGroup(snap.company);
    final order = <String>[];
    for (final v in shown) {
      if (!order.contains(v.group)) order.add(v.group);
    }
    order.sort((a, b) => (a == mine ? 0 : 1).compareTo(b == mine ? 0 : 1)); // сортировка устойчивая: порядок сервера сохраняется, «ваша сфера» первой
    final currency = snap.currency;
    return RefreshIndicator(
      onRefresh: () => ref.read(marketplaceProvider.notifier).load(),
      child: ListView(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
        children: [
          if (snap.offline) _Banner(icon: Icons.cloud_off_rounded, text: Ms.offline(context)),
          if (snap.company == null && !snap.offline) _Banner(icon: Icons.info_outline_rounded, text: Ms.stateUnknown(context)),
          TextField(
            controller: _find,
            onChanged: (_) => setState(() {}),
            decoration: InputDecoration(
              hintText: Ms.searchHint(context),
              prefixIcon: const Icon(Icons.search_rounded),
              suffixIcon: _find.text.isEmpty
                  ? null
                  : IconButton(
                      constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
                      icon: const Icon(Icons.close_rounded),
                      onPressed: () => setState(_find.clear),
                    ),
            ),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              for (final (f, label) in [(_Filter.all, Ms.fAll(context)), (_Filter.on, Ms.fOn(context)), (_Filter.available, Ms.fAvail(context))])
                ChoiceChip(
                  label: Text(label),
                  selected: _filter == f,
                  materialTapTargetSize: MaterialTapTargetSize.padded,
                  visualDensity: const VisualDensity(vertical: 1),
                  onSelected: (_) => setState(() => _filter = f),
                ),
            ],
          ),
          const SizedBox(height: 8),
          if (shown.isEmpty) Padding(padding: const EdgeInsets.only(top: 24), child: EmptyState(icon: Icons.search_off_rounded, text: Ms.nothingFound(context))),
          for (final g in order) ...[
            Padding(
              padding: const EdgeInsets.fromLTRB(2, 12, 2, 8),
              child: Text(
                '${st.content.groups[g]?.of(lang) ?? Ms.groupFallback(g)}${g == mine ? ' · ${Ms.yourSphere(context)}' : ''}',
                style: TextStyle(fontSize: 13, fontWeight: FontWeight.w800, color: p.textSoft),
              ),
            ),
            for (final v in shown.where((x) => x.group == g)) Padding(padding: const EdgeInsets.only(bottom: 10), child: MpTile(view: v, currency: currency, lang: lang)),
          ],
        ],
      ),
    );
  }
}

class _Banner extends StatelessWidget {
  final IconData icon;
  final String text;
  const _Banner({required this.icon, required this.text});
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Container(
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 20, color: p.warn),
          const SizedBox(width: 10),
          Expanded(child: Text(text, style: TextStyle(fontSize: 13, height: 1.3, color: p.text))),
        ],
      ),
    );
  }
}

/// Значок функции: цветная плитка с иконкой (свой значок у каждой функции, без «пазла»).
class MpIconTile extends StatelessWidget {
  final MpModuleView view;
  final double size;
  const MpIconTile({super.key, required this.view, this.size = 48});
  @override
  Widget build(BuildContext context) {
    final bg = moduleColor(view.content?.color ?? '', T.brandOrange);
    return ExcludeSemantics(
      child: Container(
        width: size,
        height: size,
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(size * .28)),
        child: Icon(moduleIcon(view.id, key: view.content?.icon ?? ''), color: onTile(bg), size: size * .54),
      ),
    );
  }
}

/// Плашка состояния: «Подключён» (зелёная), «Доступен» (рамка), «Скоро» (янтарная).
class MpStatusChip extends StatelessWidget {
  final MpModuleView view;
  const MpStatusChip({super.key, required this.view});
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    final (bg, fg, icon) = switch (view.status) {
      MpStatus.on => (p.successSoft, p.success, Icons.check_circle_rounded),
      MpStatus.soon => (p.warnSoft, p.warn, Icons.schedule_rounded),
      MpStatus.available => (p.soft, p.textSoft, null as IconData?),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(20), border: view.status == MpStatus.available ? Border.all(color: p.border) : null),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[Icon(icon, size: 14, color: fg), const SizedBox(width: 4)],
          Flexible(child: Text(mpStatusLabel(context, view), style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: fg))),
        ],
      ),
    );
  }
}

/// Плитка функции: значок, название, строка «что делает», состояние и цена с сервера.
class MpTile extends StatelessWidget {
  final MpModuleView view;
  final String currency;
  final String lang;
  const MpTile({super.key, required this.view, required this.currency, required this.lang});

  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    final line = view.lineFor(lang);
    final price = mpPriceLabel(context, view, currency);
    return Panel(
      onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => MpModuleScreen(id: view.id))),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          MpIconTile(view: view),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(view.name, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800, height: 1.25)),
                if (line != null) ...[
                  const SizedBox(height: 3),
                  Text(line, style: TextStyle(fontSize: 12.5, height: 1.35, color: p.textSoft)),
                ],
                const SizedBox(height: 8),
                Wrap(
                  spacing: 10,
                  runSpacing: 6,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    MpStatusChip(view: view),
                    if (price != null) Text(price, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: p.price)),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
