import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../features/receipt/receipt.dart';
import '../db/database.dart';
import '../providers.dart';
import 'escpos.dart';

/// 2026-10-20, владелец: «в мобилке андроид флаттер исправь подключение к принтерам!». До этого печати на телефоне
/// не было вовсе (в профиле стояло «Bluetooth — в следующем обновлении»), поэтому ни один принтер не подключался.
/// Теперь три пути без новых пакетов: Bluetooth (классический SPP, UUID 00001101-…) и USB-OTG — свой код Android
/// (MainActivity.kt, канал kg.nurcrm.nurmarket/printer), сеть (Wi-Fi/LAN, порт 9100) — сокет Dart.
enum PrinterKind { none, bluetooth, network, usb }

class PrinterConfig {
  final PrinterKind kind;

  /// Bluetooth — MAC; сеть — IP или имя; USB — имя устройства Android (/dev/bus/usb/…).
  final String address;
  final String name;
  final int port;
  final int paperMm;
  final PrinterCodepage codepage;
  final int codeTable;
  final bool autoPrint;

  /// 2026-10-22 (мобилка, админ): П6 — денежный ящик через принтер, контакт (0 — 2, 1 — 5), QR на бумаге.
  final bool drawer;
  final int drawerPin;
  final bool qr;

  const PrinterConfig({
    this.kind = PrinterKind.none,
    this.address = '',
    this.name = '',
    this.port = 9100,
    this.paperMm = 58,
    this.codepage = PrinterCodepage.cp866,
    this.codeTable = 17,
    this.autoPrint = false,
    this.drawer = false,
    this.drawerPin = 0,
    this.qr = true,
  });

  bool get ready => kind != PrinterKind.none && address.trim().isNotEmpty;

  PrinterConfig copyWith({
    PrinterKind? kind,
    String? address,
    String? name,
    int? port,
    int? paperMm,
    PrinterCodepage? codepage,
    int? codeTable,
    bool? autoPrint,
    bool? drawer,
    int? drawerPin,
    bool? qr,
  }) => PrinterConfig(
    kind: kind ?? this.kind,
    address: address ?? this.address,
    name: name ?? this.name,
    port: port ?? this.port,
    paperMm: paperMm ?? this.paperMm,
    codepage: codepage ?? this.codepage,
    codeTable: codeTable ?? this.codeTable,
    autoPrint: autoPrint ?? this.autoPrint,
    drawer: drawer ?? this.drawer,
    drawerPin: drawerPin ?? this.drawerPin,
    qr: qr ?? this.qr,
  );

  Map<String, Object?> toJson() => {
    'kind': kind.name,
    'address': address,
    'name': name,
    'port': port,
    'paper': paperMm,
    'codepage': codepage.name,
    'table': codeTable,
    'auto': autoPrint,
    'drawer': drawer,
    'pin': drawerPin,
    'qr': qr,
  };

  static PrinterConfig fromJson(String? raw) {
    if (raw == null || raw.isEmpty) return const PrinterConfig();
    try {
      final m = jsonDecode(raw) as Map;
      final cp = PrinterCodepage.values.where((c) => c.name == m['codepage']).firstOrNull ?? PrinterCodepage.cp866;
      return PrinterConfig(
        kind: PrinterKind.values.where((k) => k.name == m['kind']).firstOrNull ?? PrinterKind.none,
        address: m['address']?.toString() ?? '',
        name: m['name']?.toString() ?? '',
        port: (m['port'] as num?)?.toInt() ?? 9100,
        paperMm: (m['paper'] as num?)?.toInt() == 80 ? 80 : 58,
        codepage: cp,
        codeTable: (m['table'] as num?)?.toInt() ?? defaultCodeTable(cp),
        autoPrint: m['auto'] == true,
        drawer: m['drawer'] == true,
        drawerPin: (m['pin'] as num?)?.toInt() == 1 ? 1 : 0,
        qr: m['qr'] != false,
      );
    } catch (_) {
      return const PrinterConfig();
    }
  }
}

/// Найденное устройство: Bluetooth (сопряжённое или найденное поиском), USB или адрес в сети.
class PrinterDevice {
  final String address;
  final String name;
  final bool paired;
  final bool isPrinter;
  const PrinterDevice(this.address, this.name, {this.paired = false, this.isPrinter = true});
}

/// Причина, по которой не напечаталось, — экран показывает понятный текст на языке кассира.
enum PrinterProblem { notConfigured, btUnsupported, btOff, noPermission, notFound, bleOnly, timeout, refused, usbDenied, usbNotFound, io }

class PrinterException implements Exception {
  final PrinterProblem problem;
  final String detail;

  /// 2026-10-22 (мобилка, админ): П6 — часть байтов уже ушла на принтер: повтор напечатал бы чек второй раз.
  final bool sent;
  const PrinterException(this.problem, [this.detail = '', this.sent = false]);
  @override
  String toString() => '$problem $detail';
}

class PrinterService {
  final AppDatabase? db;
  PrinterService(this.db);

  /// 2026-10-22 (мобилка, админ): П6 — отправка на принтер (для тестов — фейк). По умолчанию — настоящий принтер.
  Future<void> Function(PrinterConfig c, Uint8List bytes)? sendOverride;

