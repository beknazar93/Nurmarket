import 'dart:typed_data';

import 'package:dio/dio.dart';

/// 13.10, владелец: «в мобилке добавление товара — фото тоже из интернета». Та же логика, что ProductPhotoFinder кассы:
/// по штрихкоду — Open Food Facts (фото упаковки), по названию — Яндекс.Картинки; фото без слов названия в подписи
/// отбрасываются, магазины выше фотобанков. Ничего не скачивается без выбора человека.
class PhotoFinder {
  static final _http = Dio(
    BaseOptions(
      connectTimeout: const Duration(seconds: 10),
      receiveTimeout: const Duration(seconds: 15),
      headers: {
        'User-Agent': 'Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36',
        'Accept-Language': 'ru,en;q=0.8',
      },
      validateStatus: (_) => true,
    ),
  );

  static Future<List<String>> search(String name, String barcode, {int max = 9}) async {
    final out = <String>[];
    final code = barcode.replaceAll(RegExp(r'\D'), '');
    if (code.length >= 8) {
      for (final host in ['world.openfoodfacts.org', 'ru.openfoodfacts.org']) {
        try {
          final r = await _http.get<Map<String, dynamic>>(
            'https://$host/api/v2/product/$code.json?fields=image_front_url,image_url',
            options: Options(headers: {'User-Agent': 'NurMarket/1.18 (+https://nurcrm.kg)'}),
          );
          final p = r.data?['status'] == 1 ? r.data!['product'] as Map? : null;
          final url = p?['image_front_url'] ?? p?['image_url'];
          if (url is String && url.startsWith('https://')) {
            out.add(url);
            break;
          }
        } catch (_) {
          // нет в базе или нет сети — ищем дальше
        }
      }
    }
    final query = name.trim().isNotEmpty ? name.trim() : code;
    if (query.isNotEmpty) out.addAll(await _yandex(query));
    return out.toSet().take(max).toList();
  }

  static Future<List<String>> _yandex(String query) async {
    final words = query
        .toLowerCase()
        .split(RegExp(r'[^\p{L}\p{N}]+', unicode: true))
        .where((w) => w.length >= 3 && !RegExp(r'^\d+([.,]\d+)?(г|гр|кг|мл|л|шт|g|kg|ml|l)?$').hasMatch(w))
        .map((w) => w.length > 5 ? w.substring(0, 5) : w)
        .toSet();
    try {
      final r = await _http.get<String>(
        'https://yandex.ru/images/search',
        queryParameters: {'text': query},
        options: Options(responseType: ResponseType.plain),
      );
      final html = (r.data ?? '').replaceAll('&quot;', '"').replaceAll('&amp;', '&');
      final scored = <String, int>{};
      for (final m in RegExp(r'"origUrl":"(https://[^"]+)"').allMatches(html)) {
        final image = m.group(1)!.replaceAll(r'\/', '/').replaceAll(r'&', '&');
        if (scored.containsKey(image)) continue;
        final from = (m.start - 700).clamp(0, html.length);
        final to = (m.end + 1500).clamp(0, html.length);
        final about = html.substring(from, to).toLowerCase();
        final hits = words.where(about.contains).length;
        const shops = ['купить', 'магазин', 'ozon', 'market', 'wildberries', 'lavka', 'globus', 'цена'];
        const stocks = ['freepik', 'shutterstock', 'depositphotos', 'istock', 'vecteezy', 'pngtree', 'dreamstime', 'вектор', 'силуэт', 'clipart'];
        final score = hits == 0 && words.isNotEmpty ? 0 : hits * 2 + (shops.any(about.contains) ? 1 : 0) - (stocks.any(about.contains) ? 2 : 0) + 1;
        scored[image] = score;
      }
      final list = scored.entries.where((e) => e.value > 0).toList()..sort((a, b) => b.value.compareTo(a.value));
      return list.map((e) => e.key).toList();
    } catch (_) {
      return [];
    }
  }

  /// Скачать выбранное фото (для отправки в карточку товара). null — не скачалось или это не картинка.
  static Future<Uint8List?> download(String url) async {
    try {
      final r = await _http.get<List<int>>(
        url,
        options: Options(responseType: ResponseType.bytes, headers: {'Accept': 'image/webp,image/jpeg,image/png,image/*;q=0.8'}),
      );
      final type = r.headers.value('content-type') ?? '';
      if ((r.statusCode ?? 0) >= 400 || r.data == null || r.data!.length < 2000 || !(type.startsWith('image/') || type.isEmpty)) return null;
      return Uint8List.fromList(r.data!);
    } catch (_) {
      return null;
    }
  }
}
