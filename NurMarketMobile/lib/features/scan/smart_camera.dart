import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:path_provider/path_provider.dart';

import '../../core/ai/cam_aliases.dart';
import '../../core/ai/vision.dart';
import '../../core/catalog/catalog_matcher.dart';
import '../../core/invoice/invoice_parser.dart' show InvoiceParser;
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../admin/admin_receive.dart';
import '../admin/ai_access_ui.dart';
import '../admin/invoice_import.dart' show InvoiceFileArg;
import '../sale/sale_screen.dart';
import 'code_lookup.dart';
import 'live_shot.dart';
import '../../core/ai/product_refs.dart';
import '../../core/util/audit.dart';
import 'barcode_bind.dart';

/// 13.10, доски Т9–Т10 и владелец: «умная камера при продаже в телефоне где?». Снимок системной камерой (без штрихкода),
/// товары узнаёт ИИ (VisionService), дальше — в чек (продажа) или в приёмку (полка, накладная, этикетки).
/// 14.10, владелец: «умная камера в виде сканера, а не фото» — видоискатель как у сканера (LiveShotPage) вместо
/// системной камеры: кадр с живого изображения, «Авто» — сам через 2 секунды.
Future<Uint8List?> takeProductPhoto(BuildContext context, String mode) {
  final l = context.l;
  final hint = switch (mode) {
    'shelf' => l.liveHintShelf,
    'invoice' => l.liveHintInvoice,
    'labels' => l.liveHintLabels,
    _ => l.liveHintSale,
  };
  // 2026-10-22: под подсказкой — что будет после снимка
  return LiveShotPage.open(context, title: l.smartCam, hint: hint, next: l.liveNextList);
}

/// Ключа ИИ нет ни на сервере, ни на телефоне — предлагаем вставить (как поле ключа в настройках кассы).
Future<bool> ensureAiKey(BuildContext context, WidgetRef ref) async {
  final vision = ref.read(visionProvider);
  // 2026-10-26 (владелец: ИИ — не больше 2 сотрудникам): без права на ИИ — понятное сообщение «доступен только …», а не «нет ключа»;
  // с правом — один раз короткое пояснение про общий лимит.
  try {
    if (await vision.key() != null) {
      if (context.mounted) await showAiNoticeOnce(context, ref);
      return true;
    }
  } on AiAccessDenied catch (e) {
    if (context.mounted) toast(context, e.message, error: true);
    return false;
  }
  if (!context.mounted) return false;
  final l = context.l;
  final ctrl = TextEditingController();
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(l.smartCam),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(l.aiKeyNeeded),
          const SizedBox(height: 10),
          TextField(
            controller: ctrl,
            decoration: InputDecoration(labelText: l.aiKeyLabel),
            obscureText: true,
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(MaterialLocalizations.of(c).cancelButtonLabel)),
        FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(MaterialLocalizations.of(c).okButtonLabel)),
      ],
    ),
  );
  if (ok != true || ctrl.text.trim().isEmpty) return false;
  await vision.saveKey(ctrl.text);
  return true;
}

/// Снимок → распознавание → совпадения в каталоге; ожидание — окно «Узнаю товар…».
Future<List<SeenMatch>?> photoAndRecognize(BuildContext context, WidgetRef ref, String mode) async {
  if (!await ensureAiKey(context, ref) || !context.mounted) return null;
  final jpeg = await takeProductPhoto(context, mode);
  if (jpeg == null || !context.mounted) return null;
  return recognizeBytes(context, ref, jpeg, mode);
}

/// 2026-10-27: штрихкод со снимка (сканер читает файл, камеру не занимает) — до ИИ. Для проверок подменяется.
@visibleForTesting
Future<String?> Function(Uint8List jpeg) shotBarcodeReader = _readShotBarcode;

Future<String?> _readShotBarcode(Uint8List jpeg) async {
  File? tmp;
  final scanner = MobileScannerController(autoStart: false);
  try {
    final dir = await getTemporaryDirectory();
    tmp = File('${dir.path}${Platform.pathSeparator}shot_${DateTime.now().microsecondsSinceEpoch}.jpg');
    await tmp.writeAsBytes(jpeg, flush: true);
    final r = await scanner.analyzeImage(tmp.path);
    final v = r?.barcodes.firstOrNull?.rawValue;
    return v == null || v.trim().isEmpty ? null : v.trim();
  } finally {
    await scanner.dispose();
    try {
      await tmp?.delete();
    } catch (_) {}
  }
}

/// Распознать готовые байты (снимок с камеры, фото из галереи, PDF из файла): окно «Узнаю…», ИИ, сопоставление.
/// 2026-10-27 (владелец: «на фото существующий товар не находит»): полка — штрихкод со снимка читается ДО ИИ (код есть в базе —
/// товар готов без запроса к Gemini); эталоны обучения сверяются на полке и в продаже (были только в продаже); накладная и
/// ценники — документ, а не вид упаковки, эталоны там не нужны. [mime] — 'application/pdf' для PDF; [page]/[pages] — «Фото 2 из 3».
Future<List<SeenMatch>?> recognizeBytes(
  BuildContext context,
  WidgetRef ref,
  Uint8List bytes,
  String mode, {
  String mime = 'image/jpeg',
  int? page,
  int? pages,
}) async {
  final l = context.l;
  showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (c) => AlertDialog(
      content: Row(
        children: [
          const CircularProgressIndicator(),
          const SizedBox(width: 16),
          Expanded(child: Text(pages != null && pages > 1 && page != null ? '${l.recognizing}\n${l.camPageOf(page, pages)}' : l.recognizing)),
        ],
      ),
    ),
  );
  try {
    final vision = ref.read(visionProvider);
    final isImage = mime.startsWith('image/');
    String? code;
    if (mode == 'shelf' && isImage) {
      try {
        code = await shotBarcodeReader(bytes).timeout(const Duration(seconds: 3));
      } catch (_) {
        code = null;
      }
      if (code != null) {
        final hit = await CodeLookup.find(ref.read(databaseProvider), ref.read(apiProvider), code, allowWeightLabel: false);
        if (hit.product != null) {
          if (context.mounted) Navigator.of(context, rootNavigator: true).pop();
          final p = hit.product!;
          return [SeenMatch(SeenItem(name: p.name, barcode: code, count: hit.qty?.round().clamp(1, 999) ?? 1), [(p, 1.0)])];
        }
      }
    }
    var seen = await vision.recognize(bytes, mode: mode, mime: mime);
    // штрихкод снимка не нашёл товара, но на снимке один товар — код достаётся его карточке
    if (code != null && seen.length == 1 && seen.first.barcode.isEmpty) {
      final s0 = seen.first;
      seen = [SeenItem(name: s0.name, nameRu: s0.nameRu, brand: s0.brand, size: s0.size, barcode: code, count: s0.count, labelPrice: s0.labelPrice)];
    }
    var matches = await vision.match(seen);
    // 14.10, владелец: обученные товары (3 фото + штрихкод) — точное совпадение с эталонами, первыми в списке.
    if (isImage && (mode == 'sale' || mode == 'shelf')) matches = mergeExact(await exactMatches(ref, bytes, matches), matches);
    if (context.mounted) Navigator.of(context, rootNavigator: true).pop();
    return matches;
  } on VisionException catch (e) {
    if (context.mounted) {
      Navigator.of(context, rootNavigator: true).pop();
      if (e.noKey) {
        // 2026-10-27: ключ Google отклонил — он уже помечен на 30 минут (ротация ключей); НЕ стираем и не помечаем «навсегда»
        // (раньше saveKey('') навсегда выключал общий облачный ключ на этом телефоне и ломал чат Советника)
        toast(context, l.aiKeyNeeded, error: true);
      } else {
        toast(context, e.message, error: true);
      }
    }
    return null;
  }
}

