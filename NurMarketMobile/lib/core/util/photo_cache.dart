import 'dart:io';

import 'package:cached_network_image/cached_network_image.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_cache_manager/flutter_cache_manager.dart';
import 'package:path_provider/path_provider.dart';

/// 2026-10-30 (оптимизация мобилки, п. 12; новая зависимость разрешена владельцем): дисковый кэш фото товаров. Раньше
/// `Image.network` не хранил фото на диске — после выгрузки из памяти и после перезапуска они качались заново (трафик, пустые
/// плитки при листании). Теперь каждое фото скачивается один раз. Лимит: 30 дней без обращения и не более [maxBytes] (200 МБ,
/// самые старые файлы удаляются в [trim]). Очищается при смене аккаунта и выходе (AccountSwitch.wipeAll → [wipe]).
class PhotoCache {
  static const _key = 'nurmarket_photos';
  static const maxBytes = 200 * 1024 * 1024;
  static const stale = Duration(days: 30);

  /// Выключен в тестах и в вебе (там нет sqflite/файлов): тогда фото грузятся как раньше, без диска.
  static bool enabled = !kIsWeb && !Platform.environment.containsKey('FLUTTER_TEST');

  static final CacheManager manager = CacheManager(Config(_key, stalePeriod: stale, maxNrOfCacheObjects: 3000));

  /// Стереть весь кэш фото (смена аккаунта, выход).
  static Future<void> wipe() async {
    if (!enabled) return;
    try {
      await manager.emptyCache();
    } catch (_) {
      // кэш не очистился — файлы всё равно устареют и будут вытеснены
    }
  }

  static DateTime _lastTrim = DateTime.fromMillisecondsSinceEpoch(0);

  /// Не больше [maxBytes]: самые давно изменённые файлы удаляются первыми (записи кэша без файла менеджер сам перекачает).
  /// Не чаще раза в 6 часов, если не [force]. Возвращает число удалённых файлов.
  static Future<int> trim({bool force = false, Directory? dir, int limit = maxBytes}) async {
    if (!force && DateTime.now().difference(_lastTrim) < const Duration(hours: 6)) return 0;
    _lastTrim = DateTime.now();
    try {
      final d = dir ?? Directory('${(await getTemporaryDirectory()).path}${Platform.pathSeparator}$_key');
      if (!await d.exists()) return 0;
      final files = <(File, int, DateTime)>[];
      var total = 0;
      await for (final e in d.list()) {
        if (e is! File) continue;
        final st = await e.stat();
        files.add((e, st.size, st.modified));
        total += st.size;
      }
      if (total <= limit) return 0;
      files.sort((a, b) => a.$3.compareTo(b.$3));
      var removed = 0;
      for (final f in files) {
        if (total <= limit * 0.8) break; // с запасом, чтобы не чистить после каждого фото
        try {
          await f.$1.delete();
          total -= f.$2;
          removed++;
        } catch (_) {}
      }
      return removed;
    } catch (_) {
      return 0;
    }
  }
}

/// Фото по ссылке с дисковым кэшем ([PhotoCache]); без кэша (тесты, веб) — обычный `Image.network`.
/// [cacheWidth] — размер декодирования в память (как раньше у `Image.network`). [placeholder] показывается, пока фото грузится,
/// [error] — если не загрузилось.
class CachedPhoto extends StatelessWidget {
  final String url;
  final BoxFit fit;
  final double? width;
  final double? height;
  final int? cacheWidth;
  final Widget placeholder;
  final Widget error;
  const CachedPhoto({super.key, required this.url, this.fit = BoxFit.cover, this.width, this.height, this.cacheWidth, required this.placeholder, required this.error});

  @override
  Widget build(BuildContext context) {
    if (!PhotoCache.enabled) {
      return Image.network(
        url,
        width: width,
        height: height,
        fit: fit,
        cacheWidth: cacheWidth,
        gaplessPlayback: true,
        errorBuilder: (_, _, _) => error,
        loadingBuilder: (_, w, progress) => progress == null ? w : placeholder,
      );
    }
    return CachedNetworkImage(
      imageUrl: url,
      cacheManager: PhotoCache.manager,
      width: width,
      height: height,
      fit: fit,
      memCacheWidth: cacheWidth,
      fadeInDuration: Duration.zero,
      fadeOutDuration: Duration.zero,
      useOldImageOnUrlChange: true,
      placeholder: (_, _) => placeholder,
      errorWidget: (_, _, _) => error,
    );
  }
}
