import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/sale/catalog_repository.dart';
import '../api/api_client.dart';
import '../providers.dart';
import '../util/ui.dart';

/// 2026-10-29 (аварийный аудит мобилки, п. 5): плашка «Нет связи · данные от ЧЧ:ММ». Следит за [ApiClient.link]: появляется,
/// когда запрос не дошёл до сервера, и исчезает сама, как только сервер ответил (раньше плашка жила только на главной и
/// обновлялась лишь при следующем обновлении смены). [force] — показать, даже если связь по последнему запросу есть (главная:
/// смена взята из памяти). [catalog] — время данных брать из каталога телефона («Остатки на ЧЧ:ММ»); иначе [dataAt].
class LinkBanner extends ConsumerWidget {
  final DateTime? dataAt;
  final bool force;
  final bool catalog;
  final String? detail;
  final EdgeInsetsGeometry margin;
  const LinkBanner({super.key, this.dataAt, this.force = false, this.catalog = false, this.detail, this.margin = EdgeInsets.zero});

  /// Когда каталог телефона в последний раз сверялся с сервером (лента или полная загрузка); старые версии — syncedAt.
  static Future<DateTime?> catalogAt(WidgetRef ref) async {
    final db = ref.read(databaseProvider);
    final raw = await db.getSetting('catalog.checkedAt') ?? await db.getSetting('catalog.syncedAt');
    return raw == null ? null : DateTime.tryParse(raw)?.toLocal();
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(apiProvider);
    final link = ValueListenableBuilder<LinkStatus>(
      valueListenable: api.link,
      builder: (c, st, _) {
        if (!st.offline && !force) return const SizedBox.shrink();
        if (!catalog) return _box(c, dataAt, detail);
        return FutureBuilder<DateTime?>(
          future: catalogAt(ref),
          builder: (c, snap) => _box(c, null, snap.data == null ? detail : c.l.stockAsOf(_hm(c, snap.data!))),
        );
      },
    );
    // 2026-11-05 (аудит №4, М6-07): сбитые часы телефона — плашка «Время на телефоне неверное» (продажи идут по времени сервера)
    final clock = ValueListenableBuilder<bool>(
      valueListenable: ServerClock.wrong,
      builder: (c, bad, _) => bad ? _clockBox(c) : const SizedBox.shrink(),
    );
    // 2026-10-31 («в мобилке не отображаются все товары»): под плашкой связи — ход загрузки каталога («Загружено N из M»)
    if (!catalog) return Column(mainAxisSize: MainAxisSize.min, children: [link, clock]);
    return Column(mainAxisSize: MainAxisSize.min, children: [link, clock, CatalogLoadBanner(margin: margin)]);
  }

  Widget _clockBox(BuildContext c) {
    final p = c.pal;
    return Padding(
      padding: margin,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
        child: Row(
          children: [
            Icon(Icons.schedule_rounded, color: p.warn, size: 18),
            const SizedBox(width: 8),
            Expanded(child: Text(c.l.phoneClockWrong, style: TextStyle(color: p.warn, fontSize: 12.5, fontWeight: FontWeight.w700))),
          ],
        ),
      ),
    );
  }

  static String _hm(BuildContext c, DateTime t) => TimeOfDay.fromDateTime(t).format(c);

  Widget _box(BuildContext c, DateTime? at, String? sub) {
    final l = c.l;
    final p = c.pal;
    return Padding(
      padding: margin,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Icons.wifi_off_rounded, color: p.warn, size: 18),
            const SizedBox(width: 8),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(at == null ? l.linkOffline : l.linkOfflineSince(_hm(c, at)), style: TextStyle(color: p.warn, fontSize: 12.5, fontWeight: FontWeight.w700)),
                  if (sub != null && sub.isNotEmpty) Text(sub, style: TextStyle(color: p.warn, fontSize: 12)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// 2026-10-31 («в мобилке не отображаются все товары»): пока каталог догружается — «Загружаем каталог… N из M» с полосой хода;
/// загрузка оборвалась и на телефоне не все товары — «Загружено N из M …» и кнопка «Обновить каталог» (продолжает с места обрыва).
/// Каталог полный — плашки нет.
class CatalogLoadBanner extends ConsumerStatefulWidget {
  final EdgeInsetsGeometry margin;
  const CatalogLoadBanner({super.key, this.margin = EdgeInsets.zero});
  @override
  ConsumerState<CatalogLoadBanner> createState() => _CatalogLoadBannerState();
}

class _CatalogLoadBannerState extends ConsumerState<CatalogLoadBanner> {
  @override
  void initState() {
    super.initState();
    // после запуска приложения, пока синхронизация не отработала, состояние берётся из настроек телефона
    ref.read(catalogRepoProvider).restoreProgress();
  }

  Future<void> _refresh() async {
    final repo = ref.read(catalogRepoProvider);
    if (repo.progress.value.running) return;
    try {
      await repo.sync();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final repo = ref.watch(catalogRepoProvider);
    return ValueListenableBuilder<CatalogProgress>(
      valueListenable: repo.progress,
      builder: (c, st, _) {
        if (!st.running && !st.incomplete) return const SizedBox.shrink();
        final l = c.l;
        final p = c.pal;
        final text = st.running
            ? (st.total > 0 ? l.catalogLoadingProgress(st.loaded, st.total) : l.catalogLoadingNoTotal(st.loaded))
            : (st.total > 0 ? l.catalogIncomplete(st.loaded, st.total) : l.catalogIncompleteNoTotal);
        return Padding(
          padding: widget.margin,
          child: Container(
            width: double.infinity,
            padding: const EdgeInsets.fromLTRB(12, 10, 8, 10),
            decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (st.running)
                      const Padding(padding: EdgeInsets.only(top: 1), child: SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)))
                    else
                      Icon(Icons.sync_problem_rounded, color: p.warn, size: 18),
                    const SizedBox(width: 8),
                    Expanded(child: Text(text, style: TextStyle(color: p.warn, fontSize: 12.5, fontWeight: FontWeight.w700))),
                  ],
                ),
                if (st.running && st.total > 0)
                  Padding(
                    padding: const EdgeInsets.only(top: 8, right: 4),
                    child: LinearProgressIndicator(value: (st.loaded / st.total).clamp(0.0, 1.0), minHeight: 4, borderRadius: BorderRadius.circular(2)),
                  ),
                if (!st.running)
                  Align(
                    alignment: Alignment.centerRight,
                    child: TextButton.icon(onPressed: _refresh, icon: const Icon(Icons.refresh_rounded, size: 18), label: Text(l.catalogRefresh)),
                  ),
              ],
            ),
          ),
        );
      },
    );
  }
}