/// 2026-10-22, владелец: «умная камера после сканера очень долго думает». Эталоны в памяти телефона: обученные снимки
/// читались с диска, а фото товаров из NurCRM скачивались заново при каждом скане — по одному, до 8 загрузок подряд.
class RefCache {
  static final _local = <String, Future<List<Uint8List>>>{};
  static final _remote = <String, Future<List<Uint8List>>>{};

  static Future<Set<String>> trained(AppDatabase db) => ProductRefs.trainedIds(db);

  static Future<List<Uint8List>> local(AppDatabase db, String productId) => _local[productId] ??= ProductRefs.load(db, productId);

  /// 2026-10-22 (аудит У-8): эталоны с других телефонов — фото товара в NurCRM (снимки обучения уходят туда же
  /// дополнительными фото, см. [shareTeachShots]); до 3 фото, ждём не дольше 5 с; неудачное не запоминаем.
  static Future<List<Uint8List>> remote(ApiClient api, Product p) =>
      (_remote[p.id] ??= _loadRemote(api, p)).timeout(const Duration(seconds: 5), onTimeout: () => const <Uint8List>[]);

  static Future<List<Uint8List>> _loadRemote(ApiClient api, Product p) async {
    final urls = <String>[];
    try {
      final data = await api.get('api/main/products/${p.id}/', timeout: const Duration(seconds: 3));
      if (data is Map) urls.addAll(imageUrls(data.cast<String, dynamic>()));
    } catch (_) {
      // нет связи — хотя бы главное фото
    }
    if (urls.isEmpty && p.imageUrl != null) urls.add(p.imageUrl!);
    final imgs = await Future.wait(urls.take(ProductRefs.maxPerProduct).map(ProductRefs.fromUrl));
    final out = [for (final i in imgs) ?i];
    if (out.isEmpty) _remote.remove(p.id);
    return out;
  }

  /// Адреса всех фото товара из ответа NurCRM (images: строки или объекты с адресом), главное — первым.
  static List<String> imageUrls(Map<String, dynamic> p) {
    final out = <String>[];
    void add(Object? v) {
      if (v is Map) {
        for (final k in const ['url', 'image', 'src', 'file']) {
          if (v[k] is String) return add(v[k]);
        }
        return;
      }
      if (v is! String || v.trim().isEmpty) return;
      var u = v.trim();
      if (u.startsWith('//')) {
        u = 'https:$u';
      } else if (!u.startsWith('http')) {
        u = '${ApiClient.defaultBaseUrl}/${u.replaceFirst(RegExp(r'^/+'), '')}';
      }
      if (!out.contains(u)) out.add(u);
    }

    for (final k in const ['primary_image_url', 'image_url', 'image']) {
      add(p[k]);
    }
    for (final k in const ['images', 'photos', 'gallery']) {
      if (p[k] is List) {
        for (final it in (p[k] as List).take(8)) {
          add(it);
        }
      }
    }
    return out;
  }

  /// Товар обучен заново — старые снимки из памяти убрать.
  static void forget(String productId) => _local.remove(productId);

  /// Пока кассир наводит камеру — обученные эталоны в память.
  static Future<void> warm(AppDatabase db) async {
    try {
      final ids = (await trained(db)).take(8);
      await Future.wait(ids.map((id) => local(db, id)));
    } catch (_) {}
  }
}

/// Кадр против эталонов: обученные на этом телефоне товары (до 3 снимков) и, для найденных первым шагом, фото из NurCRM.
/// 2026-10-22 (аудит У-4): не больше 6 товаров и 2,5 МБ снимков на запрос (было до 8 × 5 PNG, 6–12 МБ) — успевает на
/// мобильной сети. 2026-10-22: эталоны грузятся одновременно и из памяти;
/// [withTrained] / [withUrl] — только обученные (сверка сразу, параллельно с распознаванием) или только фото NurCRM.
Future<List<SeenMatch>> exactMatches(WidgetRef ref, Uint8List shot, List<SeenMatch> first, {bool withTrained = true, bool withUrl = true}) async {
  try {
    final db = ref.read(databaseProvider);
    final trained = await RefCache.trained(db);
    final ordered = <String>[
      for (final m in first)
        for (final (p, _) in m.candidates) p.id,
    ];
    final ids = withTrained ? <String>{...ordered.where(trained.contains), ...trained.where((id) => !ordered.contains(id))}.take(6).toList() : const <String>[];
    final local = await Future.wait(ids.map((id) async => (await db.productById(id), await RefCache.local(db, id))));
    final refs = <(Product, List<Uint8List>)>[
      for (final (p, pngs) in local)
        if (p != null && pngs.isNotEmpty) (p, pngs),
    ];
    if (withUrl) {
      // товары, обученные на другом телефоне: их фото в NurCRM
      final want = <Product>[];
      for (final m in first) {
        for (final (p, _) in m.candidates.take(2)) {
          if (refs.length + want.length >= 6 || trained.contains(p.id) || p.imageUrl == null) continue;
          if (refs.any((r) => r.$1.id == p.id) || want.any((w) => w.id == p.id)) continue;
          want.add(p);
        }
      }
      final api = ref.read(apiProvider);
      final imgs = await Future.wait(want.map((p) => RefCache.remote(api, p)));
      for (var i = 0; i < want.length; i++) {
        if (imgs[i].isNotEmpty) refs.add((want[i], imgs[i]));
      }
    }
    // общий размер снимков — не больше 2,5 МБ (в base64 ещё +33 %)
    var budget = 2500 * 1024 - shot.length;
    for (var i = 0; i < refs.length; i++) {
      final keep = <Uint8List>[];
      for (final img in refs[i].$2) {
        if (img.length > budget) break;
        budget -= img.length;
        keep.add(img);
      }
      refs[i] = (refs[i].$1, keep);
    }
    refs.removeWhere((r) => r.$2.isEmpty);
    if (refs.isEmpty) return const [];
    final found = await ref.read(visionProvider).identify(shot, [for (final r in refs) (r.$1.name, r.$2)]);
    return [
      for (final (i, count) in found) SeenMatch(SeenItem(name: '✓ ${refs[i].$1.name}', count: count), [(refs[i].$1, 1.0)]),
    ];
  } catch (_) {
    return const [];
  }
}

/// Точные совпадения — первыми, без повторов того же товара.
List<SeenMatch> mergeExact(List<SeenMatch> exact, List<SeenMatch> matches) {
  if (exact.isEmpty) return matches;
  final ids = <String>{};
  final uniq = [
    for (final e in exact)
      if (ids.add(e.candidates.first.$1.id)) e,
  ];
  return [...uniq, ...matches.where((m) => m.best == null || !ids.contains(m.best!.id))];
}

