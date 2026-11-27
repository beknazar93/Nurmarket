import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/server_capabilities.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import 'marketplace_content.dart';
import 'marketplace_data.dart';

/// 2026-11-27 (Маркетплейс мобилки): состояние раздела и сборка плиток. Правила — как в кассе (MarketplaceModules, CompanyModules):
/// модуль включён по ответу сервера; «Входит в вашу сферу» — по default_in профиля; цена — только серверная (public/modules), нет ответа — «Цена уточняется».

/// «Статус ремонта на витрине»: отдельного модуля у сервера нет, он привязан к «Ремонту» — признак repair_status_public в capabilities.features (как в кассе).
const repairStatusId = 'repair_status_site';
const repairStatusFlag = 'repair_status_public';

enum MpStatus { on, available, soon }

/// Одна плитка: модуль сервера + наш контент + состояние компании.
class MpModuleView {
  final String id;
  final String name;
  final String group;
  final double? monthly;
  final int trialDays;
  final MpModuleContent? content;
  final String? serverDescription;
  final bool on;
  final bool included;
  final bool onTrial;
  final DateTime? trialUntil;
  final bool soon;

  /// «Привязано к Ремонту» (нет своей цены и переключателя).
  final bool linked;

  /// Состояние компании неизвестно (ответ модулей не получен): действия недоступны.
  final bool stateKnown;

  /// Карточка функции без серверного модуля: capability / fiscal / builtin / service (null — обычный модуль сервера).
  final String? featureMode;

  /// «Что нужно» для карточки функции.
  final LText? need;
  const MpModuleView({
    required this.id,
    required this.name,
    required this.group,
    required this.monthly,
    required this.trialDays,
    required this.content,
    required this.serverDescription,
    required this.on,
    required this.included,
    required this.onTrial,
    required this.trialUntil,
    required this.soon,
    required this.linked,
    required this.stateKnown,
    this.featureMode,
    this.need,
  });

  MpStatus get status => soon ? MpStatus.soon : (on ? MpStatus.on : MpStatus.available);

  /// Одна строка «что делает» для плитки: наша → описание сервера → null (плитка тогда не рисует пустую строку).
  String? lineFor(String lang) {
    final c = content;
    if (c != null && !c.line.isEmpty) return c.line.of(lang);
    if (c != null && !c.short.isEmpty) return c.short.of(lang);
    final d = serverDescription;
    return d == null || d.trim().isEmpty ? null : d;
  }

  /// Можно ли включить / выключить здесь.
  bool get canToggle => stateKnown && !soon && !linked && featureMode == null;
  bool get trialAvailable => canToggle && !on && !included && trialDays > 0;

  bool get isFeatureCard => featureMode != null;
}

/// Название на языке [lang]: сервер на этом языке → наше (5 языков) → русское сервера → наше русское → код.
String pickName(String lang, Map<String, String> server, LText? ours, String fallback) {
  final s = server[lang];
  if (s != null && s.isNotEmpty) return s;
  if (ours != null && ours.has(lang)) return ours.of(lang);
  return server['ru'] ?? server['en'] ?? (ours == null || ours.isEmpty ? fallback : ours.of('ru'));
}

/// Плитки: все модули сервера (public/modules + catalog компании), порядок сервера. Модуль без записи контента остаётся (с описанием сервера или без строки), не теряется.
List<MpModuleView> buildModuleViews(MpSnapshot snap, MarketplaceContent content, String lang, {Set<String> features = const {}}) {
  final company = snap.company;
  final ids = <String>[];
  final pub = {for (final m in snap.modules) m.id: m};
  for (final m in snap.modules) {
    ids.add(m.id);
  }
  if (company != null) {
    for (final id in company.catalogNames.keys) {
      if (!ids.contains(id)) ids.add(id);
    }
  }
  if (ids.isNotEmpty && !ids.contains(repairStatusId)) ids.add(repairStatusId);
  final views = <MpModuleView>[];
  for (final id in ids) {
    final p = pub[id];
    final c = content.module(id);
    final linked = id == repairStatusId;
    final soon = linked && !features.contains(repairStatusFlag);
    final names = <String, String>{...?company?.catalogNames[id], ...?p?.names};
    final on = linked ? features.contains(repairStatusFlag) : (company?.isOn(id) ?? false);
    final item = company?.items[id];
    views.add(
      MpModuleView(
        id: id,
        name: pickName(lang, names, c?.name, id),
        group: (p?.group.isNotEmpty ?? false) ? p!.group : (linked ? 'electronics' : 'other'),
        monthly: linked ? null : p?.monthly,
        trialDays: linked ? 0 : (p?.trialDays ?? 0),
        content: c,
        serverDescription: p?.description,
        on: on,
        included: !linked && (company?.includedIn(id, publicDefaultIn: p?.defaultIn ?? const []) ?? false),
        onTrial: on && (item?.isTrial ?? false),
        trialUntil: item?.trialUntil,
        soon: soon,
        linked: linked,
        stateKnown: company != null,
      ),
    );
  }
  // Карточки функций, которых сервер модулями не отдаёт: показываем, пока сервер не заведёт модуль с таким кодом (тогда он придёт обычной плиткой выше)
  for (final f in ids.isEmpty ? const <MpFeature>[] : content.features.values) {
    if (ids.contains(f.id)) continue;
    final c = content.module(f.id);
    final capOn = f.mode == 'capability' && f.flag != null && features.contains(f.flag);
    final soon = f.mode == 'capability' && !capOn;
    views.add(
      MpModuleView(
        id: f.id,
        name: pickName(lang, const {}, c?.name, f.id),
        group: 'extra',
        monthly: f.mode == 'fiscal' ? pub[f.id]?.monthly : null,
        trialDays: 0,
        content: c,
        serverDescription: null,
        on: f.mode == 'builtin' || capOn,
        included: false,
        onTrial: false,
        trialUntil: null,
        soon: soon,
        linked: false,
        stateKnown: true,
        featureMode: f.mode,
        need: f.need.isEmpty ? null : f.need,
      ),
    );
  }
  return views;
}