  // 2026-10-22 (мобилка, админ): П6 — задания идут на принтер по одному (два подключения Bluetooth сразу рвут оба);
  // тот же чек, пока печатается, второй раз не ставится; ящик по одному чеку открывается один раз.
  Future<void> _queue = Future.value();
  final _inflight = <String, Future<void>>{};
  final _drawerDone = <String>{};

  static String _receiptKey(Receipt r) => '${r.number}|${r.at.millisecondsSinceEpoch}|${r.total}';

  Future<void> _enqueue(Future<void> Function() job) {
    final r = _queue.then((_) => job());
    _queue = r.then((_) {}, onError: (_) {});
    return r;
  }

  static const _channel = MethodChannel('kg.nurcrm.nurmarket/printer');
  static const _key = 'printer.config';

  Future<PrinterConfig> load() async => PrinterConfig.fromJson(await db?.getSetting(_key));
  Future<void> save(PrinterConfig c) async => await db?.setSetting(_key, jsonEncode(c.toJson()));

  static PrinterException _fromPlatform(PlatformException e) {
    final p = PrinterProblem.values.where((x) => x.name == e.code).firstOrNull ?? PrinterProblem.io;
    // 2026-10-22 (мобилка, админ): П6 — MainActivity.kt кладёт в details «sent», если байты начали уходить.
    return PrinterException(p, e.message ?? '', e.details == 'sent');
  }

  Future<T> _call<T>(String method, [Map<String, Object?>? args, Duration timeout = const Duration(seconds: 25)]) async {
    if (!Platform.isAndroid) throw const PrinterException(PrinterProblem.btUnsupported);
    try {
      final r = await _channel.invokeMethod<T>(method, args).timeout(timeout);
      return r as T;
    } on PlatformException catch (e) {
      throw _fromPlatform(e);
    } on MissingPluginException {
      throw const PrinterException(PrinterProblem.btUnsupported);
    } on TimeoutException {
      throw const PrinterException(PrinterProblem.timeout);
    }
  }

  /// Bluetooth: включён ли и есть ли разрешение (Android 12+ — «Устройства поблизости»).
  Future<({bool supported, bool enabled, bool permission})> btState() async {
    final m = await _call<Map>('btState');
    return (supported: m['supported'] == true, enabled: m['enabled'] == true, permission: m['permission'] == true);
  }

  /// Запросить разрешение Bluetooth (окно Android); true — дали.
  Future<bool> requestBtPermission({bool scan = false}) async => await _call<bool>('btRequestPermission', {'scan': scan}, const Duration(minutes: 2)) == true;

  /// Окно Android «Включить Bluetooth?».
  Future<void> enableBt() => _call<Object?>('btEnable');

  /// Настройки Bluetooth телефона — сопряжение принтера (PIN обычно 0000 или 1234).
  Future<void> openBtSettings() => _call<Object?>('btSettings');

  /// Сопряжённые устройства и (если [discover]) найденные поиском за ~8 с. Принтеры — первыми.
  Future<List<PrinterDevice>> btDevices({bool discover = false}) async {
    final list = await _call<List>('btDevices', {'discover': discover}, const Duration(seconds: 30));
    final out = [
      for (final e in list.whereType<Map>())
        PrinterDevice(
          e['address']?.toString() ?? '',
          (e['name']?.toString() ?? '').isEmpty ? (e['address']?.toString() ?? '') : e['name'].toString(),
          paired: e['paired'] == true,
          isPrinter: e['printer'] == true,
        ),
    ];
    out.sort((a, b) => (b.isPrinter ? 2 : 0) + (b.paired ? 1 : 0) - (a.isPrinter ? 2 : 0) - (a.paired ? 1 : 0));
    return out;
  }

  /// USB-устройства, подключённые через OTG; принтеры (класс 7 или с выходом bulk) — первыми.
  Future<List<PrinterDevice>> usbDevices() async {
    final list = await _call<List>('usbDevices');
    final out = [
      for (final e in list.whereType<Map>())
        PrinterDevice(e['device']?.toString() ?? '', e['name']?.toString() ?? '', isPrinter: e['printer'] == true),
    ];
    out.sort((a, b) => (b.isPrinter ? 1 : 0) - (a.isPrinter ? 1 : 0));
    return out;
  }

  /// Поиск сетевых принтеров: порт 9100 во всей подсети телефона (/24), по 48 адресов сразу, ~0,4 с на адрес.
  Future<List<PrinterDevice>> scanNetwork({int port = 9100}) async {
    final prefixes = <String>{};
    for (final ni in await NetworkInterface.list(type: InternetAddressType.IPv4)) {
      for (final a in ni.addresses) {
        final p = a.address.split('.');
        if (p.length == 4 && !a.isLoopback) prefixes.add('${p[0]}.${p[1]}.${p[2]}');
      }
    }
    final found = <PrinterDevice>[];
    for (final prefix in prefixes) {
      final hosts = [for (var i = 1; i < 255; i++) '$prefix.$i'];
      for (var i = 0; i < hosts.length; i += 48) {
        await Future.wait(
          hosts.skip(i).take(48).map((h) async {
            try {
              final s = await Socket.connect(h, port, timeout: const Duration(milliseconds: 400));
              s.destroy();
              found.add(PrinterDevice(h, '$h:$port'));
            } catch (_) {
              // нет принтера по этому адресу
            }
          }),
        );
      }
    }
    return found;
  }