/// 14.10, владелец: обучение умной камеры — снимки видоискателем (справа, слева, сверху) и штрихкод.
/// 2026-10-22: все снимки — на одной странице с одной камерой (раньше на каждый снимок открывалась новая страница,
/// и второй кадр шёл в чёрный экран): «Фото 2 из 5», «Переснять», «Готово» — с 1 снимка, советуем 3.
/// 2026-10-22, владелец: «после обучения выходит сканер, но он тупо ничего не говорит». Был общий сканер приёмки
/// (подсказка «Отсканировано: 0», после скана не закрывался и ждал «Готово», без штрихкода — только крестик). Теперь
/// шаг «Штрихкод» ([BarcodeBindPage]): «1 Фото ✓ → 2 Штрихкод → 3 Готово», «Пропустить», «Ввести вручную», после
/// скана — что будет с кодом; код другого товара — «Заменить» ([takenFrom]) / «Оставить» / «Отмена».
/// [productName] и [productId] — для надписи «привязан к «Товар»» и возможности «Заменить» (у созданного товара).
Future<({List<Uint8List> shots, String? barcode, Product? takenFrom})?> teachCamera(
  BuildContext context, {
  bool askBarcode = true,
  String productName = '',
  String? productId,
}) async {
  final l = context.l;
  final shots = await LiveShotPage.openMany(
    context,
    title: l.teachCamera,
    hint: l.teachHint,
    steps: [l.teachAngleRight, l.teachAngleLeft, l.teachAngleTop, l.teachAngleFront, l.teachAngleBack],
    stages: askBarcode ? [l.stepPhoto, l.stepBarcode, l.done] : [l.stepPhoto, l.done],
    next: askBarcode ? l.teachNext : l.teachNextNoCode,
  );
  if (shots == null || shots.isEmpty) return null;
  String? barcode;
  Product? takenFrom;
  if (askBarcode && context.mounted) {
    // сканер штрихкодов включаем, только когда умная камера отпустила камеру
    await CameraGate.idle();
    if (!context.mounted) return (shots: shots, barcode: null, takenFrom: null);
    final b = await BarcodeBindPage.open(context, productName: productName, productId: productId);
    barcode = b?.code;
    takenFrom = b?.takenFrom;
  }
  return (shots: shots, barcode: barcode, takenFrom: takenFrom);
}

/// 2026-10-22 (аудит У-8): эталоны были только на этом телефоне — обучение не помогало второму телефону и пропадало при
/// переустановке. Снимки обучения (до 3, кроме уже отправленного главным фото) уходят в NurCRM дополнительными фото
/// товара — другие телефоны берут их эталонами ([RefCache.remote]). Без связи — молча, эталоны на телефоне остаются.
Future<void> shareTeachShots(WidgetRef ref, String productId, List<Uint8List> shots, {Uint8List? already}) async {
  final repo = ref.read(adminReceiveRepoProvider);
  for (final s in shots.where((s) => !identical(s, already)).take(ProductRefs.maxPerProduct)) {
    try {
      await repo.uploadImage(productId, await ProductRefs.compact(s));
    } catch (_) {}
  }
}

/// Главное фото товара из снимков обучения: этикетка спереди (4-й снимок), иначе последний.
Uint8List teachPhoto(List<Uint8List> shots) => shots.length > 3 ? shots[3] : shots.last;

/// Обучить камеру на уже существующем товаре (склад → действия товара).
Future<void> teachExistingProduct(BuildContext context, WidgetRef ref, Product p) async {
  final r = await teachCamera(context, askBarcode: p.barcode == null || p.barcode!.isEmpty, productName: p.name, productId: p.id);
  if (r == null || !context.mounted) return;
  final l = context.l;
  await ProductRefs.save(ref.read(databaseProvider), p.id, r.shots);
  RefCache.forget(p.id);
  var linked = false;
  try {
    // 2026-10-22: «Заменить» — сначала снять штрихкод с другого товара, потом записать этому
    if (r.barcode != null && r.takenFrom != null) await ref.read(apiProvider).patch('api/main/products/${r.takenFrom!.id}/', body: {'barcode': null});
    if (r.barcode != null) {
      await ref.read(apiProvider).patch('api/main/products/${p.id}/', body: {'barcode': r.barcode});
      linked = true;
      // телефон узнаёт товар по новому коду сразу, не через 5 минут
      unawaited(ref.read(catalogRepoProvider).sync().then((_) {}, onError: (_) {}));
    }
    final photo = p.imageUrl == null ? teachPhoto(r.shots) : null;
    if (photo != null) await ref.read(adminReceiveRepoProvider).uploadImage(p.id, photo, primary: true);
    unawaited(shareTeachShots(ref, p.id, r.shots, already: photo));
  } on ApiException catch (e) {
    if (context.mounted) toast(context, e.message, error: true);
  }
  Audit.log('product', '${l.teachCamera}: ${p.name}');
  if (context.mounted) toast(context, linked ? '${l.teachDone}\n${l.bindLinked(r.barcode!, p.name)}' : l.teachDone);
}

/// 2026-10-22, владелец: «умная камера после сканера очень долго думает». Было по очереди: кадр 1080p → распознавание →
/// эталоны с диска и фото из NurCRM по одному → второй запрос к ИИ с тем же кадром, и только потом список. Теперь кадр
/// 720p; сверка с обученными эталонами (если их до 8) идёт одновременно с распознаванием; список показывается сразу
/// после первого ответа ИИ, а «✓ точно» по фото NurCRM дописывается следом ([show] с done = false, затем true).
Future<void> recognizeSaleShot(WidgetRef ref, Uint8List jpeg, void Function(List<SeenMatch> matches, bool done) show) async {
  final vision = ref.read(visionProvider);
  // 2026-10-22 (скорость скана): запрос к ИИ уходит сразу, список обученных читается из базы одновременно с ним
  final seenF = vision.recognize(jpeg, mode: 'sale');
  final Set<String> trained;
  try {
    trained = await RefCache.trained(ref.read(databaseProvider));
  } catch (_) {
    await seenF.then((_) {}, onError: (_) {});
    rethrow;
  }
  final early = trained.isNotEmpty && trained.length <= 6 ? exactMatches(ref, jpeg, const [], withUrl: false) : null;
  final first = await vision.match(await seenF);
  // обученных больше 8 — берём похожие на увиденное; фото NurCRM — для похожих необученных
  final needUrl = first.any((m) => m.candidates.take(2).any((c) => c.$1.imageUrl != null && !trained.contains(c.$1.id)));
  final late = early == null || needUrl ? exactMatches(ref, jpeg, first, withTrained: early == null) : null;
  if (early == null && late == null) {
    show(first, true);
    return;
  }
  show(first, false);
  final exact = <SeenMatch>[];
  for (final f in [?early, ?late]) {
    exact.addAll(await f.timeout(const Duration(seconds: 6), onTimeout: () => const <SeenMatch>[]));
  }
  show(mergeExact(exact, first), true);
}

/// Продажа «По виду»: увиденное и подходящие товары каталога — нажатие кладёт товар в чек (сколько штук видно на снимке).
/// 2026-10-22: камера не закрывается после снимка — «Сканировать» снова для следующего товара, список — под рамкой.
Future<void> recognizeForSale(BuildContext context, WidgetRef ref) async {
  if (!await ensureAiKey(context, ref) || !context.mounted) return;
  final l = context.l;
  unawaited(RefCache.warm(ref.read(databaseProvider)));
  await LiveShotPage.openLive(
    context,
    title: l.smartCam,
    hint: l.liveHintSale,
    next: l.liveNextSale,
    onShot: (jpeg, show, sincePress) async {
      // товары этого снимка, уже положенные в чек (повторное касание — вопрос «добавить ещё?»)
      final added = <String>{};
      try {
        await recognizeSaleShot(ref, jpeg, (matches, done) {
          final sec = sincePress.elapsedMilliseconds / 1000;
          show((c) => _salePanel(c, ref, matches, checking: !done, seconds: sec, added: added));
        });
      } on VisionException catch (e) {
        // 2026-10-27: ключ при отказе Google уже помечен на 30 минут; не стираем (было saveKey('') — «навсегда»)
        show((c) => _errorPanel(c, aiErrorText(c, e)));
      }
    },
  );
}

Future<bool?> _askYes(BuildContext context, String text) => showDialog<bool>(
  context: context,
  builder: (c) => AlertDialog(
    content: Text(text),
    actions: [
      TextButton(onPressed: () => Navigator.pop(c, false), child: Text(c.l.cancel)),
      FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(c.l.yesAdd)),
    ],
  ),
);

