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
    for (var i = 0; i < shots.length; i++) {
      final f = File('${dir.path}/${productId}_$i.png');
      await f.writeAsBytes(await shrink(shots[i]));
      paths.add(f.path);
    }
    index[productId] = paths;
    await db.setSetting(_indexKey, jsonEncode(index));
  }

  static Future<Set<String>> trainedIds(AppDatabase db) async => (await _index(db)).keys.toSet();

  static Future<List<Uint8List>> load(AppDatabase db, String productId) async {
    final out = <Uint8List>[];
    for (final p in (await _index(db))[productId] ?? const <String>[]) {
      try {
        out.add(await File(p).readAsBytes());
      } catch (_) {}
    }
    return out;
  }

  /// Фото товара с сервера как эталон (для товаров, обученных на другом телефоне).
  static Future<Uint8List?> fromUrl(String url) async {
    try {
      final r = await Dio(BaseOptions(connectTimeout: const Duration(seconds: 8), receiveTimeout: const Duration(seconds: 15)))
          .get<List<int>>(url, options: Options(responseType: ResponseType.bytes));
      final bytes = r.data;
      return bytes == null ? null : await shrink(Uint8List.fromList(bytes));
    } catch (_) {
      return null;
    }
  }
}
