import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

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
    return ValueListenableBuilder<LinkStatus>(
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
