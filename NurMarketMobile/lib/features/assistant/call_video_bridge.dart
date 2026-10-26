// 2026-10-26 (видеозвонок, В2 + голос-управление, В4): «мост к каталогу». ИИ в звонке видит картинку, но цену и остаток называет
// ПРОГРАММА: по кнопке «Что это? / Накладная / Полка / Ценник» (или по фразе, [videoIntent]) делается чёткий снимок, ищется
// штрихкод со снимка, товар — в каталоге телефона, остальное — готовыми функциями умной камеры (vision.recognize + match).
// Результат — карточка на экране ([VideoCard]) и сообщение «[Программа] …» для ИИ. Ничего не проводится и не меняется.
// Распознавание подменяется ([VideoRecognizer]) — виджет-тесты идут без сети и камеры.
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/assistant/read_tools.dart' show Reader;
import '../../core/ai/live_video.dart' show VideoStill;
import '../../core/ai/vision.dart';
import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../l10n/app_localizations.dart';
import '../admin/admin_receive.dart' show ReceiveLine;
import '../scan/code_lookup.dart';
import '../scan/smart_camera.dart' show camSyncLine;

enum VideoAsk { product, invoice, shelf, label }

String _norm(String s) => s.toLowerCase().replaceAll('ё', 'е').replaceAll(RegExp('[’ʻʼ`´]'), "'").replaceAll(RegExp(r'[?!.,…]+'), ' ').replaceAll(RegExp(r'\s+'), ' ').trim();

int _words(String s) => s.isEmpty ? 0 : s.split(' ').length;

/// `\b` в Dart не знает кириллицу, поэтому границы слова — через пробелы: ведущий `\b` → «после пробела», остальные → «перед пробелом».
String _wordBounds(String p) {
  final out = StringBuffer();
  for (var i = 0; i < p.length; i++) {
    if (p.startsWith(r'\b', i)) {
      final lead = i == 0 || p[i - 1] == '(' || p[i - 1] == '|';
      out.write(lead ? r'(?<=\s)' : r'(?=\s)');
      i++;
    } else {
      out.write(p[i]);
    }
  }
  return out.toString();
}

bool _any(String t, List<String> patterns) {
  final padded = ' $t ';
  return patterns.any((p) => RegExp(_wordBounds(p)).hasMatch(padded));
}

