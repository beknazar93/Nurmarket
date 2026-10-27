// 2026-10-27 (мобилка, видеозвонок, М2): мастер «добавить товар» прямо в видеозвонке. Жалоба владельца: «при добавлении товара
// в видео вызове сразу диктуй команду добавления товара: 3–4 стороны фото и сканер штрихкода». Сценарий: спереди → сзади →
// сбоку → (по желанию ещё сторона) → штрихкод → проверка дубля → название (справочник NurCRM или ИИ по фото) → цена, закупка,
// остаток ГОЛОСОМ → итог вслух и «да» → создание → отмена удалением созданного.
//
// Мастер — обычный Dart-класс без Flutter-виджетов и без сети: камера, справочник, ИИ и создание подставляются
// ([WizardCamera], [WizardBackend]); в тестах — моки. Снимки живут ТОЛЬКО в памяти звонка (в журнал и на диск не попадают
// до создания товара). Деньги: цены и остаток — только названные вслух (это проверяют ворота инструмента), перед созданием
// программа сама зачитывает итог и ждёт отдельного «да» (реплика позже вопроса). Один и тот же мастер ведут и инструмент
// `product_wizard`, и кнопки на экране звонка.
import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/foundation.dart' show ChangeNotifier;

import '../../db/database.dart';
import '../live_tools.dart' show UserTurn;
import 'action_tools.dart' show ActionHost;
import 'call_focus.dart';
import 'call_product_search.dart' show ScoredProductSearch, confidentHit;
import 'call_table.dart';
import 'intent_gate.dart' show saidYes;
import 'tool_registry.dart' show ToolStatus;
import 'wizard_texts.dart';

/// Сторона упаковки. [extra] — необязательная четвёртая.
enum WizardSide { front, back, side, extra, barcode }

/// Снимок с потока камеры звонка: JPEG и штрихкод, прочитанный со снимка; [error] — понятный текст, если не вышло.
typedef WizardStill = ({Uint8List? bytes, String? barcode, String? error});

typedef WizardCamera = Future<WizardStill> Function();

/// Черновик товара для создания.
class WizardDraft {
  final String name;
  final double price;
  final double purchase;
  final double qty;
  final String category;
  final String brand;
  final String unit;
  final String barcode;

  /// Снимки по порядку: спереди (главное фото), сзади, сбоку, ещё.
  final List<Uint8List> shots;
  const WizardDraft({
    required this.name,
    required this.price,
    this.purchase = 0,
    this.qty = 0,
    this.category = '',
    this.brand = '',
    this.unit = 'шт',
    this.barcode = '',
    this.shots = const [],
  });
}

/// Внешний мир мастера: справочник NurCRM, ИИ по фото, создание в NurCRM. Рабочая реализация — в call_controller.dart.
abstract class WizardBackend {
  /// Название по штрихкоду из справочника NurCRM (встроенного и серверного); null — нет.
  Future<({String name, String? category, String? brand})?> globalInfo(String code);

  /// Название товара по фото (ИИ); null — не определилось.
  Future<String?> nameFromPhoto(Uint8List jpeg);

  /// Создать товар (карточка, остаток, фото, эталоны умной камеры, обновить каталог телефона). Возвращает id.
  /// `ApiException` — наружу: исход «неизвестен» (обрыв) отличает вызывающий код.
  Future<String> create(WizardDraft d);

  /// Обучить умную камеру снимками уже существующего товара (эталоны + фото). Возвращает, сколько снимков принято.
  Future<int> teach(Product p, List<Uint8List> shots);
}

/// Ответ шага мастера: статус инструмента и готовая фраза (её произносит модель как есть).
class WizardOut {
  final ToolStatus status;
  final String say;
  final Map<String, dynamic>? data;

  /// Исключение создания (для вызывающего кода: обрыв связи или отказ сервера); в JSON модели не уходит.
  final Object? error;
  const WizardOut(this.status, this.say, {this.data, this.error});
  bool get ok => status == ToolStatus.ok;
}

/// Что создано (для отмены и журнала).
class WizardCreated {
  final String id;
  final String name;
  final double price;
  final double qty;
  final int photos;
  final bool queued;
  const WizardCreated(this.id, this.name, this.price, this.qty, this.photos, {this.queued = false});
}

