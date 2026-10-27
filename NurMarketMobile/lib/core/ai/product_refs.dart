import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:dio/dio.dart';
import 'package:path_provider/path_provider.dart';

import '../db/database.dart';

/// 14.10, владелец: «при добавлении товара умной камерой — 3 фото (справа, слева, сверху) и штрихкод, а потом при скане
/// умной камерой для корзины сразу находит товар точно». Эталонные снимки товара — на телефоне (папка refs, уменьшенные
/// до 384 px), список — в настройке refs.index. При продаже ИИ сравнивает кадр с эталонами (VisionService.identify).
/// На других телефонах эталоном служит фото товара в NurCRM (первые снимки уходят фото товара, если его не было).
class ProductRefs {
  static const _indexKey = 'refs.index';

  /// Только для тестов: папка вместо папки приложения.
  static Directory? testDirectory;

  static Future<Directory> _dir() async {
    final d = Directory('${(testDirectory ?? await getApplicationDocumentsDirectory()).path}/refs');
    if (!await d.exists()) await d.create(recursive: true);
    return d;
  }

  static Future<Map<String, List<String>>> _index(AppDatabase db) async {
    final raw = await db.getSetting(_indexKey);
    if (raw == null || raw.isEmpty) return {};
    try {
      return (jsonDecode(raw) as Map).map((k, v) => MapEntry('$k', [for (final x in v as List) '$x']));
    } catch (_) {
      return {};
    }
  }

  /// Уменьшить снимок до 384 px по ширине (PNG) — эталоны лёгкие, запрос к ИИ быстрый.
  static Future<Uint8List> shrink(Uint8List image, {int width = 384}) async {
    final codec = await ui.instantiateImageCodec(image, targetWidth: width);
    final frame = await codec.getNextFrame();
    final data = await frame.image.toByteData(format: ui.ImageByteFormat.png);
    return data!.buffer.asUint8List();
  }

  /// 2026-10-22 (аудит У-4): эталон — сам снимок камеры (JPEG 640×480, 40–80 КБ), а не PNG 384 px (150–300 КБ):
  /// запрос сверки с 6 товарами по 3 снимка был 6–12 МБ и на мобильной сети не успевал. Большой снимок или не JPEG —
  /// уменьшается до 320 px.
  static bool _isJpeg(Uint8List b) => b.length > 2 && b[0] == 0xFF && b[1] == 0xD8;

  static Future<Uint8List> compact(Uint8List image) async => _isJpeg(image) && image.length <= 150 * 1024 ? image : await shrink(image, width: 320);

  /// Сколько снимков товара отправлять в сверку (лучшие первые: справа, слева, сверху).
  static const maxPerProduct = 3;

  static Future<void> save(AppDatabase db, String productId, List<Uint8List> shots) async {
    if (productId.isEmpty || shots.isEmpty) return;
    final dir = await _dir();
    final index = await _index(db);
    for (final old in index[productId] ?? const <String>[]) {
      try {
        await File(old).delete();
      } catch (_) {}
    }
    final paths = <String>[];
    for (var i = 0; i < shots.length && i < maxPerProduct; i++) {
      final bytes = await compact(shots[i]);
      final f = File('${dir.path}/${productId}_$i.${_isJpeg(bytes) ? 'jpg' : 'png'}');
      await f.writeAsBytes(bytes);
      paths.add(f.path);
    }
    index[productId] = paths;
    await db.setSetting(_indexKey, jsonEncode(index));
  }

  // 2026-10-27 (мобилка, М3): владелец — «в редактировании товара нет возможности обучения умной камеры». В карточке товара
  // эталоны видны по одному (3 места) и снимаются/убираются по отдельности. Место = позиция в списке refs.index; пустое
  // место — '' (старый [load] такие пути пропускает, [save] их стирает без ошибки), схема и ключ настройки не менялись.

  /// Сколько мест под эталоны в карточке товара (столько же ходит в сверку при продаже).
  static const slotCount = maxPerProduct;