/// 2026-10-22 (аудит У-9): ошибка ИИ на языке кассира, а не сырой текст исключения.
String aiErrorText(BuildContext c, VisionException e) {
  final l = c.l;
  final m = e.message.toLowerCase();
  if (e is AiAccessDenied) return e.message; // 2026-10-26: нет права на ИИ — текст уже на языке интерфейса
  if (e.noKey) return l.aiKeyNeeded;
  if (m.contains('связи') || m.contains('connection') || m.contains('socket')) return l.aiErrNoNet;
  if (m.contains('вовремя') || m.contains('timeout')) return l.aiErrSlow;
  if (m.contains('лимит') || m.contains('429')) return l.aiErrLimit;
  if (m.contains('перегруж') || m.contains('недоступна') || m.contains('503')) return l.aiErrBusy;
  return '${l.aiErrOther}\n${e.message}';
}

Widget _errorPanel(BuildContext c, String text) => Padding(
  padding: const EdgeInsets.fromLTRB(16, 16, 48, 16),
  child: Text(text, style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700)),
);

Widget _salePanel(BuildContext c, WidgetRef ref, List<SeenMatch> matches, {required bool checking, required double seconds, required Set<String> added}) {
  final l = c.l;
  final sec = seconds.toStringAsFixed(1);
  return StatefulBuilder(builder: (c, setPanel) => ListView(
    shrinkWrap: true,
    padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
    children: [
      Padding(
        padding: const EdgeInsets.only(right: 36),
        child: Row(
          children: [
            Text(l.seenOnPhoto, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
            const SizedBox(width: 8),
            Text(
              l.scanTook(Localizations.localeOf(c).languageCode == 'en' ? sec : sec.replaceAll('.', ',')),
              style: TextStyle(color: c.pal.textSoft, fontSize: 12),
            ),
          ],
        ),
      ),
      if (checking)
        Padding(
          padding: const EdgeInsets.only(top: 6),
          child: Row(
            children: [
              const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2)),
              const SizedBox(width: 8),
              Expanded(child: Text(l.scanChecking, style: TextStyle(color: c.pal.textSoft, fontSize: 12))),
            ],
          ),
        ),
      if (matches.isEmpty) Padding(padding: const EdgeInsets.only(top: 8), child: Text(l.recognizedNone, style: const TextStyle(color: T.warn))),
      for (final m in matches) ...[
        const SizedBox(height: 8),
        Text(
          '${m.seen.title} · ×${m.seen.count}${m.seen.name.startsWith('✓') ? ' · ${l.exactMatch}' : ''}',
          style: TextStyle(color: m.seen.name.startsWith('✓') ? T.success : c.pal.textSoft, fontWeight: FontWeight.w700),
        ),
        if (m.candidates.isEmpty) Text(l.notInCatalog, style: const TextStyle(color: T.warn)),
        for (final (p, score) in m.candidates)
          ListTile(
            contentPadding: EdgeInsets.zero,
            dense: true,
            leading: ProductThumb(name: p.name, url: p.imageUrl, size: 40),
            title: Text(p.name, style: TextStyle(fontWeight: score >= .5 ? FontWeight.w800 : FontWeight.w500)),
            subtitle: Text('${money(p.price)} ${l.som} · ${l.inStock(qtyText(p.quantity), p.unit)}'),
            // 2026-10-22 (аудит У-10): второе касание того же товара — спросить, а не добавить молча ещё строку
            trailing: added.contains(p.id) ? const Icon(Icons.check_circle_rounded, color: T.success) : const Icon(Icons.add_shopping_cart_rounded),
            onTap: () async {
              if (added.contains(p.id) && await _askYes(c, l.addAgain(p.name)) != true) return;
              if (!c.mounted) return;
              await addProduct(c, ref, p, qty: p.isWeight ? null : m.seen.count.toDouble());
              added.add(p.id);
              if (c.mounted) {
                setPanel(() {});
                toast(c, l.addedToCheckName(p.name));
              }
            },
          ),
      ],
    ],
  ));
}

/// Т9–Т10: снимки полки, накладной или этикеток → проверка: «есть в базе» (+ к остатку) и «новые» (название, закупка, цена)
/// → «Загрузить на склад» сразу или «В приёмку с поставщиком» (ПР2–ПР6). 13.10, владелец: «дизайн умной камеры продумай»,
/// «количество не вводится» — у каждой строки −/+ и ввод числа, у новых — поля названия, закупки и цены.
/// 2026-10-26 (П0, ошибка денег Б1): состояние строки загрузки. Раньше строки прихода строились заново при каждом
/// нажатии «Загрузить» — флаги «проведена / создание без ответа / PATCH остатка без ответа» жили в выброшенных объектах,
/// и повтор после частичной ошибки второй раз прибавлял остаток проведённым строкам и создавал товар второй раз.
enum CamRowState { pending, posted, rejected, unknown }

/// Строка уже ушла на сервер так, что менять товар/название нельзя: проведена, создание или запись остатка без ответа,
/// новый товар уже создан. Такую строку сохраняем тем же объектом до успеха.
bool camLineLocked(ReceiveLine? l) =>
    l != null && (l.posted || l.createUnknown || l.pendingTarget != null || (l.isNew && l.productId.isNotEmpty));

CamRowState camRowState(ReceiveLine? l, String? error) {
  if (l == null) return CamRowState.pending;
  if (l.posted) return CamRowState.posted;
  if (l.createUnknown || l.pendingTarget != null) return CamRowState.unknown;
  return error != null ? CamRowState.rejected : CamRowState.pending;
}

/// Стабильная строка прихода позиции: один объект на задачу, создаётся ДО отправки и живёт до успеха. Перед проведением
/// обновляются только количество, закупка и (у нового) цена. Новая строка создаётся лишь пока старая не «заперта»
/// ([camLineLocked]) и не подходит по товару/названию/штрихкоду (кассир выбрал «Это «…»?» или поправил название).
ReceiveLine camSyncLine(
  ReceiveLine? old, {
  required Product? product,
  required String name,
  required String barcode,
  required double qty,
  required double cost,
  required double price,
}) {
  final fits = old != null && (product != null ? !old.isNew && old.productId == product.id : old.isNew && old.name == name && old.barcode == barcode);
  if (old != null && (fits || camLineLocked(old))) {
    old.qty = qty;
    old.cost = cost;
    if (old.isNew) old.price = price;
    return old;
  }
  return product != null
      ? ReceiveLine(
          productId: product.id,
          name: product.name,
          barcode: product.barcode ?? '',
          unit: product.unit,
          before: product.quantity,
          oldPrice: product.price,
          price: product.price,
          qty: qty,
          cost: cost,
        )
      : ReceiveLine(productId: '', name: name, barcode: barcode, qty: qty, cost: cost, price: price, isNew: true);
}

/// Связь с сервером есть: лёгкий запрос. Без связи ничего не проводим — список остаётся черновиком на экране.
/// Только «нет ответа вообще» (status == null) считаем отсутствием связи; ответ сервера с ошибкой — это связь.
Future<bool> camReachable(ApiClient api) async {
  try {
    await api.get('api/main/products/list/', query: {'page': 1, 'page_size': 1}, timeout: const Duration(seconds: 4));
    return true;
  } on ApiException catch (e) {
    return e.status != null;
  } catch (_) {
    return false;
  }
}