final _codeRe = RegExp(r'^\d{6,14}$');

String _sp(String s) => s.replaceAll(' ', ' ').replaceAll(' ', ' ');

class ProductWizard extends ChangeNotifier implements WizardHook {
  final ActionHost host;
  final WizardBackend backend;
  final WizardCamera camera;
  final bool Function() cameraOn;
  final CallFocus? focus;
  final DateTime Function() now;

  ProductWizard({required this.host, required this.backend, required this.camera, required this.cameraOn, this.focus, DateTime Function()? now})
    : now = now ?? DateTime.now;

  // --- состояние черновика
  @override
  bool active = false;
  final shots = <WizardSide, Uint8List>{};
  String barcode = '';
  bool barcodeSkipped = false;
  int barcodeFails = 0;
  String name = '';

  /// Откуда название: 'global' | 'photo' | 'user'.
  String nameFrom = '';
  String category = '';
  String brand = '';
  String unit = 'шт';
  double? price;
  double? purchase;
  double? qty;
  bool askedMore = false;

  /// «Хватит» сказали до всех сторон: остальные снимки не просим (нужен хотя бы «спереди»).
  bool photosEnough = false;
  bool busy = false;

  /// Товар, у которого уже есть этот штрихкод.
  Product? duplicate;

  /// Похожий по названию товар магазина (подсказка, создание не запрещает).
  Product? similar;

  /// Когда началось и когда снят последний кадр (по реплике пользователя): новый кадр — только после новой реплики.
  DateTime? _startedTurnAt;
  DateTime? _lastShotTurnAt;

  /// Итог был зачитан вслух: отпечаток черновика и момент.
  String? _askedFp;
  DateTime? _askedAt;

  /// Последний созданный товар (для отмены и сообщений).
  WizardCreated? lastCreated;

  String get fingerprint => '$name|$price|$purchase|$qty|$barcode|$barcodeSkipped|$category|$unit|${shots.length}';

  bool get hasFront => shots.containsKey(WizardSide.front);
  bool get ready => name.trim().isNotEmpty && (price ?? 0) > 0;
  int get shotCount => shots.length - (shots.containsKey(WizardSide.barcode) ? 1 : 0);

  /// Снимки по порядку для карточки: спереди (главное), сзади, сбоку, ещё; снимок штрихкода в карточку не идёт.
  List<Uint8List> get photoList => [
    for (final s in const [WizardSide.front, WizardSide.back, WizardSide.side, WizardSide.extra])
      if (shots[s] != null) shots[s]!,
  ];

  /// Следующая по порядку сторона, которой ещё нет; null — все три основные сняты.
  WizardSide? get nextSide {
    for (final s in const [WizardSide.front, WizardSide.back, WizardSide.side]) {
      if (!shots.containsKey(s)) return s;
    }
    return null;
  }

  void reset() {
    active = false;
    shots.clear();
    barcode = '';
    barcodeSkipped = false;
    barcodeFails = 0;
    name = '';
    nameFrom = '';
    category = '';
    brand = '';
    unit = 'шт';
    price = purchase = qty = null;
    askedMore = false;
    photosEnough = false;
    busy = false;
    duplicate = null;
    similar = null;
    _startedTurnAt = null;
    _lastShotTurnAt = null;
    _askedFp = null;
    _askedAt = null;
    notifyListeners();
  }

  // =================================================================================================================
  // старт и отмена
  // =================================================================================================================

  WizardOut start(String lang, {UserTurn? turn}) {
    if (!cameraOn()) return WizardOut(ToolStatus.clarify, wt('noCamera', lang));
    if (active) {
      return WizardOut(ToolStatus.ok, _promptSync(lang), data: _state());
    }
    reset();
    active = true;
    lastCreated = null;
    _startedTurnAt = turn?.at;
    notifyListeners();
    return WizardOut(ToolStatus.ok, wt('start', lang), data: _state());
  }

  @override
  String cancelNow(String lang) => cancel(lang).say;

  WizardOut cancel(String lang) {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    reset();
    return WizardOut(ToolStatus.ok, wt('cancelled', lang));
  }

  // =================================================================================================================
  // кадр
  // =================================================================================================================