  static bool _filled(String path) => path.isNotEmpty;

  /// Снимки по местам; пустое место или потерянный файл — null.
  static Future<List<Uint8List?>> slots(AppDatabase db, String productId) async {
    final paths = (await _index(db))[productId] ?? const <String>[];
    final out = <Uint8List?>[];
    for (var i = 0; i < slotCount; i++) {
      Uint8List? bytes;
      if (i < paths.length && _filled(paths[i])) {
        try {
          bytes = await File(paths[i]).readAsBytes();
        } catch (_) {}
      }
      out.add(bytes);
    }
    return out;
  }

  /// Сколько эталонов реально лежит на телефоне.
  static Future<int> count(AppDatabase db, String productId) async => (await slots(db, productId)).where((s) => s != null).length;

  /// Записать снимок в место [slot] (0…slotCount-1); прежний снимок этого места заменяется.
  static Future<void> setSlot(AppDatabase db, String productId, int slot, Uint8List shot) async {
    if (productId.isEmpty || slot < 0 || slot >= slotCount) return;
    final dir = await _dir();
    final index = await _index(db);
    final paths = [...(index[productId] ?? const <String>[])];
    while (paths.length < slotCount) {
      paths.add('');
    }
    final old = paths[slot];
    final bytes = await compact(shot);
    final f = File('${dir.path}/${productId}_$slot.${_isJpeg(bytes) ? 'jpg' : 'png'}');
    await f.writeAsBytes(bytes);
    if (_filled(old) && old != f.path) {
      try {
        await File(old).delete();
      } catch (_) {}
    }
    paths[slot] = f.path;
    index[productId] = paths;
    await db.setSetting(_indexKey, jsonEncode(index));
  }

  /// Убрать эталон из места [slot]; когда мест не осталось — товар считается необученным.
  static Future<void> clearSlot(AppDatabase db, String productId, int slot) async {
    final index = await _index(db);
    final paths = [...(index[productId] ?? const <String>[])];
    if (slot < 0 || slot >= paths.length) return;
    if (_filled(paths[slot])) {
      try {
        await File(paths[slot]).delete();
      } catch (_) {}
    }
    paths[slot] = '';
    if (paths.every((p) => !_filled(p))) {
      index.remove(productId);
    } else {
      index[productId] = paths;
    }
    await db.setSetting(_indexKey, jsonEncode(index));
  }

  /// Убрать все эталоны товара с телефона (фото в NurCRM не трогает).
  static Future<void> clearAll(AppDatabase db, String productId) async {
    final index = await _index(db);
    final paths = index.remove(productId);
    if (paths == null) return;
    for (final old in paths) {
      if (!_filled(old)) continue;
      try {
        await File(old).delete();
      } catch (_) {}
    }
    await db.setSetting(_indexKey, jsonEncode(index));
  }

  static Future<Set<String>> trainedIds(AppDatabase db) async => (await _index(db)).keys.toSet();

  static Future<List<Uint8List>> load(AppDatabase db, String productId) async {
    final out = <Uint8List>[];
    for (final p in ((await _index(db))[productId] ?? const <String>[]).take(maxPerProduct)) {
      try {
        out.add(await File(p).readAsBytes());
      } catch (_) {}
    }
    return out;
  }

  /// Фото товара с сервера как эталон (для товаров, обученных на другом телефоне).
  /// 2026-10-22: сроки под мобильную сеть — 3 + 4 с (было 8 + 15 с на каждое фото), лёгкое фото не перекодируется.
  static final _dio = Dio(BaseOptions(connectTimeout: const Duration(seconds: 3), receiveTimeout: const Duration(seconds: 4)));

  static Future<Uint8List?> fromUrl(String url) async {
    try {
      final r = await _dio.get<List<int>>(url, options: Options(responseType: ResponseType.bytes));
      final bytes = r.data;
      return bytes == null ? null : await compact(Uint8List.fromList(bytes));
    } catch (_) {
      return null;
    }
  }
}
