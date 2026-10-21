import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/print/escpos.dart';
import '../../core/print/printer_service.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../receipt/receipt.dart';

/// 2026-10-20, владелец: «исправь подключение к принтерам». Понятный текст ошибки печати на языке кассира.
String printerProblemText(AppLocalizations l, PrinterException e) => switch (e.problem) {
  PrinterProblem.notConfigured => l.prnErrNotConfigured,
  PrinterProblem.btUnsupported => l.prnErrUnsupported,
  PrinterProblem.btOff => l.prnErrBtOff,
  PrinterProblem.noPermission => l.prnErrPermission,
  PrinterProblem.notFound => l.prnErrNotFound,
  PrinterProblem.bleOnly => l.prnErrBleOnly,
  PrinterProblem.timeout => l.prnErrTimeout,
  PrinterProblem.refused => l.prnErrRefused,
  PrinterProblem.usbDenied => l.prnErrUsbDenied,
  PrinterProblem.usbNotFound => l.prnErrUsbNotFound,
  PrinterProblem.io => l.prnErrIo(e.detail),
};

/// Печать чека с любого экрана (после оплаты, «Чек», «Чеки»). [silentIfNone] — автопечать: принтера нет — молчим.
Future<void> printReceiptUi(BuildContext context, Receipt r, {bool silentIfNone = false}) async {
  final l = context.l;
  final service = ProviderScope.containerOf(context, listen: false).read(printerProvider);
  final c = await service.load();
  if (!c.ready) {
    if (!silentIfNone && context.mounted) toast(context, l.prnErrNotConfigured, error: true);
    return;
  }
  try {
    await service.printReceipt(r, config: c);
    if (context.mounted) toast(context, l.prnSent);
  } on PrinterException catch (e) {
    if (context.mounted) toast(context, printerProblemText(l, e), error: true);
  }
}

/// Профиль → «Принтер чеков»: тип подключения, поиск, ширина ленты, кодировка, «Проверить печать».
class PrinterSettingsScreen extends ConsumerStatefulWidget {
  const PrinterSettingsScreen({super.key});
  @override
  ConsumerState<PrinterSettingsScreen> createState() => _PrinterSettingsScreenState();
}

class _PrinterSettingsScreenState extends ConsumerState<PrinterSettingsScreen> {
  PrinterConfig _c = const PrinterConfig();
  bool _loaded = false;
  bool _busy = false;
  bool _searching = false;
  List<PrinterDevice>? _found;
  String? _note;
  final _ip = TextEditingController();
  final _port = TextEditingController(text: '9100');
  final _table = TextEditingController();

  PrinterService get _service => ref.read(printerProvider);

  @override
  void initState() {
    super.initState();
    _service.load().then((c) {
      if (!mounted) return;
      setState(() {
        _c = c;
        _loaded = true;
        if (c.kind == PrinterKind.network) _ip.text = c.address;
        _port.text = '${c.port}';
        _table.text = '${c.codeTable}';
      });
    });
  }

  @override
  void dispose() {
    _ip.dispose();
    _port.dispose();
    _table.dispose();
    super.dispose();
  }

  PrinterConfig _current() {
    var c = _c;
    if (c.kind == PrinterKind.network) c = c.copyWith(address: _ip.text.trim(), port: int.tryParse(_port.text.trim()) ?? 9100);
    return c.copyWith(codeTable: int.tryParse(_table.text.trim()) ?? defaultCodeTable(c.codepage));
  }

  Future<void> _save() async {
    _c = _current();
    await _service.save(_c);
  }

  void _setKind(PrinterKind k) {
    setState(() {
      _c = _c.copyWith(kind: k, address: k == PrinterKind.network ? _ip.text.trim() : (k == _c.kind ? _c.address : ''), name: k == _c.kind ? _c.name : '');
      _found = null;
      _note = null;
    });
    _save();
  }

  /// Bluetooth: разрешение → включён ли → список сопряжённых и найденных рядом.
  Future<bool> _btReady({bool scan = false}) async {
    final l = context.l;
    var st = await _service.btState();
    if (!st.supported) throw const PrinterException(PrinterProblem.btUnsupported);
    if (!st.permission || scan) {
      final ok = await _service.requestBtPermission(scan: scan);
      if (!ok && !scan) throw const PrinterException(PrinterProblem.noPermission);
    }
    st = await _service.btState();
    if (!st.enabled) {
      await _service.enableBt();
      if (mounted) setState(() => _note = l.prnErrBtOff);
      return false;
    }
    return true;
  }

  Future<void> _search() async {
    final l = context.l;
    setState(() {
      _searching = true;
      _note = null;
      _found = null;
    });
    try {
      final list = switch (_c.kind) {
        PrinterKind.bluetooth => await _btReady(scan: true) ? await _service.btDevices(discover: true) : <PrinterDevice>[],
        PrinterKind.usb => await _service.usbDevices(),
        PrinterKind.network => await _service.scanNetwork(port: int.tryParse(_port.text.trim()) ?? 9100),
        PrinterKind.none => <PrinterDevice>[],
      };
      if (!mounted) return;
      setState(() {
        _found = list;
        if (list.isEmpty && _note == null) _note = l.prnNothingFound;
      });
    } on PrinterException catch (e) {
      if (mounted) setState(() => _note = printerProblemText(l, e));
    } finally {
      if (mounted) setState(() => _searching = false);
    }
  }

  void _pick(PrinterDevice d) {
    setState(() {
      _c = _c.copyWith(address: d.address, name: d.name);
      if (_c.kind == PrinterKind.network) _ip.text = d.address;
    });
    _save();
  }

