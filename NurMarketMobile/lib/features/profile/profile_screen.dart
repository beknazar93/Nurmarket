import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/ai_access.dart';
import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/link/kassa_link.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/brand_mark.dart';
import '../../core/theme/tokens.dart';
import '../../core/tester/tester_mode.dart';
import '../../core/util/ui.dart';
import '../sale/piece_sale_mode.dart';
import '../admin/advisor.dart';
import '../admin/ai_access_ui.dart';
import 'kassa_link_screen.dart';
import 'printer_settings.dart';

/// Профиль (Т19): касса, язык, тёмная тема, работа без интернета (очередь), выход.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  static const languages = {'ru': 'Русский', 'ky': 'Кыргызча', 'en': 'English', 'tr': 'Türkçe', 'uz': "O'zbekcha"};

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l;
    final pal = context.pal;
    final s = ref.watch(sessionProvider);
    final prefs = ref.watch(prefsProvider);
    final pending = ref.watch(pendingCountProvider).value ?? 0;
    // 2026-10-26 (владелец: ИИ — не больше 2 сотрудникам): правило «кому разрешён ИИ» — статус рядом с «Настройкой ИИ».
    final aiPolicy = ref.watch(aiPolicyProvider).value;
    Widget item(IconData icon, String title, {String? sub, Widget? trailing, VoidCallback? onTap}) => ListTile(
      leading: Container(
        width: 36,
        height: 36,
        decoration: BoxDecoration(color: pal.window, borderRadius: BorderRadius.circular(10)),
        child: Icon(icon, size: 20),
      ),
      title: Text(title, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14.5)),
      subtitle: sub == null ? null : Text(sub, style: TextStyle(fontSize: 12, color: pal.textSoft)),
      trailing: trailing,
      onTap: onTap,
    );
    return Scaffold(
      appBar: ScreenTitle(l.profileTitle), // 2026-10-26 (П2): у экрана был пустой заголовок
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        children: [
          Row(
            children: [
              CircleAvatar(
                radius: 28,
                backgroundColor: T.accent,
                child: Text(
                  initials(s?.userName ?? '?').toUpperCase(),
                  style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 18),
                ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(s?.userName ?? '', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                    Text(
                      [s?.companyName, s?.role, s?.cashboxName].whereType<String>().where((e) => e.isNotEmpty).join(' · '),
                      style: TextStyle(fontSize: 12.5, color: pal.textSoft),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          Panel(
            padding: EdgeInsets.zero,
            child: Column(
              children: [
                item(
                  Icons.point_of_sale_rounded,
                  l.cashbox,
                  sub: s?.cashboxName,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/cashbox'),
                ),
                // 2026-10-20, владелец: «исправь подключение к принтерам» — вместо «в следующем обновлении» настройка печати.
                item(
                  Icons.print_outlined,
                  l.receiptPrinter,
                  sub: l.prnSub,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const PrinterSettingsScreen())),
                ),
                // 2026-10-27: «Связь с кассой» — сопряжение телефона с кассой по QR (сканер для кассы). Только в мобилке.
                if (KassaLink.supported)
                  ListenableBuilder(
                    listenable: KassaLink.instance,
                    builder: (context, _) {
                      final link = KassaLink.instance;
                      if (!link.loaded) link.ensureLoaded();
                      return item(
                        Icons.cast_connected_rounded,
                        l.klTitle,
                        sub: !link.isPaired ? l.klProfileOff : [if (link.kassaName.isNotEmpty) l.klKassaName(link.kassaName), link.status.text(l)].join(' · '),
                        trailing: const Icon(Icons.chevron_right_rounded),
                        onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const KassaLinkScreen())),
                      );
                    },
                  ),
              ],
            ),
          ),
          const SizedBox(height: 12),
          const AiAccessBanner(), // 2026-10-26: «Рекомендуем ограничить ИИ двумя сотрудниками» — только владельцу и пока список не задан
          Panel(
            padding: EdgeInsets.zero,
            child: Column(
              children: [
                item(
                  Icons.language_rounded,
                  l.language,
                  trailing: Text(languages[prefs.locale.languageCode] ?? '', style: TextStyle(color: pal.textSoft)),
                  onTap: () async {
                    final code = await showModalBottomSheet<String>(
                      context: context,
                      builder: (c) => SafeArea(
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            for (final e in languages.entries)
                              ListTile(
                                title: Text(e.value),
                                trailing: e.key == prefs.locale.languageCode ? const Icon(Icons.check_rounded) : null,
                                onTap: () => Navigator.pop(c, e.key),
                              ),
                          ],
                        ),
                      ),
                    );
                    if (code != null) ref.read(prefsProvider.notifier).setLocale(code);
                  },
                ),
                // 13.10, макет С5: магазин одежды — размер и цвет при продаже.
                item(
                  Icons.checkroom_rounded,
                  l.clothingMode,
                  sub: l.clothingModeSub,
                  // 2026-10-27 (аудит мобилки, Н-5): у переключателя своя подпись для TalkBack (раньше читалось только «выключено»)
                  trailing: Semantics(label: l.clothingMode, child: Switch(value: prefs.clothing, onChanged: (v) => ref.read(prefsProvider.notifier).setClothing(v))),
                ),
                // 2026-10-27 (ревью М-6): быстро выключить продажу штуки из упаковки, если сервер её не понимает
                item(
                  Icons.view_in_ar_rounded,
                  l.pieceSaleMode,
                  sub: l.pieceSaleModeSub,
                  trailing: Semantics(
                    label: l.pieceSaleMode,
                    child: Switch(key: const Key('pieceSaleSwitch'), value: ref.watch(pieceSaleProvider), onChanged: (v) => ref.read(pieceSaleProvider.notifier).setOn(v)),
                  ),
                ),
                // 13.10, макет ТМ3: светлая, тёмная или как в системе.
                // 14.10, владелец: «Нур Советник — настройка и добавление ИИ».
                item(
                  Icons.auto_awesome_rounded,
                  l.aiSettings,
                  sub: aiPolicy == null ? null : AiAccess.statusText(l, s, aiPolicy),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiSettingsScreen())),
                ),
                // 2026-10-26: кому разрешён ИИ (не больше 2 сотрудников) — задаёт владелец или администратор.
                if (AiAccess.canManage(s))
                  item(
                    Icons.groups_2_outlined,
                    l.aiAccTitle,
                    sub: aiPolicy == null
                        ? null
                        : aiPolicy.isSet
                        ? l.aiAccCounter(aiPolicy.users!.length, AiAccess.limit)
                        : l.aiAccNotSet,
                    trailing: const Icon(Icons.chevron_right_rounded),
                    onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiAccessScreen())),
                  ),
                // 2026-10-26 (голос-управление, В5): режим помощника и журнал действий ИИ (доступны всем по роли).
                item(
                  Icons.tune_rounded,
                  l.assistantTitle,
                  sub: l.assistantSub,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/ai/settings'),
                ),
                item(
                  Icons.history_rounded,
                  l.aiLogTitle,
                  sub: s != null && s.isAdmin ? l.aiLogSubAll : l.aiLogMineOnly,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/ai/log'),
                ),
                item(Icons.dark_mode_outlined, l.appearance),
                // 14.10, владелец: «смена темы по времени» — четвёртый вариант и часы тёмной темы.
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                  child: Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (final (v, label) in [('light', l.themeLight), ('dark', l.themeDark), ('system', l.themeSystem), ('auto', l.themeAuto)])
                        ChoiceChip(label: Text(label), selected: prefs.mode == v, onSelected: (_) => ref.read(prefsProvider.notifier).setMode(v)),
                    ],
                  ),
                ),
                if (prefs.auto)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                    child: Row(
                      children: [
                        Text(l.darkFrom),
                        const SizedBox(width: 6),
                        OutlinedButton(
                          onPressed: () async {
                            final t = await showTimePicker(
                              context: context,
                              initialTime: TimeOfDay(hour: prefs.darkFrom ~/ 60, minute: prefs.darkFrom % 60),
                            );
                            if (t != null) await ref.read(prefsProvider.notifier).setDarkHours(from: t.hour * 60 + t.minute);
                          },
                          child: Text(PrefsController.hhmm(prefs.darkFrom)),
                        ),
                        const SizedBox(width: 6),
                        Text(l.darkTo),
                        const SizedBox(width: 6),
                        OutlinedButton(
                          onPressed: () async {
                            final t = await showTimePicker(
                              context: context,
                              initialTime: TimeOfDay(hour: prefs.darkTo ~/ 60, minute: prefs.darkTo % 60),
                            );
                            if (t != null) await ref.read(prefsProvider.notifier).setDarkHours(to: t.hour * 60 + t.minute);
                          },
                          child: Text(PrefsController.hhmm(prefs.darkTo)),
                        ),
                      ],
                    ),
                  ),
                item(
                  Icons.wifi_off_rounded,
                  l.offlineMode,
                  sub: l.waitingToSend(pending),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/sync'),
                ),
                // 2026-10-27: встроенный справочник готовых наименований NurCRM — сколько товаров, от какой выгрузки, сколько добавлено
                item(
                  Icons.menu_book_outlined,
                  l.globalCatalogTitle,
                  sub: ref
                      .watch(globalCatalogStatusProvider)
                      .when(
                        data: (st) => st == null ? l.globalCatalogOff : l.globalCatalogStatus(groupThousands(st.count), niceCatalogDate(st.exportedAt), '${st.added}'),
                        loading: () => null,
                        error: (_, _) => l.globalCatalogOff,
                      ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 24),
          OutlinedButton(
            style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), foregroundColor: pal.danger),
            onPressed: () async {
              if (pending > 0) {
                final ok = await showDialog<bool>(
                  context: context,
                  builder: (c) => AlertDialog(
                    content: Text(l.logoutPending(pending)),
                    actions: [
                      TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                      TextButton(
                        onPressed: () => Navigator.pop(c, true),
                        child: Text(l.logout, style: TextStyle(color: pal.danger)),
                      ),
                    ],
                  ),
                );
                if (ok != true) return;
              }
              await ref.read(sessionProvider.notifier).logout();
              if (context.mounted) context.go('/login');
            },
            child: Text(l.logout),
          ),
          // 2026-10-22, брендбук NUR CRM: логотип внизу профиля («о приложении»).
          Padding(
            padding: const EdgeInsets.only(top: 28, bottom: 8),
            // 2026-10-27: 5 быстрых нажатий на логотип — «Режим тестировщика» (только тестовая сборка или по коду)
            child: Center(child: TesterTap(child: BrandLogo(width: 130, variant: pal.dark ? BrandVariant.dark : BrandVariant.light))),
          ),
        ],
      ),
    );
  }
}