/// Намерение из расшифровки речи (раздел 3.7 ТЗ видеозвонка): фраза → то же, что нажатие кнопки. Чистая функция.
/// Вопросы с цифрами без «это / этот / бул / bu / this» («сколько продали вчера») — НЕ product.
VideoAsk? videoIntent(String said) {
  final t = _norm(said);
  if (t.isEmpty) return null;
  final short = _words(t) <= 4;
  final printing = _any(t, [r'напечата|распечата|печат|print|басып чыг|chop et|yazdır']);

  // ценник
  if (!printing &&
      _any(t, [r'ценник', r'этикетк', r'баа белгиси', r"narx yorlig'i", r'etiketka', r'price tag', r'\blabel\b', r'fiyat etiketi', r'\betiket\b']) &&
      (short || _any(t, [r'совпад', r'правильн', r'провер', r'сверь', r'какой', r'что', r'посмотр', r'туура', r'дал кел', r'mos', r'match', r'correct', r'check', r'uyuş', r'doğru', r'ne yaz']))) {
    return VideoAsk.label;
  }
  // накладная
  if (_any(t, [r'накладн', r'накладной', r"nakladnoy", r'invoice', r'delivery note', r'irsaliye', r'fatura']) &&
      (short || _any(t, [r'прими', r'разбери', r'распозна', r'вот', r'смотри', r'это', r'кабыл ал', r'эми', r'qabul', r'mana', r'read', r'scan', r'this', r'al\b', r'bu '])) &&
      !_any(t, [r'вчера|сегодня|за неделю|за месяц|сколько|канча|qancha|how many|kaç'])) {
    return VideoAsk.invoice;
  }
  if (_any(t, [r'прими приход', r'прими товар по'])) return VideoAsk.invoice;
  // полка
  if (_any(t, [r'\bполк', r'\bтекче', r'\bjavon', r'\bshelf\b', r'\braf\b', r'санап чык', r'sanab chiq']) ||
      (_any(t, [r'пересчитай']) && _any(t, [r'это|тут|здесь|товар']))) {
    return VideoAsk.shelf;
  }
  // товар: только с «это / этот / бул / bu / this»
  if (_any(t, [
    r'\bчто это\b', r'\bэто что\b', r'сколько стоит (это|этот|эта|эти)\b', r'сколько осталось (этого|этой|этих)\b', r'найди это\b',
    r'бул эмне', r'бул канча турат', r'мунун баасы', r'бул канча калды',
    r'\bbu nima\b', r'\bbu qancha turadi\b', r'\bbuning narxi\b',
    r'what is this\b', r'what.?s this\b', r'how much is this\b', r'is this in stock\b', r'how many of this\b',
    r'\bbu ne\b', r'bu kaç para', r'bundan kaç var',
  ])) {
    return VideoAsk.product;
  }
  return null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Карточки результата
// ---------------------------------------------------------------------------------------------------------------------

sealed class VideoCard {
  const VideoCard();

  /// Сообщение ИИ («[Программа] …», по-русски, как остальные) и просить ли его ответить вслух.
  String get aiMessage;
  bool get aiRespond => true;

  /// Строка для чата после звонка (как таблицы).
  String get chatText;
}

class VideoProductCard extends VideoCard {
  final Product? product;
  final String name;
  final double price;

  /// Остаток; null — нет права «Склад» (в карточку и в ИИ не попадает).
  final double? stock;
  final String unit;

  /// Совпал штрихкод — «✓ точно».
  final bool exactBarcode;

  /// Похоже на … (до 3 вариантов): нажатие выбирает.
  final List<Product> alternatives;
  const VideoProductCard({this.product, this.name = '', this.price = 0, this.stock, this.unit = '', this.exactBarcode = false, this.alternatives = const []});

  bool get found => product != null;
  bool get looksLike => product == null && alternatives.isNotEmpty;
  bool get notFound => product == null && alternatives.isEmpty;

  VideoProductCard choose(Product p, {required bool canStock}) => VideoProductCard(
    product: p,
    name: p.name,
    price: p.price,
    stock: canStock ? p.quantity : null,
    unit: p.unit,
  );

  static String _n(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toString().replaceAll('.', ',');

  @override
  String get aiMessage {
    if (notFound) return '[Программа] Камера: в каталоге не нашла. Предложи поднести ближе или показать штрихкод.';
    if (looksLike) {
      return '[Программа] Камера: товар не определён точно, похоже на: ${alternatives.map((p) => '«${p.name}» ${_n(p.price)} сом').join('; ')}. Скажи это коротко и попроси выбрать на экране. Ничего не меняй.';
    }
    return '[Программа] Камера: товар «$name», цена ${_n(price)} сом${stock == null ? '' : ', остаток ${_n(stock!)} $unit'}${exactBarcode ? ', точно по штрихкоду' : ', похоже, не точно'}. Скажи это коротко. Ничего не меняй.';
  }

  @override
  String get chatText => notFound
      ? 'Камера: в каталоге не нашла.'
      : looksLike
      ? 'Камера: похоже на ${alternatives.map((p) => '«${p.name}»').join(', ')}'
      : 'Камера: «$name» — ${_n(price)} сом${stock == null ? '' : ', остаток ${_n(stock!)} $unit'}';
}

class VideoInvoiceLine {
  final String name;
  final double qty;
  final double cost;
  final bool known;
  const VideoInvoiceLine(this.name, this.qty, this.cost, this.known);
}

class VideoInvoiceCard extends VideoCard {
  final int lines;
  final int known;
  final int fresh;
  final List<VideoInvoiceLine> preview;

  /// Строки для приёмки: только найденные в каталоге (новые через «Склад → Накладная»).
  final List<ReceiveLine> ready;
  final bool canProcure;
  const VideoInvoiceCard({required this.lines, required this.known, required this.fresh, this.preview = const [], this.ready = const [], this.canProcure = false});

  @override
  String get aiMessage => canProcure
      ? '[Программа] Накладная: строк $lines, найдено в каталоге $known, новых $fresh. На экране кнопка «Открыть приход». Сам ничего не проводи.'
      : '[Программа] Накладная: строк $lines, найдено в каталоге $known, новых $fresh. Приход проводит администратор. Сам ничего не проводи.';

  @override
  String get chatText => 'Накладная: строк $lines, в каталоге $known, новых $fresh';
}

class VideoShelfRow {
  final String name;
  final int seen;
  final double? have;
  const VideoShelfRow(this.name, this.seen, this.have);
}

class VideoShelfCard extends VideoCard {
  final List<VideoShelfRow> rows;
  const VideoShelfCard(this.rows);

  @override
  String get aiMessage =>
      '[Программа] Полка: ${rows.take(5).map((r) => '${r.name}: видно ${r.seen}, в программе ${r.have == null ? '—' : VideoProductCard._n(r.have!)}').join('; ')}. Назови главные расхождения, скажи, что ИИ считает на глаз.';

  @override
  String get chatText => 'Полка (на глаз): ${rows.take(8).map((r) => '${r.name} — видно ${r.seen}, в программе ${r.have == null ? '—' : VideoProductCard._n(r.have!)}').join('; ')}';
}

class VideoTagCard extends VideoCard {
  final String name;
  final double tag;
  final double price;
  const VideoTagCard(this.name, this.tag, this.price);
  bool get ok => (tag - price).abs() < 0.01;

  @override
  String get aiMessage =>
      '[Программа] Ценник: на ценнике ${VideoProductCard._n(tag)} сом, в программе ${VideoProductCard._n(price)} сом — ${ok ? 'совпадает' : 'не совпадает'}.';

  @override
  String get chatText => 'Ценник «$name»: на ценнике ${VideoProductCard._n(tag)} сом, в программе ${VideoProductCard._n(price)} сом — ${ok ? 'совпадает' : 'не совпадает'}';
}

/// Распознать не удалось. [text] — понятный текст на языке интерфейса; [retry] — кнопка «Повторить».
class VideoErrorCard extends VideoCard {
  final String text;
  final bool retry;
  const VideoErrorCard(this.text, {this.retry = true});

  @override
  String get aiMessage => '[Программа] Распознать не удалось. Скажи, что не получилось, и предложи повторить.';
  @override
  bool get aiRespond => false;
  @override
  String get chatText => text;
}

// ---------------------------------------------------------------------------------------------------------------------
// Распознавание
// ---------------------------------------------------------------------------------------------------------------------

class VideoAskContext {
  final bool canStock;
  final bool canProcure;
  final AppLocalizations l;
  const VideoAskContext({required this.canStock, required this.canProcure, required this.l});
}

abstract class VideoRecognizer {
  /// Чёткий снимок → карточка. Не бросает: ошибки — [VideoErrorCard].
  Future<VideoCard> run(VideoAsk ask, VideoStill still, VideoAskContext ctx);
}

/// Понятный текст ошибки ИИ (то же, что aiErrorText умной камеры, но без BuildContext).
String visionErrorText(AppLocalizations l, VisionException e) {
  final m = e.message.toLowerCase();
  if (e.noKey) return l.aiKeyNeeded;
  if (m.contains('связи') || m.contains('connection') || m.contains('socket')) return l.aiErrNoNet;
  if (m.contains('вовремя') || m.contains('timeout')) return l.aiErrSlow;
  if (m.contains('лимит') || m.contains('429')) return l.aiErrLimit;
  if (m.contains('перегруж') || m.contains('недоступна') || m.contains('503')) return l.aiErrBusy;
  return l.aiErrOther;
}

/// Рабочее распознавание: каталог телефона и тот же ключ Gemini, что у умной камеры. Эталоны обучения (RefCache) здесь не
/// используются — «✓ точно» только по штрихкоду.
class VisionVideoRecognizer implements VideoRecognizer {
  final Reader read;
  VisionVideoRecognizer(this.read);

  @override
  Future<VideoCard> run(VideoAsk ask, VideoStill still, VideoAskContext ctx) async {
    final bytes = still.bytes;
    if (bytes == null) return VideoErrorCard(ctx.l.aiErrOther);
    try {
      return switch (ask) {
        VideoAsk.product => await _product(bytes, still.barcode, ctx),
        VideoAsk.invoice => await _invoice(bytes, ctx),
        VideoAsk.shelf => await _shelf(bytes, ctx),
        VideoAsk.label => await _label(bytes, still.barcode, ctx),
      };
    } on VisionException catch (e) {
      return VideoErrorCard(visionErrorText(ctx.l, e));
    } on ApiException catch (e) {
      return VideoErrorCard(e.message.isEmpty ? ctx.l.aiErrNoNet : e.message);
    } catch (_) {
      return VideoErrorCard(ctx.l.aiErrOther);
    }
  }

  Future<Product?> _byCode(String? code) async {
    if (code == null || code.isEmpty) return null;
    final r = await CodeLookup.find(read(databaseProvider), read(apiProvider), code);
    return r.product;
  }

  Future<VideoCard> _product(Uint8List jpeg, String? barcode, VideoAskContext ctx) async {
    final byCode = await _byCode(barcode);
    if (byCode != null) {
      return VideoProductCard(product: byCode, name: byCode.name, price: byCode.price, stock: ctx.canStock ? byCode.quantity : null, unit: byCode.unit, exactBarcode: true);
    }
    final vision = read(visionProvider);
    final matches = await vision.match(await vision.recognize(jpeg, mode: 'sale'));
    final first = matches.where((m) => m.candidates.isNotEmpty).firstOrNull;
    if (first == null) return const VideoProductCard();
    final top = first.candidates.first;
    if (top.$2 >= 0.99) {
      final p = top.$1;
      return VideoProductCard(product: p, name: p.name, price: p.price, stock: ctx.canStock ? p.quantity : null, unit: p.unit, exactBarcode: true);
    }
    return VideoProductCard(alternatives: [for (final c in first.candidates.take(3)) c.$1]);
  }

  Future<VideoCard> _invoice(Uint8List jpeg, VideoAskContext ctx) async {
    final vision = read(visionProvider);
    final matches = await vision.match(await vision.recognize(jpeg, mode: 'invoice'));
    var known = 0;
    final lines = <VideoInvoiceLine>[];
    final ready = <ReceiveLine>[];
    for (final m in matches) {
      final p = m.best;
      if (p != null) {
        known++;
        ready.add(camSyncLine(null, product: p, name: p.name, barcode: '', qty: m.seen.count.toDouble(), cost: m.seen.labelPrice, price: p.price));
      }
      lines.add(VideoInvoiceLine(p?.name ?? m.seen.title, m.seen.count.toDouble(), m.seen.labelPrice, p != null));
    }
    return VideoInvoiceCard(lines: matches.length, known: known, fresh: matches.length - known, preview: lines, ready: ready, canProcure: ctx.canProcure);
  }

  Future<VideoCard> _shelf(Uint8List jpeg, VideoAskContext ctx) async {
    final vision = read(visionProvider);
    final matches = await vision.match(await vision.recognize(jpeg, mode: 'shelf'));
    return VideoShelfCard([for (final m in matches) VideoShelfRow(m.best?.name ?? m.seen.title, m.seen.count, m.best?.quantity)]);
  }

  Future<VideoCard> _label(Uint8List jpeg, String? barcode, VideoAskContext ctx) async {
    final vision = read(visionProvider);
    final seen = await vision.recognize(jpeg, mode: 'labels');
    final tag = seen.where((s) => s.labelPrice > 0).firstOrNull;
    var p = await _byCode(barcode);
    if (p == null && tag != null) p = (await vision.match([tag])).firstOrNull?.best;
    if (p == null || tag == null) return const VideoProductCard();
    return VideoTagCard(p.name, tag.labelPrice, p.price);
  }
}

/// Провайдер для тестов и приложения (подмена в тестах — overrideWithValue).
final videoRecognizerProvider = Provider<VideoRecognizer>((ref) => VisionVideoRecognizer(ref.read));