  Future<void> _test() async {
    final l = context.l;
    await _save();
    if (!mounted) return;
    if (!_c.ready) {
      toast(context, l.prnErrNotConfigured, error: true);
      return;
    }
    setState(() => _busy = true);
    try {
      if (_c.kind == PrinterKind.bluetooth && !await _btReady()) return;
      await _service.printTest(_c, title: ref.read(sessionProvider)?.companyName ?? 'NurMarket');
      if (mounted) {
        setState(() => _note = l.prnTestHint);
        toast(context, l.prnTestSent);
      }
    } on PrinterException catch (e) {
      if (mounted) setState(() => _note = printerProblemText(l, e));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    if (!_loaded) return Scaffold(appBar: ScreenTitle(l.receiptPrinter), body: const Center(child: CircularProgressIndicator()));
    return Scaffold(
      appBar: ScreenTitle(l.receiptPrinter, subtitle: _c.ready ? (_c.name.isEmpty ? _c.address : _c.name) : l.prnNotSet),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 120),
        children: [
          FieldLabel(l.prnConnection),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final (k, label) in [
                (PrinterKind.none, l.prnOff),
                (PrinterKind.bluetooth, 'Bluetooth'),
                (PrinterKind.network, l.prnNetwork),
                (PrinterKind.usb, l.prnUsb),
              ])
                ChoiceChip(label: Text(label), selected: _c.kind == k, onSelected: (_) => _setKind(k)),
            ],
          ),
          if (_c.kind == PrinterKind.network) ...[
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  flex: 3,
                  child: TextField(
                    controller: _ip,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: InputDecoration(labelText: l.prnIp, hintText: '192.168.1.100', isDense: true),
                    onChanged: (_) => _c = _current(),
                    onEditingComplete: _save,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _port,
                    keyboardType: TextInputType.number,
                    decoration: InputDecoration(labelText: l.prnPort, isDense: true),
                    onEditingComplete: _save,
                  ),
                ),
              ],
            ),
          ],
          if (_c.kind != PrinterKind.none) ...[
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: _searching ? null : _search,
                    icon: _searching ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.search_rounded),
                    label: Text(_searching ? l.prnSearching : l.prnSearch),
                  ),
                ),
                if (_c.kind == PrinterKind.bluetooth) ...[
                  const SizedBox(width: 8),
                  IconButton.outlined(
                    tooltip: l.prnBtSettings,
                    onPressed: () => _service.openBtSettings().catchError((_) {}),
                    icon: const Icon(Icons.settings_bluetooth_rounded),
                  ),
                ],
              ],
            ),
            if (_c.kind == PrinterKind.bluetooth)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(l.prnPairHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
              ),
            if (_c.kind == PrinterKind.usb)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(l.prnUsbHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
              ),
            if (_found != null)
              for (final d in _found!)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(
                    _c.kind == PrinterKind.usb ? Icons.usb_rounded : (_c.kind == PrinterKind.network ? Icons.lan_outlined : Icons.bluetooth_rounded),
                    color: d.isPrinter ? null : pal.textSoft,
                  ),
                  title: Text(d.name, style: TextStyle(fontWeight: d.isPrinter ? FontWeight.w700 : FontWeight.w500)),
                  subtitle: Text([d.address, if (_c.kind == PrinterKind.bluetooth) d.paired ? l.prnPaired : l.prnNotPaired].join(' · ')),
                  trailing: d.address == _c.address ? const Icon(Icons.check_circle_rounded, color: Colors.green) : null,
                  onTap: () => _pick(d),
                ),
          ],
          if (_note != null)
            Padding(
              padding: const EdgeInsets.only(top: 10),
              child: Panel(child: Text(_note!, style: const TextStyle(fontWeight: FontWeight.w600))),
            ),
          FieldLabel(l.prnPaper),
          Wrap(
            spacing: 6,
            children: [
              for (final mm in [58, 80])
                ChoiceChip(
                  label: Text(l.prnPaperMm(mm, lineWidth(mm))),
                  selected: _c.paperMm == mm,
                  onSelected: (_) {
                    setState(() => _c = _c.copyWith(paperMm: mm));
                    _save();
                  },
                ),
            ],
          ),
          FieldLabel(l.prnCodepage),
          Row(
            children: [
              for (final cp in PrinterCodepage.values)
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: ChoiceChip(
                    label: Text(cp == PrinterCodepage.cp866 ? 'CP866' : 'CP1251'),
                    selected: _c.codepage == cp,
                    onSelected: (_) {
                      setState(() {
                        _c = _c.copyWith(codepage: cp, codeTable: defaultCodeTable(cp));
                        _table.text = '${defaultCodeTable(cp)}';
                      });
                      _save();
                    },
                  ),
                ),
              const SizedBox(width: 6),
              SizedBox(
                width: 110,
                child: TextField(
                  controller: _table,
                  keyboardType: TextInputType.number,
                  decoration: InputDecoration(labelText: l.prnCodeTable, isDense: true),
                  onEditingComplete: _save,
                ),
              ),
            ],
          ),
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(l.prnCodepageHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
          ),
          const SizedBox(height: 8),
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(l.prnAuto, style: const TextStyle(fontWeight: FontWeight.w600)),
            value: _c.autoPrint,
            onChanged: (v) {
              setState(() => _c = _c.copyWith(autoPrint: v));
              _save();
            },
          ),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
          child: FilledButton.icon(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
            onPressed: _busy || _c.kind == PrinterKind.none ? null : _test,
            icon: _busy ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2.5)) : const Icon(Icons.print_rounded),
            label: Text(l.prnTest),
          ),
        ),
      ),
    );
  }
}