/// Синхронизация (О10): что ждёт отправки, что не принято сервером, «Отправить сейчас».
class SyncScreen extends ConsumerStatefulWidget {
  const SyncScreen({super.key});
  @override
  ConsumerState<SyncScreen> createState() => _SyncScreenState();
}

class _SyncScreenState extends ConsumerState<SyncScreen> {
  bool _busy = false;
  String? _catalogAt;
  String? _legacyOwner;

  @override
  void initState() {
    super.initState();
    ref.read(databaseProvider).getSetting('catalog.syncedAt').then((v) => mounted ? setState(() => _catalogAt = v) : null);
    ref.read(databaseProvider).getSetting(OutboxMeta.legacyOwnerSetting).then((v) => mounted ? setState(() => _legacyOwner = v) : null);
  }

  Future<void> _now() async {
    setState(() => _busy = true);
    await ref.read(syncProvider).runOnce(force: true);
    final at = await ref.read(databaseProvider).getSetting('catalog.syncedAt');
    if (mounted) {
      setState(() {
        _busy = false;
        _catalogAt = at;
      });
      final err = ref.read(syncProvider).lastError;
      if (err != null) toast(context, err, error: true);
    }
  }

  String _kind(BuildContext context, String k) => switch (k) {
    'sale' => context.l.kindSale,
    'receive' => context.l.receiving,
    'audit' => context.l.audit,
    _ => k,
  };

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final at = DateTime.tryParse(_catalogAt ?? '');
    return Scaffold(
      appBar: ScreenTitle(
        l.sync,
        subtitle: at == null
            ? null
            : l.catalogUpdated('${at.day.toString().padLeft(2, '0')}.${at.month.toString().padLeft(2, '0')} ${TimeOfDay.fromDateTime(at).format(context)}'),
      ),
      body: StreamBuilder<List<OutboxData>>(
        stream: ref.read(databaseProvider).watchOutbox(),
        builder: (c, snap) {
          final rows = (snap.data ?? []).where((r) => r.status != OutboxMeta.dropped).take(100).toList();
          final me = OutboxMeta.ownerOf(ref.read(sessionProvider));
          if (rows.isEmpty) {
            return Center(
              child: Text(l.nothingToSend, style: TextStyle(color: pal.textSoft)),
            );
          }
          return ListView.separated(
            padding: const EdgeInsets.all(16),
            itemCount: rows.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (c, i) {
              final r = rows[i];
              // 2026-10-22 (мобилка, деньги, К-6): запись другого аккаунта — ждёт входа своим логином, не отправляется.
              final foreignOwner = r.status == 'sent' || OutboxMeta.belongsTo(r, me, legacyOwner: _legacyOwner) ? null : (OutboxMeta.ownerOfRow(r) ?? _legacyOwner ?? '');
              final (color, label) = switch (r.status) {
                // 2026-10-26 (живой аудит, П1/П2, С-7): цвет текста статуса — из палитры (в тёмной теме светлее, контраст AA)
                'sent' => (pal.success, l.sent),
                _ when foreignOwner != null => (pal.warn, l.otherAccount),
                'failed' => (pal.danger, l.notAccepted),
                _ => (pal.warn, l.waitsForNetwork),
              };
              return Panel(
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '${_kind(context, r.kind)}${r.serverNumber != null ? ' №${r.serverNumber}' : ''}',
                            style: const TextStyle(fontWeight: FontWeight.w700),
                          ),
                          Text(
                            r.summary,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(fontSize: 12.5, color: pal.textSoft),
                          ),
                          if (foreignOwner != null) Text(l.otherAccountHint(foreignOwner), style: TextStyle(fontSize: 12, color: color)),
                          if (r.status != 'sent' && r.lastError != null) Text(r.lastError!, style: TextStyle(fontSize: 12, color: color)),
                        ],
                      ),
                    ),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        if (r.kind == 'sale') Text('${money(r.total)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800)),
                        Text(
                          label,
                          style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: color),
                        ),
                        if (r.status == 'failed' && foreignOwner == null)
                          TextButton(
                            onPressed: () async {
                              await ref.read(databaseProvider).retryFailed(r.id);
                              await _now();
                            },
                            child: Text(l.sendAgain),
                          ),
                        // 2026-10-22 (мобилка, деньги): не принятое сервером можно снять — не уйдёт и не держит смену.
                        if (r.status == 'failed' && foreignOwner == null)
                          TextButton(
                            style: TextButton.styleFrom(foregroundColor: pal.danger),
                            onPressed: () async {
                              final ok = await showDialog<bool>(
                                context: context,
                                builder: (c) => AlertDialog(
                                  content: Text(r.kind == 'sale' ? l.dropRowAsk : l.dropRowAskStock),
                                  actions: [
                                    TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                                    FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.dropRow)),
                                  ],
                                ),
                              );
                              if (ok == true) await OutboxMeta.drop(ref.read(databaseProvider), r);
                            },
                            child: Text(l.dropRow),
                          ),
                      ],
                    ),
                  ],
                ),
              );
            },
          );
        },
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
          child: FilledButton(
            onPressed: _busy ? null : _now,
            child: _busy ? const CircularProgressIndicator(color: T.navy) : Text(l.sendNow),
          ),
        ),
      ),
    );
  }
}