/// Приход поставщику с неизвестным исходом ещё хранит ключ повтора (24 ч). Пока он есть, «Загрузить на склад» без
/// поставщика тем же составом мог бы прибавить остаток второй раз — сначала повторить тот приход.
Future<bool> camHasPendingReceipt(AppDatabase db) async {
  try {
    final raw = await db.getSetting(AdminReceiveRepository.pendingReceiptKeySetting);
    if (raw == null || raw.isEmpty) return false;
    final at = DateTime.tryParse(((jsonDecode(raw) as Map)['at'] as String?) ?? '');
    return at != null && DateTime.now().difference(at) < const Duration(hours: 24);
  } catch (_) {
    return false;
  }
}

/// 2026-10-27: выбранный файл или фото накладной (байты, имя, тип для ИИ).
class CamPicked {
  final Uint8List bytes;
  final String name;
  final String mime;

  /// Файл слишком большой, чтобы читать его в память: байтов нет.
  final bool tooBig;
  const CamPicked(this.bytes, this.name, {this.mime = 'image/jpeg', this.tooBig = false});
}

/// Куда идёт выбранный файл накладной по расширению: таблицы читает «Загрузить накладную», PDF и фото — ИИ.
enum CamFileRoute { table, ai, oldExcel, unsupported }

/// Файл больше — в ИИ не отправляем (до base64: в Gemini уходит +33 %).
const camMaxAiBytes = 8 * 1024 * 1024;

String _ext(String name) {
  final i = name.lastIndexOf('.');
  return i < 0 ? '' : name.substring(i + 1).toLowerCase().trim();
}

/// Маршрут по расширению: xlsx/csv/xml/txt/tsv — таблица; pdf/jpg/jpeg/png/webp — ИИ; xls — «сохраните как .xlsx».
CamFileRoute camFileRoute(String name) => switch (_ext(name)) {
  'xlsx' || 'csv' || 'xml' || 'txt' || 'tsv' => CamFileRoute.table,
  'pdf' || 'jpg' || 'jpeg' || 'png' || 'webp' => CamFileRoute.ai,
  'xls' => CamFileRoute.oldExcel,
  _ => CamFileRoute.unsupported,
};

/// Тип файла для ИИ: PDF — application/pdf, остальное — картинка.
String camMimeOf(String name) => switch (_ext(name)) {
  'pdf' => 'application/pdf',
  'png' => 'image/png',
  'webp' => 'image/webp',
  _ => 'image/jpeg',
};

/// «Из галереи»: системный выбор фото (Android 13+ — Photo Picker, разрешений не нужно); несколько страниц накладной.
Future<List<CamPicked>> _pickImages() async {
  final shots = await ImagePicker().pickMultiImage(maxWidth: 2400, imageQuality: 85);
  return [for (final x in shots) CamPicked(await x.readAsBytes(), x.name, mime: camMimeOf(x.name))];
}

/// «Из файла»: системный выбор файла (SAF, разрешений не нужно).
Future<CamPicked?> _pickFile() async {
  final files = await FilePicker.pickFiles(type: FileType.custom, allowedExtensions: ['xlsx', 'csv', 'xml', 'txt', 'tsv', 'pdf', 'jpg', 'jpeg', 'png', 'webp']);
  final f = files.firstOrNull;
  if (f == null) return null;
  final len = await f.length();
  // таблицы читаются до InvoiceParser.maxBytes, для ИИ — до camMaxAiBytes; больше любого предела — не читаем в память
  if (len != null && len > InvoiceParser.maxBytes) return CamPicked(Uint8List(0), f.name, tooBig: true);
  return CamPicked(Uint8List.fromList(await f.readAsBytes()), f.name, mime: camMimeOf(f.name));
}

class _CamItem {
  final SeenMatch seen;
  Product? product;
  double qty;
  // 2026-10-26 (П0): стабильная строка прихода этой позиции и причина последнего отказа сервера
  ReceiveLine? line;
  String? error;
  final TextEditingController name;
  final TextEditingController cost;
  final TextEditingController price;
  // 2026-10-27 (ревью М-3): что запомнено при выборе варианта в этой позиции (чтобы «Не тот товар» это отменило) и
  // товары, которые кассир отверг («не тот») — в вариантах их больше нет
  String? learnedId;
  List<String> learned = [];
  final Set<String> rejected = {};
  // 2026-10-27: товар берётся сам, только если лучший ≥ 0,85 и отрыв от второго ≥ 0,15 ([SeenMatch.picked]); иначе — карточка
  // «Похоже на» с вариантами (раньше порог 0,75: всё слабее уходило в «Новые» и выглядело как «товар не найден»)
  _CamItem(this.seen)
    : product = seen.picked,
      qty = seen.seen.count.toDouble(),
      name = TextEditingController(text: seen.seen.title),
      cost = TextEditingController(text: seen.seen.labelPrice > 0 ? seen.seen.labelPrice.toStringAsFixed(0) : ''),
      price = TextEditingController();
}

class SmartCameraScreen extends ConsumerStatefulWidget {
  /// Только для проверок без камеры: уже распознанные позиции и режим (2026-10-26, П0).
  @visibleForTesting
  final List<SeenMatch>? debugMatches;
  @visibleForTesting
  final String? debugMode;

  /// 2026-10-27: режим из маршрута (?mode=invoice — «Сфотографировать накладную» из «Загрузить накладную»).
  final String? initialMode;

  /// Только для проверок без системных окон выбора: фото «из галереи» и файл «из файла».
  @visibleForTesting
  final Future<List<CamPicked>> Function()? debugPickImages;
  @visibleForTesting
  final Future<CamPicked?> Function()? debugPickFile;
  const SmartCameraScreen({super.key, this.debugMatches, this.debugMode, this.initialMode, this.debugPickImages, this.debugPickFile});
  @override
  ConsumerState<SmartCameraScreen> createState() => _SmartCameraScreenState();
}