  /// Отправить байты на принтер; при обрыве — ещё одна попытка через секунду (принтер мог «спать»).
  /// 2026-10-22 (мобилка, админ): П6 — повтор только если ни один байт не ушёл: обрыв посреди чека раньше давал
  /// второй (целый) чек и второй импульс ящика.
  Future<void> send(PrinterConfig c, Uint8List bytes) => _enqueue(() => _send(c, bytes));

  Future<void> _send(PrinterConfig c, Uint8List bytes) async {
    if (!c.ready) throw const PrinterException(PrinterProblem.notConfigured);
    PrinterException? last;
    for (var attempt = 0; attempt < 2; attempt++) {
      try {
        await (sendOverride ?? _sendOnce)(c, bytes);
        return;
      } on PrinterException catch (e) {
        last = e;
        // настройка или разрешение — повтор не поможет; байты ушли — повтор напечатает второй чек
        if (e.sent ||
            const {PrinterProblem.btOff, PrinterProblem.noPermission, PrinterProblem.btUnsupported, PrinterProblem.bleOnly, PrinterProblem.usbDenied, PrinterProblem.notConfigured}
                .contains(e.problem)) {
          rethrow;
        }
        await Future<void>.delayed(retryDelay);
      }
    }
    throw last!;
  }

  /// Пауза перед повтором (в тестах — ноль).
  Duration retryDelay = const Duration(seconds: 1);

  Future<void> _sendOnce(PrinterConfig c, Uint8List bytes) async {
    switch (c.kind) {
      case PrinterKind.network:
        Socket? s;
        // 2026-10-22 (мобилка, админ): П6 — после подключения байты считаются ушедшими (повтор не нужен).
        var connected = false;
        try {
          s = await Socket.connect(c.address.trim(), c.port, timeout: const Duration(seconds: 5));
          connected = true;
          s.add(bytes);
          await s.flush().timeout(const Duration(seconds: 10));
          await s.close().timeout(const Duration(seconds: 5));
        } on TimeoutException {
          throw PrinterException(PrinterProblem.timeout, '', connected);
        } on SocketException catch (e) {
          final code = e.osError?.errorCode ?? 0;
          // 111 / 10061 — порт закрыт; иначе (нет маршрута, тайм-аут) — принтер не отвечает
          throw PrinterException(code == 111 || code == 10061 ? PrinterProblem.refused : PrinterProblem.timeout, e.message, connected);
        } finally {
          s?.destroy();
        }
      case PrinterKind.bluetooth:
        await _call<Object?>('btPrint', {'address': c.address, 'data': bytes});
      case PrinterKind.usb:
        await _call<Object?>('usbPrint', {'device': c.address, 'data': bytes}, const Duration(minutes: 1));
      case PrinterKind.none:
        throw const PrinterException(PrinterProblem.notConfigured);
    }
  }

  /// [afterSale] — печать сразу после продажи: при включённом ящике и наличной оплате в том же задании импульс ящика
  /// (один раз на чек). 2026-10-22 (мобилка, админ): П6 — двойное нажатие «Печать», пока чек печатается, не даёт второй чек.
  Future<void> printReceipt(Receipt r, {PrinterConfig? config, bool afterSale = false}) {
    final key = _receiptKey(r);
    final running = _inflight[key];
    if (running != null) return running;
    final f = () async {
      final c = config ?? await load();
      final drawer = afterSale && c.drawer && receiptNeedsDrawer(r) && !_drawerDone.contains(key);
      await send(c, receiptToEscPos(r, paperMm: c.paperMm, codepage: c.codepage, codeTable: c.codeTable, qr: c.qr, openDrawer: drawer, drawerPin: c.drawerPin));
      if (drawer) _drawerDone.add(key);
    }();
    _inflight[key] = f;
    return f.whenComplete(() => _inflight.remove(key));
  }

  /// 2026-10-22 (мобилка, админ): П6 — ящик после продажи без печати чека (автопечать выключена).
  Future<void> drawerAfterSale(Receipt r, PrinterConfig c) async {
    final key = _receiptKey(r);
    if (!c.ready || !c.drawer || !receiptNeedsDrawer(r) || _drawerDone.contains(key)) return;
    _drawerDone.add(key);
    await send(c, drawerPulse(pin: c.drawerPin));
  }

  /// Кнопка «Открыть ящик» в настройках.
  Future<void> openDrawerTest(PrinterConfig c) => send(c, drawerPulse(pin: c.drawerPin));

  Future<void> printTest(PrinterConfig c, {String title = 'NurMarket'}) =>
      send(c, testPage(paperMm: c.paperMm, codepage: c.codepage, codeTable: c.codeTable, title: title));
}

final printerProvider = Provider((ref) => PrinterService(ref.watch(databaseProvider)));