  /// Снять кадр. [viaUi] — нажата кнопка «Снять» (подтверждение — само нажатие); иначе нужна НОВАЯ реплика пользователя
  /// («готово») после старта и после прошлого кадра: модель не снимает все стороны подряд сама.
  Future<WizardOut> shoot(WizardSide? side, String lang, {UserTurn? turn, bool viaUi = false}) async {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    if (busy) return WizardOut(ToolStatus.clarify, wt('waitTurn', lang));
    if (!viaUi) {
      final since = [?_startedTurnAt, ?_lastShotTurnAt].fold<DateTime?>(null, (a, b) => a == null || b.isAfter(a) ? b : a);
      if (turn == null || (since != null && !turn.at.isAfter(since))) return WizardOut(ToolStatus.clarify, wt('waitTurn', lang));
    }
    final target = side ?? nextSide ?? WizardSide.barcode;
    busy = true;
    notifyListeners();
    final WizardStill still;
    try {
      still = await camera();
    } catch (_) {
      busy = false;
      notifyListeners();
      return WizardOut(ToolStatus.error, wt('noCamera', lang));
    }
    busy = false;
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang)); // «отмена» пришла, пока снимали
    final bytes = still.bytes;
    if (bytes == null) {
      notifyListeners();
      return WizardOut(ToolStatus.clarify, still.error ?? wt('noCamera', lang));
    }
    final retake = shots.containsKey(target);
    shots[target] = bytes;
    _lastShotTurnAt = turn?.at ?? now();
    final read = (still.barcode ?? '').trim();
    final parts = <String>[
      if (retake) wt('retake', lang).trim(),
      wt(switch (target) {
        WizardSide.front => 'shotFront',
        WizardSide.back => 'shotBack',
        WizardSide.side => 'shotSide',
        WizardSide.extra => 'shotExtra',
        WizardSide.barcode => 'shotBarcode',
      }, lang),
    ];
    var gotNewCode = false;
    if (_codeRe.hasMatch(read) && read != barcode) {
      barcode = read;
      barcodeSkipped = false;
      barcodeFails = 0;
      gotNewCode = true;
      duplicate = null;
      parts.add(wt('barcodeRead', lang, {'code': read}));
    } else if (target == WizardSide.barcode && barcode.isEmpty) {
      barcodeFails++;
      parts.add(wt(barcodeFails >= 2 ? 'barcodeFail2' : 'barcodeFail1', lang));
      notifyListeners();
      return WizardOut(ToolStatus.ok, parts.join(' '), data: _state());
    }
    if (gotNewCode) {
      final dup = await _checkDuplicate(read, lang);
      if (dup != null) {
        notifyListeners();
        return WizardOut(ToolStatus.ok, '${parts.join(' ')} $dup', data: _state());
      }
    }
    parts.add(await _advance(lang, afterSide: target));
    notifyListeners();
    return WizardOut(ToolStatus.ok, parts.where((s) => s.isNotEmpty).join(' '), data: _state());
  }

  /// Штрихкод пришёл не с кадра (сказали цифры вслух, отсканировали сканером) — тот же путь: дубль, название.
  Future<WizardOut> acceptBarcode(String code, String lang) async {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    final c = code.replaceAll(RegExp(r'\s+'), '');
    if (!_codeRe.hasMatch(c)) return WizardOut(ToolStatus.clarify, wt('barcodeFail2', lang));
    barcode = c;
    barcodeSkipped = false;
    barcodeFails = 0;
    duplicate = null;
    final dup = await _checkDuplicate(c, lang);
    if (dup != null) {
      notifyListeners();
      return WizardOut(ToolStatus.ok, '${wt('barcodeRead', lang, {'code': c})} $dup', data: _state());
    }
    final next = await _advance(lang);
    notifyListeners();
    return WizardOut(ToolStatus.ok, '${wt('barcodeRead', lang, {'code': c})} $next', data: _state());
  }

  /// Товар с этим кодом уже есть? Тогда мастер останавливается и предлагает выбор. null — свободен.
  Future<String?> _checkDuplicate(String code, String lang) async {
    try {
      final f = await host.findByCode(code);
      final p = f.product;
      if (p == null) return null;
      duplicate = p;
      focus?.setLast(p);
      final stock = wt('stockPart', lang, {'qty': _sp(CallTable.formatCell(p.quantity, CellKind.qty, lang, unit: p.unit))});
      return wt('duplicate', lang, {'name': p.name, 'price': _sp(CallTable.formatCell(p.price, CellKind.money, lang)), 'stock': p.isService ? '' : stock});
    } catch (_) {
      return null; // нет связи — дубль проверит сервер при создании
    }
  }

  // =================================================================================================================
  // поля
  // =================================================================================================================

  /// Записать названное пользователем. Числа сюда приходят только после ворот инструмента (названы вслух).
  Future<WizardOut> setFields(
    String lang, {
    String? name,
    double? price,
    double? purchase,
    double? qty,
    String? category,
    String? unit,
    String? barcode,
    bool noBarcode = false,
    bool finish = false,
  }) async {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    if (price != null && price <= 0) return WizardOut(ToolStatus.clarify, wt('askPrice', lang));
    if ((purchase != null && purchase < 0) || (qty != null && qty < 0)) return WizardOut(ToolStatus.clarify, wt('askMore', lang));
    final parts = <String>[];
    if (name != null && name.trim().isNotEmpty) {
      this.name = name.trim();
      nameFrom = 'user';
      parts.add(wt('partName', lang, {'v': this.name}));
    }
    if (category != null && category.trim().isNotEmpty) {
      this.category = category.trim();
      parts.add(wt('partCategory', lang, {'v': this.category}));
    }
    if (unit != null && unit.trim().isNotEmpty) {
      this.unit = unit.trim();
      parts.add(wt('partUnit', lang, {'v': this.unit}));
    }
    if (price != null) {
      this.price = price;
      parts.add(wt('partPrice', lang, {'v': _sp(CallTable.formatCell(price, CellKind.money, lang))}));
    }
    if (purchase != null) {
      this.purchase = purchase;
      parts.add(wt('partPurchase', lang, {'v': _sp(CallTable.formatCell(purchase, CellKind.money, lang))}));
    }
    if (qty != null) {
      this.qty = qty;
      parts.add(wt('partQty', lang, {'v': _sp(CallTable.formatCell(qty, CellKind.qty, lang, unit: this.unit))}));
    }
    String? dupSay;
    if (noBarcode) {
      this.barcode = '';
      barcodeSkipped = true;
      duplicate = null;
      parts.add(wt('noBarcode', lang).replaceAll('.', ''));
    } else if (barcode != null && barcode.trim().isNotEmpty) {
      final c = barcode.replaceAll(RegExp(r'\s+'), '');
      if (_codeRe.hasMatch(c)) {
        this.barcode = c;
        barcodeSkipped = false;
        duplicate = null;
        parts.add(wt('partCode', lang, {'v': c}));
        dupSay = await _checkDuplicate(c, lang);
      }
    }
    if (finish) {
      askedMore = true;
      if (hasFront) photosEnough = true;
    }
    if (parts.isEmpty && !finish) {
      notifyListeners();
      return WizardOut(ToolStatus.clarify, await _advance(lang));
    }
    var say = parts.isEmpty ? '' : wt('recorded', lang, {'parts': parts.join(', ')});
    if (price != null && (this.purchase ?? 0) > 0 && price < this.purchase! - 0.004) say += wt('belowCost', lang);
    if (purchase != null && (this.price ?? 0) > 0 && this.price! < purchase - 0.004) say += wt('belowCost', lang);
    if (dupSay != null) {
      notifyListeners();
      return WizardOut(ToolStatus.ok, '$say $dupSay'.trim(), data: _state());
    }
    say = '$say ${await _advance(lang)}'.trim();
    notifyListeners();
    return WizardOut(ToolStatus.ok, say, data: _state());
  }

  // =================================================================================================================
  // что дальше (одна фраза)
  // =================================================================================================================

  String _promptSync(String lang) {
    if (!hasFront) return wt('start', lang);
    if (!photosEnough && !shots.containsKey(WizardSide.back)) return wt('askBack', lang);
    if (!photosEnough && !shots.containsKey(WizardSide.side)) return wt('askSide', lang);
    if (barcode.isEmpty && !barcodeSkipped) return wt('askBarcode', lang);
    if (name.trim().isEmpty) return wt('nameNone', lang);
    if ((price ?? 0) <= 0) return wt('askPrice', lang);
    return summary(lang);
  }

  /// Следующий шаг: недостающая сторона → штрихкод → название → цена → «закупка и остаток» → итог.
  Future<String> _advance(String lang, {WizardSide? afterSide}) async {
    if (!hasFront) return wt('start', lang);
    if (!photosEnough && !shots.containsKey(WizardSide.back)) return wt('askBack', lang);
    if (!photosEnough && !shots.containsKey(WizardSide.side)) return wt('askSide', lang);
    if (barcode.isEmpty && !barcodeSkipped) return afterSide == WizardSide.extra ? wt('askBarcodeOnly', lang) : wt('askBarcode', lang);
    if (name.trim().isEmpty) {
      final n = await _resolveName(lang);
      if (n != null) return n;
      return wt('nameNone', lang);
    }
    if ((price ?? 0) <= 0) return wt('askPrice', lang);
    if (!askedMore && (purchase == null || qty == null)) {
      askedMore = true;
      return wt('askMore', lang);
    }
    return summary(lang);
  }

  /// Название: справочник NurCRM по штрихкоду, иначе ИИ по снимку «спереди». Возвращает фразу или null.
  Future<String?> _resolveName(String lang) async {
    if (barcode.isNotEmpty) {
      try {
        final g = await backend.globalInfo(barcode).timeout(const Duration(seconds: 4));
        if (g != null && g.name.trim().isNotEmpty) {
          name = g.name.trim();
          nameFrom = 'global';
          if (category.isEmpty && (g.category ?? '').trim().isNotEmpty) category = g.category!.trim();
          if ((g.brand ?? '').trim().isNotEmpty) brand = g.brand!.trim();
          return wt('nameFound', lang, {'name': name, 'src': wt('srcGlobal', lang)}) + await _similarNote(lang);
        }
      } catch (_) {
        // справочник недоступен — пробуем по фото
      }
    }
    final front = shots[WizardSide.front] ?? photoList.firstOrNull;
    if (front != null) {
      try {
        final n = await backend.nameFromPhoto(front).timeout(const Duration(seconds: 14));
        if (n != null && n.trim().isNotEmpty) {
          name = n.trim();
          nameFrom = 'photo';
          return wt('nameFound', lang, {'name': name, 'src': wt('srcPhoto', lang)}) + await _similarNote(lang);
        }
      } catch (_) {
        // ИИ недоступен — название скажет пользователь
      }
    }
    return null;
  }

  /// Название найдено — не заведён ли уже похожий товар (матчер по всему каталогу). Только подсказка: создавать можно.
  Future<String> _similarNote(String lang) async {
    final h = host;
    if (h is! ScoredProductSearch) return '';
    try {
      final hits = await (h as ScoredProductSearch).findScored(name, top: 3);
      final best = confidentHit(hits);
      if (best == null || best.score < 0.9) return '';
      similar = best.product;
      focus?.setLast(best.product);
      return ' ${wt('similar', lang, {'name': best.product.name, 'price': _sp(CallTable.formatCell(best.product.price, CellKind.money, lang))})}';
    } catch (_) {
      return '';
    }
  }

  /// Итог для подтверждения вслух. Зачитанный итог запоминается: создать можно только то, что программа зачитала.
  String summary(String lang) {
    String m(double v) => _sp(CallTable.formatCell(v, CellKind.money, lang));
    final extra = StringBuffer();
    if ((purchase ?? 0) > 0) extra.write(wt('extraPurchase', lang, {'v': m(purchase!)}));
    if ((qty ?? 0) > 0) extra.write(wt('extraQty', lang, {'v': _sp(CallTable.formatCell(qty!, CellKind.qty, lang, unit: unit))}));
    extra.write(barcode.isEmpty ? wt('extraNoCode', lang) : wt('extraCode', lang, {'v': barcode}));
    final photos = photoList.length;
    if (photos > 0) extra.write(wt('extraPhotos', lang, {'v': '$photos'}));
    _askedFp = fingerprint;
    _askedAt = now();
    var s = wt('summary', lang, {'name': name, 'price': m(price ?? 0), 'extra': extra.toString()});
    if ((purchase ?? 0) > 0 && (price ?? 0) < purchase! - 0.004) s += wt('belowCost', lang);
    return s;
  }

  // =================================================================================================================
  // создание
  // =================================================================================================================

  /// Создать товар. Без [viaUi] нужно, чтобы итог был зачитан и после этого прозвучала новая реплика («да»).
  Future<WizardOut> create(String lang, {UserTurn? turn, bool viaUi = false}) async {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    if (busy) return WizardOut(ToolStatus.clarify, wt('waitTurn', lang));
    if (name.trim().isEmpty) return WizardOut(ToolStatus.clarify, wt('needName', lang));
    if ((price ?? 0) <= 0) return WizardOut(ToolStatus.clarify, wt('needPrice', lang));
    if (duplicate != null && barcode.isNotEmpty) return WizardOut(ToolStatus.clarify, wt('dupBlocks', lang, {'name': duplicate!.name}));
    if (!viaUi) {
      final asked = _askedAt;
      if (_askedFp != fingerprint || asked == null) {
        return WizardOut(ToolStatus.clarify, wt('needSummaryFirst', lang, {'summary': summary(lang)}));
      }
      // «да» должно прозвучать после вопроса: реплика, начатая раньше вопроса, не считается
      // и это именно «да», а не «нет, не создавай»
      if (turn == null || !turn.at.isAfter(asked) || !saidYes(turn.text)) {
        return WizardOut(ToolStatus.clarify, wt('needSummaryFirst', lang, {'summary': summary(lang)}));
      }
    }
    final draft = WizardDraft(
      name: name.trim(),
      price: price!,
      purchase: purchase ?? 0,
      qty: qty ?? 0,
      category: category,
      brand: brand,
      unit: unit,
      barcode: barcode,
      shots: photoList,
    );
    busy = true;
    notifyListeners();
    try {
      final id = await backend.create(draft);
      busy = false;
      final done = WizardCreated(id, draft.name, draft.price, draft.qty, draft.shots.length);
      lastCreated = done;
      final p = await host.productById(id);
      if (p != null) focus?.setLast(p);
      reset();
      String m(double v) => _sp(CallTable.formatCell(v, CellKind.money, lang));
      final stock = draft.qty > 0 ? wt('stockPart', lang, {'qty': _sp(CallTable.formatCell(draft.qty, CellKind.qty, lang, unit: draft.unit))}) : '';
      return WizardOut(
        ToolStatus.ok,
        wt('created', lang, {'name': draft.name, 'price': m(draft.price), 'stock': stock, 'photos': '${draft.shots.length}'}),
        data: {'id': id, 'name': draft.name, 'price': draft.price, 'qty': draft.qty, 'photos': draft.shots.length},
      );
    } catch (e) {
      busy = false;
      notifyListeners();
      return WizardOut(ToolStatus.error, wt('createFailed', lang, {'why': e is Exception ? '$e'.replaceFirst('Exception: ', '') : '…'}), error: e);
    }
  }

  /// Обучить камеру снимками: товар-дубль, а если его нет — товар с последней карточки.
  Future<WizardOut> teach(String lang) async {
    if (!active) return WizardOut(ToolStatus.clarify, wt('notActive', lang));
    final p = duplicate ?? similar ?? focus?.last;
    final list = photoList;
    if (p == null || list.isEmpty) return WizardOut(ToolStatus.clarify, wt('teachNone', lang));
    try {
      final n = await backend.teach(p, list);
      reset();
      return WizardOut(ToolStatus.ok, wt('taught', lang, {'n': '$n', 'name': p.name}), data: {'id': p.id, 'photos': n});
    } catch (_) {
      return WizardOut(ToolStatus.error, wt('teachNone', lang));
    }
  }

  Map<String, dynamic> _state() => {
    'step': nextSide?.name ?? (barcode.isEmpty && !barcodeSkipped ? 'barcode' : (name.isEmpty ? 'name' : ((price ?? 0) <= 0 ? 'price' : 'confirm'))),
    'photos': photoList.length,
    if (barcode.isNotEmpty) 'barcode_read': true,
    if (duplicate != null) 'duplicate': duplicate!.name,
    if (name.isNotEmpty) 'name': name,
    if (price != null) 'price': price,
    if (purchase != null) 'purchase': purchase,
    if (qty != null) 'qty': qty,
  };
}