/// Группа сферы компании: group модуля = сфера (grocery, stroymag, bazaar …); профиль компании приводится к тому же имени.
String companyGroup(MpCompany? c) => canonProfile(c?.profile);

class MpState {
  final bool loading;
  final MpSnapshot? snap;
  final MarketplaceContent content;
  final Set<String> busy;

  /// Последнее сообщение для показа (итог действия); экран показывает и сбрасывает.
  final String? message;
  const MpState({this.loading = true, this.snap, this.content = MarketplaceContent.emptyContent, this.busy = const {}, this.message});

  MpState copy({bool? loading, MpSnapshot? snap, MarketplaceContent? content, Set<String>? busy, String? message, bool clearMessage = false}) => MpState(
    loading: loading ?? this.loading,
    snap: snap ?? this.snap,
    content: content ?? this.content,
    busy: busy ?? this.busy,
    message: clearMessage ? null : (message ?? this.message),
  );
}

final marketplaceRepoProvider = Provider<MarketplaceRepository>((ref) {
  final api = ref.watch(apiProvider);
  return MarketplaceRepository(api, ref.watch(databaseProvider), OutboxMeta.ownerOf(ref.watch(sessionProvider)));
});

final marketplaceProvider = NotifierProvider<MarketplaceController, MpState>(MarketplaceController.new);

class MarketplaceController extends Notifier<MpState> {
  @override
  MpState build() {
    Future.microtask(load);
    return const MpState();
  }

  MarketplaceRepository get _repo => ref.read(marketplaceRepoProvider);

  Future<void> load() async {
    state = state.copy(loading: true);
    final content = await MarketplaceContent.load();
    final snap = await _repo.load();
    if (!ref.mounted) return;
    state = state.copy(loading: false, snap: snap, content: content);
  }

  /// Итог действия (для сообщения): null — всё хорошо.
  Future<MpChangeResult> toggle(String id, bool enable) async {
    state = state.copy(busy: {...state.busy, id});
    MpChangeResult r;
    try {
      r = await _repo.change(id, enable: enable, company: state.snap?.company);
    } catch (_) {
      r = const MpChangeResult(MpChange.noConnection);
    }
    await _afterChange(id, r);
    return r;
  }

  Future<MpChangeResult> trial(String id) async {
    state = state.copy(busy: {...state.busy, id});
    MpChangeResult r;
    try {
      r = await _repo.startTrial(id);
    } catch (_) {
      r = const MpChangeResult(MpChange.noConnection);
    }
    await _afterChange(id, r);
    return r;
  }

  Future<void> _afterChange(String id, MpChangeResult r) async {
    var company = r.company;
    if (r.ok || r.status == MpChange.alreadyActive || r.status == MpChange.conflict) {
      company ??= await _repo.reloadCompany();
    }
    if (!ref.mounted) return;
    final snap = state.snap;
    final busy = {...state.busy}..remove(id);
    state = state.copy(
      busy: busy,
      snap: snap == null || company == null
          ? snap
          : MpSnapshot(modules: snap.modules, currency: snap.currency, company: company, tariffs: snap.tariffs, offline: false, at: DateTime.now()),
    );
    if (r.ok) unawaited(_refreshFeatures());
  }

  /// Признаки возможностей сервера зависят от модулей: обновляем их, чтобы остальные экраны перестроились (как «Модули компании» в кассе); сбой тихий.
  Future<void> _refreshFeatures() async {
    try {
      final s = ref.read(sessionProvider);
      await ServerCapabilities.refresh(ref.read(apiProvider), ref.read(databaseProvider), OutboxMeta.ownerOf(s));
    } catch (_) {
      // признаки обновятся при следующем входе в приложение
    }
  }
}