class _SmartCameraScreenState extends ConsumerState<SmartCameraScreen> {
  late String _mode = widget.debugMode ?? (const {'shelf', 'invoice', 'labels'}.contains(widget.initialMode) ? widget.initialMode! : 'shelf');
  late final List<_CamItem> _items = [for (final m in widget.debugMatches ?? const <SeenMatch>[]) _CamItem(m)];
  int _shots = 0;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    // 2026-10-27: индекс сопоставления строится, пока кассир наводит камеру (на большом каталоге — доли секунды)
    unawaited(CatalogMatcher.warm(ref.read(databaseProvider)));
  }

  // 2026-10-26 (П0): защита от двойного нажатия (ставится синхронно, до первого await), всего проведено за эту загрузку
  // и итог последней попытки («проведено N из M…»), который остаётся на экране.
  bool _posting = false;
  int _postedTotal = 0;
  String? _summary;

  Future<void> _shoot() async {
    final found = await photoAndRecognize(context, ref, _mode);
    if (found == null || !mounted) return;
    _addMatches(found);
  }

  /// Результат распознавания (снимок, фото из галереи, PDF) → позиции экрана; тот же товар — к количеству.
  void _addMatches(List<SeenMatch> found) {
    if (found.isEmpty) toast(context, context.l.recognizedNone, error: true);
    setState(() {
      _shots++;
      for (final m in found) {
        final same = m.picked == null ? null : _items.where((x) => x.product?.id == m.picked!.id).firstOrNull;
        if (same != null) {
          same.qty += m.seen.count;
        } else {
          _items.add(_CamItem(m));
        }
      }
    });
  }

  /// 2026-10-27 (владелец: «в накладной добавь выбор из галереи»): одно или несколько фото накладной из галереи (страницы).
  /// 2026-10-27 (владелец: «не только из камеры, но и из галереи»): то же для «Полки» и «Этикеток» — фото из галереи
  /// идёт в тот же разбор, что и живой снимок текущего режима.
  Future<void> _fromGallery() async {
    if (_busy) return;
    final l = context.l;
    final pick = widget.debugPickImages ?? _pickImages;
    final mode = _mode; // режим на момент выбора: пока идёт разбор, вкладку могут переключить
    List<CamPicked> files;
    try {
      files = await pick();
    } catch (_) {
      if (mounted) toast(context, l.camFileReadFail, error: true);
      return;
    }
    if (files.isEmpty || !mounted) return;
    if (!await ensureAiKey(context, ref) || !mounted) return;
    for (var i = 0; i < files.length; i++) {
      final f = files[i];
      if (f.bytes.length > camMaxAiBytes) {
        toast(context, l.camFileTooBig, error: true);
        continue;
      }
      final found = await recognizeBytes(context, ref, f.bytes, mode, mime: f.mime, page: i + 1, pages: files.length);
      if (found == null || !mounted) return;
      _addMatches(found);
    }
  }

  /// «Из файла»: таблица (xlsx/csv/xml/txt/tsv) — в экран «Загрузить накладную»; PDF и фото — в ИИ.
  Future<void> _fromFile() async {
    if (_busy) return;
    final l = context.l;
    CamPicked? f;
    try {
      f = await (widget.debugPickFile ?? _pickFile)();
    } catch (_) {
      if (mounted) toast(context, l.camFileReadFail, error: true);
      return;
    }
    if (f == null || !mounted) return;
    if (f.tooBig) {
      toast(context, camFileRoute(f.name) == CamFileRoute.table ? l.invErrTooBig : l.camFileTooBig, error: true);
      return;
    }
    switch (camFileRoute(f.name)) {
      case CamFileRoute.table:
        if (!(ref.read(apiProvider).session?.canRoute('/admin/invoice') ?? false)) {
          toast(context, l.camFileNoProcure, error: true);
          return;
        }
        if (f.bytes.length > InvoiceParser.maxBytes) {
          toast(context, l.invErrTooBig, error: true);
          return;
        }
        await context.push('/admin/invoice', extra: InvoiceFileArg(f.bytes, f.name));
      case CamFileRoute.ai:
        if (f.bytes.length > camMaxAiBytes) {
          toast(context, l.camFileTooBig, error: true);
          return;
        }
        if (!await ensureAiKey(context, ref) || !mounted) return;
        final found = await recognizeBytes(context, ref, f.bytes, 'invoice', mime: camMimeOf(f.name));
        if (found == null || !mounted) return;
        _addMatches(found);
      case CamFileRoute.oldExcel:
        toast(context, l.invErrXls, error: true);
      case CamFileRoute.unsupported:
        toast(context, l.camFileBadType, error: true);
    }
  }

  /// Кассир выбрал «Это «…»?» — запоминаем, как ИИ называл упаковку: в следующий раз товар узнаётся сразу (оценка 1,0).
  /// Алиас общий для компании, если хватает прав записи; иначе остаётся на этом телефоне ([CamAliases]).
  void _learn(_CamItem x, Product p) {
    // 2026-10-27 (ревью М-3): выбор другого варианта отменяет прежнее запоминание этой позиции
    if (x.learnedId != null && x.learnedId != p.id) _unlearn(x);
    final names = {x.seen.seen.title, x.seen.seen.titleRu}.where((t) => t.trim().length >= 3);
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    x.learnedId = p.id;
    x.learned = [for (final n in names) CamAliases.normalize(n)];
    for (final n in names) {
      CamAliases.add(db, api, p.id, n).then((_) {}, onError: (_) {});
    }
  }

  /// 2026-10-27 (ревью М-3): забыть названия, запомненные в этой позиции (отметка «удалено» уходит и на сервер).
  void _unlearn(_CamItem x) {
    final id = x.learnedId;
    if (id == null) return;
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    for (final n in x.learned) {
      CamAliases.remove(db, api, id, text: n).then((_) {}, onError: (_) {});
    }
    x.learnedId = null;
    x.learned = [];
  }

  /// 2026-10-27 (ревью М-3): «Не тот товар» — позиция возвращается к вариантам, а названия, по которым товар был выбран
  /// (запомненные ранее или сейчас), забываются: ошибка не закрепляется для всей компании.
  void _notThis(_CamItem x) {
    final p = x.product;
    if (p == null) return;
    if (camLineLocked(x.line)) {
      toast(context, context.l.smartRowLocked, error: true);
      return;
    }
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    for (final n in {x.seen.seen.title, x.seen.seen.titleRu, ...x.learned}) {
      if (n.trim().length < 3) continue;
      final norm = CamAliases.normalize(n);
      if ((CamAliases.current[p.id] ?? const <String>[]).contains(norm)) {
        CamAliases.remove(db, api, p.id, text: norm).then((_) {}, onError: (_) {});
      }
    }
    setState(() {
      x.rejected.add(p.id);
      x.product = null;
      x.learnedId = null;
      x.learned = [];
    });
    toast(context, context.l.camNotThisDone);
  }

  /// Запомненные названия товара [p] — по одному можно удалить (правка запомненного).
  Widget _aliasChips(Product p) {
    final names = CamAliases.current[p.id] ?? const <String>[];
    if (names.isEmpty) return const SizedBox.shrink();
    final pal = context.pal;
    final db = ref.read(databaseProvider);
    final api = ref.read(apiProvider);
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Wrap(
        spacing: 6,
        runSpacing: 4,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(context.l.camAliasesTitle, style: TextStyle(color: pal.textSoft, fontSize: 12)),
          for (final n in names)
            InputChip(
              key: ValueKey('alias_${p.id}_$n'),
              label: Text(n, style: const TextStyle(fontSize: 12)),
              onDeleted: () {
                CamAliases.remove(db, api, p.id, text: n).then((_) {}, onError: (_) {});
                setState(() {});
              },
              deleteButtonTooltipMessage: context.l.camAliasForget,
            ),
        ],
      ),
    );
  }

  static double _autoPrice(double cost) => cost <= 0 ? 0 : ((cost * 1.3 / 5).ceil() * 5).toDouble();

  /// 2026-10-26 (П0, Б1): строка прихода позиции — один объект до успеха ([camSyncLine]); раньше `_lines()` строил новые
  /// при каждом нажатии. Цена нового: введённая или «+30 %, до 5 сом».
  ReceiveLine _lineOf(_CamItem x) {
    final cost = parseNum(x.cost.text) ?? 0;
    return x.line = camSyncLine(
      x.line,
      product: x.product,
      name: x.product?.name ?? x.name.text.trim(),
      // штрихкод от ИИ — только с верной контрольной цифрой (ИИ мог его выдумать)
      barcode: x.product != null ? '' : (VisionService.validEan(x.seen.seen.barcode) ? x.seen.seen.barcode : ''),
      qty: x.qty,
      cost: cost,
      price: parseNum(x.price.text) ?? _autoPrice(cost),
    );
  }

  /// Строки к проведению: позиции с количеством, у новых — название и цена продажи.
  List<(_CamItem, ReceiveLine)> _ready() => [
    for (final x in _items.where((x) => x.qty > 0))
      if (_lineOf(x) case final l when l.name.isNotEmpty && (!l.isNew || l.price > 0)) (x, l),
  ];

  Future<void> _upload() async {
    // двойное нажатие: флаг ставится до первого await (диалог полки, проверка связи, сам запрос)
    if (_posting || _busy) return;
    _posting = true;
    final l = context.l;
    var started = false;
    try {
      final pairs = _ready();
      if (pairs.isEmpty) return;
      // 2026-10-22 (аудит У-7): снимок полки — то, что уже стоит на полке, а не поставка: прибавлять к остатку только по
      // явному «Да» (иначе остаток удваивался)
      if (_mode == 'shelf' && await _askYes(context, l.shelfAddAsk) != true) return;
      if (!mounted) return;
      final repo = ref.read(adminReceiveRepoProvider);
      setState(() => _busy = true);
      started = true;
      // 2026-10-26 (П0): без связи не проводим ничего — список остаётся на экране как черновик
      if (!await camReachable(repo.api)) {
        if (mounted) {
          setState(() => _summary = l.smartNoLink);
          toast(context, l.smartNoLink, error: true);
        }
        return;
      }
      if (await camHasPendingReceipt(ref.read(databaseProvider))) {
        if (mounted) {
          setState(() => _summary = l.smartPendingReceipt);
          toast(context, l.smartPendingReceipt, error: true);
        }
        return;
      }
      var errors = <String>[];
      String? fatal;
      try {
        errors = (await repo.post([for (final p in pairs) p.$2], null, false, l10n: l)).$2;
      } on ApiException catch (e) {
        fatal = e.message;
      } catch (e) {
        fatal = e.toString();
      }
      // синхронизация каталога не должна заслонять итог (раньше её ошибка прятала результат проведения)
      try {
        await ref.read(catalogRepoProvider).sync();
      } catch (_) {}
      if (!mounted) return;
      final doneNow = pairs.where((p) => p.$2.posted).length;
      final left = pairs.length - doneNow;
      _postedTotal += doneNow;
      // причина отказа — к своей позиции (текст ошибки начинается с названия: «Название: причина»)
      final pool = [...errors];
      for (final (x, line) in pairs) {
        x.error = null;
        if (line.posted) continue;
        final i = pool.indexWhere((e) => e.startsWith('${line.name}: '));
        if (i >= 0) x.error = pool.removeAt(i).substring(line.name.length + 2);
      }
      final allDone = left == 0 && fatal == null;
      final total = _postedTotal + left;
      setState(() {
        _items.removeWhere((x) => x.line?.posted == true);
        _summary = allDone ? null : l.smartPartial(_postedTotal, total, left);
      });
      if (allDone) {
        toast(context, l.smartUploaded(_postedTotal));
        _postedTotal = 0;
        unawaited(Navigator.of(context).maybePop());
      } else {
        final why = [?fatal, ...errors].take(2).join('; ');
        toast(context, '${l.smartPartial(_postedTotal, total, left)}${why.isEmpty ? '' : '\n$why'}', error: true);
      }
    } finally {
      _posting = false;
      if (started && mounted) setState(() => _busy = false);
    }
  }

  /// «В приёмку с поставщиком»: те же объекты строк (приёмка с поставщиком отметит их проведёнными).
  Future<void> _toReceive() async {
    final l = context.l;
    final lines = [for (final x in _items.where((x) => x.qty > 0)) _lineOf(x)];
    // запись остатка без ответа (путь «на склад») могла дойти — приход поставщику прибавил бы второй раз
    if (lines.any((x) => x.pendingTarget != null)) {
      toast(context, l.smartPendingStock, error: true);
      return;
    }
    final done = await context.push<bool>('/admin/receive', extra: lines);
    if (!mounted) return;
    setState(() => _items.removeWhere((x) => x.line?.posted == true));
    if (done == true && mounted) Navigator.of(context).maybePop();
  }

  /// Подпись состояния строки: отказ сервера / нет ответа (повтор безопасен).
  Widget _rowStatus(_CamItem x) {
    final l = context.l;
    final st = camRowState(x.line, x.error);
    if (st != CamRowState.rejected && st != CamRowState.unknown) return const SizedBox.shrink();
    final text = st == CamRowState.rejected
        ? l.smartRowRejected(x.error!)
        : '${l.smartRowUnknown}${x.error == null ? '' : '\n${x.error}'}';
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Text(text, style: TextStyle(color: st == CamRowState.rejected ? T.danger : T.warn, fontSize: 12.5, fontWeight: FontWeight.w700)),
    );
  }

  Future<void> _editQty(_CamItem x) async {
    final v = await askNumber(context, title: x.product?.name ?? x.name.text, initial: x.qty);
    if (v != null) setState(() => x.qty = v.clamp(0, 99999).toDouble());
  }

  Widget _qtyRow(_CamItem x) {
    final pal = context.pal;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        IconButton.outlined(onPressed: () => setState(() => x.qty = (x.qty - 1).clamp(0, 99999).toDouble()), icon: const Icon(Icons.remove)),
        InkWell(
          onTap: () => _editQty(x),
          borderRadius: BorderRadius.circular(10),
          child: Container(
            constraints: const BoxConstraints(minWidth: 56),
            height: 40,
            alignment: Alignment.center,
            margin: const EdgeInsets.symmetric(horizontal: 4),
            decoration: BoxDecoration(color: pal.window, borderRadius: BorderRadius.circular(10)),
            child: Text(qtyText(x.qty), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
          ),
        ),
        IconButton.filled(
          style: IconButton.styleFrom(backgroundColor: pal.action),
          onPressed: () => setState(() => x.qty += 1),
          icon: Icon(Icons.add, color: pal.onAction),
        ),
      ],
    );
  }

  Widget _knownCard(_CamItem x) {
    final pal = context.pal;
    final p = x.product!;
    return Panel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              ProductThumb(name: p.name, url: p.imageUrl, size: 44),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      p.name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                    Text(
                      '${context.l.onStockLabel}: ${qtyText(p.quantity)} ${p.unit} · ${money(p.price)}',
                      style: TextStyle(color: pal.textSoft, fontSize: 12),
                    ),
                  ],
                ),
              ),
              IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _items.remove(x))),
            ],
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Text(
                '+ ',
                style: TextStyle(color: pal.textSoft, fontWeight: FontWeight.w800),
              ),
              _qtyRow(x),
            ],
          ),
          _rowStatus(x),
          // 2026-10-27 (ревью М-3): неверный выбор можно отменить, запомненные названия — посмотреть и удалить
          if (x.seen.candidates.isNotEmpty || x.learnedId != null)
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                key: ValueKey('notThis_${p.id}'),
                style: TextButton.styleFrom(minimumSize: const Size(48, 48)),
                onPressed: () => _notThis(x),
                icon: const Icon(Icons.undo_rounded, size: 18),
                label: Text(context.l.camNotThis),
              ),
            ),
          _aliasChips(p),
        ],
      ),
    );
  }

  Widget _newCard(_CamItem x) {
    final l = context.l;
    final pal = context.pal;
    final cost = parseNum(x.cost.text) ?? 0;
    return Panel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(8)),
                child: Text(
                  l.newItemShort,
                  style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 11.5),
                ),
              ),
              if (x.seen.seen.barcode.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(left: 8),
                  child: Text(x.seen.seen.barcode, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                ),
              const Spacer(),
              IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _items.remove(x))),
            ],
          ),
          // 2026-10-27: похоже на товары из базы, но не уверенно — до 5 вариантов крупными кнопками, кассир выбирает сам;
          // выбор запоминается алиасом ([_learn]) — в следующий раз товар узнаётся сразу
          if (x.seen.candidates.any((c) => !x.rejected.contains(c.$1.id))) ...[
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Text(l.camPickTitle, style: TextStyle(color: pal.textSoft, fontSize: 12.5, fontWeight: FontWeight.w700)),
            ),
            for (final (p, score) in x.seen.candidates.where((c) => !x.rejected.contains(c.$1.id)).take(5))
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: OutlinedButton(
                  key: ValueKey('cand_${p.id}'),
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48), alignment: Alignment.centerLeft, padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8)),
                  onPressed: () {
                    // 2026-10-26 (П0): строка, уже ушедшая на сервер, товар не меняет (иначе остаток уйдёт не туда)
                    if (camLineLocked(x.line)) {
                      toast(context, l.smartRowLocked, error: true);
                      return;
                    }
                    setState(() => x.product = p);
                    _learn(x, p);
                  },
                  child: Row(
                    children: [
                      const Icon(Icons.inventory_2_outlined, size: 18),
                      const SizedBox(width: 8),
                      Expanded(child: Text(l.isItProduct(p.name), maxLines: 2, overflow: TextOverflow.ellipsis)),
                      const SizedBox(width: 6),
                      Text('${(score * 100).round()}%', style: TextStyle(color: pal.textSoft, fontSize: 12)),
                    ],
                  ),
                ),
              ),
          ],
          TextField(
            controller: x.name,
            readOnly: camLineLocked(x.line),
            decoration: InputDecoration(labelText: l.clientName, isDense: true),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: x.cost,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  onChanged: (_) => setState(() {}),
                  decoration: InputDecoration(labelText: l.purchasePrice, isDense: true),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: TextField(
                  controller: x.price,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(labelText: l.salePrice, hintText: cost > 0 ? money(_autoPrice(cost)) : null, isDense: true),
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          _qtyRow(x),
          _rowStatus(x),
        ],
      ),
    );
  }

  /// 2026-10-27 (владелец: подписи «Накладн/ая», «Этикетк/и» рвались посреди слова): три равные вкладки «значок над подписью»;
  /// подпись в одну строку и сама уменьшается под ширину ячейки (любой язык, шрифт 1,3), высота от 56 dp.
  Widget _modeTabs(AppLocalizations l) {
    final tabs = [
      ('shelf', Icons.shelves, l.smartShelf),
      ('invoice', Icons.receipt_long_rounded, l.smartInvoice),
      ('labels', Icons.sell_outlined, l.smartLabels),
    ];
    final cs = Theme.of(context).colorScheme;
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < tabs.length; i++) ...[
            if (i > 0) const SizedBox(width: 6),
            Expanded(
              child: Semantics(
                button: true,
                selected: _mode == tabs[i].$1,
                child: Material(
                  color: _mode == tabs[i].$1 ? cs.secondaryContainer : Colors.transparent,
                  shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: cs.outline)),
                  clipBehavior: Clip.antiAlias,
                  child: InkWell(
                    key: Key('cam_mode_${tabs[i].$1}'),
                    onTap: () => setState(() => _mode = tabs[i].$1),
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(minHeight: 56),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6),
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Icon(tabs[i].$2, size: 20, color: _mode == tabs[i].$1 ? cs.onSecondaryContainer : cs.onSurface),
                            const SizedBox(height: 2),
                            FittedBox(
                              fit: BoxFit.scaleDown,
                              child: Text(
                                tabs[i].$3,
                                maxLines: 1,
                                softWrap: false,
                                style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: _mode == tabs[i].$1 ? cs.onSecondaryContainer : cs.onSurface),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final known = _items.where((x) => x.product != null).toList();
    final fresh = _items.where((x) => x.product == null).toList();
    final count = _items.where((x) => x.qty > 0).length;
    return Scaffold(
      appBar: ScreenTitle(l.smartCam, subtitle: _shots > 0 ? l.smartShots(_shots) : null),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 140),
        children: [
          _modeTabs(l),
          const SizedBox(height: 12),
          // большая «кнопка-видоискатель»: снимок системной камерой
          InkWell(
            onTap: _busy ? null : _shoot,
            borderRadius: BorderRadius.circular(22),
            child: Container(
              height: _items.isEmpty ? 220 : 120,
              decoration: BoxDecoration(
                color: T.navy,
                borderRadius: BorderRadius.circular(22),
                border: Border.all(color: T.accent.withValues(alpha: .6), width: 2),
              ),
              child: Stack(
                children: [
                  for (final (a, b) in [(Alignment.topLeft, 0.0), (Alignment.topRight, 1.0), (Alignment.bottomLeft, 2.0), (Alignment.bottomRight, 3.0)])
                    Align(
                      alignment: a,
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Transform.rotate(
                          angle: b * 1.5708,
                          child: const Icon(Icons.crop_free_rounded, color: T.accent, size: 22),
                        ),
                      ),
                    ),
                  Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Container(
                          width: 64,
                          height: 64,
                          decoration: const BoxDecoration(color: T.accent, shape: BoxShape.circle),
                          child: const Icon(Icons.photo_camera_rounded, color: T.navy, size: 32),
                        ),
                        const SizedBox(height: 8),
                        Text(
                          _items.isEmpty ? l.takePhoto : l.morePhoto,
                          style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800),
                        ),
                        if (_items.isEmpty)
                          Padding(
                            padding: const EdgeInsets.fromLTRB(24, 4, 24, 0),
                            child: Text(
                              l.smartHint,
                              textAlign: TextAlign.center,
                              style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12),
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
          // 2026-10-27 (владелец: «в накладной добавь выбор из галереи и из файла»; «не только из камеры, но и из галереи»):
          // «Из галереи» — во всех режимах (сразу под зоной снимка, в прокручиваемом списке), «Из файла» (PDF, Excel, CSV, XML) — только в накладной
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  key: const Key('cam_gallery'),
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: _busy ? null : _fromGallery,
                  icon: const Icon(Icons.photo_library_outlined),
                  label: Text(l.camFromGallery, maxLines: 1, overflow: TextOverflow.ellipsis),
                ),
              ),
              if (_mode == 'invoice') ...[
                const SizedBox(width: 10),
                Expanded(
                  child: OutlinedButton.icon(
                    key: const Key('cam_file'),
                    style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                    onPressed: _busy ? null : _fromFile,
                    icon: const Icon(Icons.attach_file_rounded),
                    label: Text(l.camFromFile, maxLines: 1, overflow: TextOverflow.ellipsis),
                  ),
                ),
              ],
            ],
          ),
          if (_mode == 'invoice')
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(l.camInvoiceSrcHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
            ),
          if (_summary != null)
            Padding(
              padding: const EdgeInsets.only(top: 12),
              child: Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(color: T.warn.withValues(alpha: .14), borderRadius: BorderRadius.circular(12), border: Border.all(color: T.warn)),
                child: Text(_summary!, style: const TextStyle(fontWeight: FontWeight.w700)),
              ),
            ),
          if (known.isNotEmpty) ...[
            FieldLabel(l.seenInBase(known.length)),
            for (final x in known) Padding(padding: const EdgeInsets.only(bottom: 6), child: _knownCard(x)),
          ],
          if (fresh.isNotEmpty) ...[
            FieldLabel(l.seenNew(fresh.length)),
            Text(l.smartPriceRule, style: TextStyle(color: pal.textSoft, fontSize: 12)),
            const SizedBox(height: 4),
            for (final x in fresh) Padding(padding: const EdgeInsets.only(bottom: 6), child: _newCard(x)),
          ],
        ],
      ),
      bottomNavigationBar: _items.isEmpty
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    // 2026-10-26 (П0, Б2): накладная «прямо на склад» без поставщика — только с правом «Закупки»
                    if (_mode != 'invoice' || (ref.read(apiProvider).session?.canProcure ?? false)) ...[
                      FilledButton(
                        style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
                        onPressed: _busy || count == 0 ? null : _upload,
                        child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.smartUpload(count)),
                      ),
                      const SizedBox(height: 6),
                    ],
                    OutlinedButton(
                      onPressed: _busy ? null : _toReceive,
                      child: Text(l.smartToReceive),
                    ),
                  ],
                ),
              ),
            ),
    );
  }
}
