import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_more.dart';
import '../../core/util/audit.dart';

/// 14.10, владелец: «добавить такую же функцию с QR-кодом, как на десктопе: генерация QR; владелец сам добавляет QR и
/// указывает, какой QR какому кассиру, или на выбор 2–5 QR разных банков».
///
/// Как на кассе (BankQrSetting + DynamicPaymentQrService): владелец загружает статический QR своего банка, при оплате
/// покупателю показывается QR с уже вписанной суммой чека (ELQR, см. [Elqr]). Отличие телефона — список QR общий для
/// всех телефонов магазина: хранится в общих настройках компании NurCRM (api/main/app-settings/nurmarket-mobile/,
/// ключ PaymentQrs), а на телефоне — копия для работы без связи.
class PayQr {
  final String id;
  String bank;

  /// Текст статического QR банка (ссылка https://app.mbank.kg/qr/#… или голый ELQR).
  String payload;

  /// Кому показывать: id сотрудников; пусто — всем кассирам.
  List<String> cashiers;

  /// Вписывать сумму чека в QR (ELQR). Выключено — показывается статический QR как есть.
  bool dynamicAmount;

  PayQr({required this.id, required this.bank, required this.payload, List<String>? cashiers, this.dynamicAmount = true}) : cashiers = cashiers ?? [];

  Map<String, dynamic> toJson() => {'id': id, 'bank': bank, 'payload': payload, 'cashiers': cashiers, 'dynamic': dynamicAmount};

  factory PayQr.fromJson(Map<String, dynamic> m) => PayQr(
    id: '${m['id'] ?? DateTime.now().microsecondsSinceEpoch}',
    bank: '${m['bank'] ?? ''}',
    payload: '${m['payload'] ?? ''}',
    cashiers: [for (final c in (m['cashiers'] as List?) ?? const []) '$c'],
    dynamicAmount: m['dynamic'] != false,
  );

  bool visibleTo(String userId) => cashiers.isEmpty || cashiers.contains(userId);

  /// Что показать покупателю: с суммой (если включено и QR — ELQR), иначе статический.
  String forAmount(double amount) => (dynamicAmount ? Elqr.buildWithAmount(payload, amount) : null) ?? payload;
}

/// Сборка ELQR с суммой — перенос ElqrQrBuilder кассы один в один (TLV, сумма в тыйынах в теге 54, контрольная сумма
/// тега 63 = последние 4 символа SHA-256 строки без объекта 63, строчными — как у MBank).
class Elqr {
  static List<(String, String)> parse(String payload) {
    final out = <(String, String)>[];
    var i = 0;
    while (i + 4 <= payload.length) {
      final tag = payload.substring(i, i + 2);
      final len = int.tryParse(payload.substring(i + 2, i + 4));
      if (len == null || i + 4 + len > payload.length) break;
      out.add((tag, payload.substring(i + 4, i + 4 + len)));
      i += 4 + len;
    }
    return out;
  }

  static String _serialize(Iterable<(String, String)> fields) => fields.map((f) => '${f.$1}${f.$2.length.toString().padLeft(2, '0')}${f.$2}').join();

  static String checksum(String body) {
    final hex = sha256.convert(utf8.encode(body)).toString();
    return hex.substring(hex.length - 4);
  }

  static String? buildWithAmount(String text, double amountSom) {
    final t = text.trim();
    if (t.isEmpty) return null;
    final hash = t.indexOf('#');
    final prefix = hash >= 0 ? t.substring(0, hash + 1) : '';
    String decoded;
    try {
      decoded = Uri.decodeComponent(hash >= 0 ? t.substring(hash + 1) : t);
    } catch (_) {
      return null;
    }
    final fields = parse(decoded);
    if (fields.length < 3 || !fields.any((f) => f.$1 == '00') || !fields.any((f) => f.$1 == '63')) return null;
    final tiyin = (amountSom * 100).round();
    if (tiyin <= 0) return null;
    final amount = '$tiyin';
    final rebuilt = <(String, String)>[];
    var inserted = false;
    for (final f in fields) {
      if (f.$1 == '63' || f.$1 == '54') continue;
      final n = int.tryParse(f.$1);
      if (!inserted && n != null && n > 54) {
        rebuilt.add(('54', amount));
        inserted = true;
      }
      rebuilt.add(f);
    }
    if (!inserted) rebuilt.add(('54', amount));
    final body = _serialize(rebuilt);
    final payload = '${body}6304${checksum(body)}';
    return prefix + _encode(payload);
  }

  static String _encode(String s) {
    final b = StringBuffer();
    for (final ch in s.runes) {
      final c = String.fromCharCode(ch);
      if (RegExp(r'[0-9A-Za-z.\-_~]').hasMatch(c)) {
        b.write(c);
      } else {
        for (final byte in utf8.encode(c)) {
          b.write('%${byte.toRadixString(16).padLeft(2, '0')}');
        }
      }
    }
    return b.toString();
  }
}

class PayQrRepository {
  final ApiClient api;
  final AppDatabase db;
  PayQrRepository(this.api, this.db);

  static const _app = 'nurmarket-mobile';
  static const _key = 'PaymentQrs';
  static const _local = 'pay.qrs';

  Future<List<PayQr>> _decode(String? raw) async {
    if (raw == null || raw.isEmpty) return [];
    try {
      return [for (final m in jsonDecode(raw) as List) PayQr.fromJson((m as Map).cast<String, dynamic>())];
    } catch (_) {
      return [];
    }
  }

  /// Сначала сервер (общий список магазина), без связи — копия на телефоне.
  Future<List<PayQr>> load() async {
    try {
      final data = await api.get('api/main/app-settings/$_app/', timeout: const Duration(seconds: 10));
      final list = data is Map && data['settings'] is Map ? (data['settings'] as Map)[_key] : null;
      if (list is List) {
        final raw = jsonEncode(list);
        await db.setSetting(_local, raw);
        return await _decode(raw);
      }
    } on ApiException {
      // без связи или настроек ещё нет — копия телефона
    }
    return _decode(await db.getSetting(_local));
  }

  Future<void> save(List<PayQr> list) async {
    final raw = jsonEncode([for (final q in list) q.toJson()]);
    await db.setSetting(_local, raw);
    await api.patch('api/main/app-settings/$_app/', body: {_key: jsonDecode(raw)});
  }
}

final payQrRepoProvider = Provider((ref) => PayQrRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

/// QR этого кассира (не больше 5 — на выбор покупателю).
final myPayQrsProvider = FutureProvider<List<PayQr>>((ref) async {
  final userId = ref.watch(sessionProvider)?.userId ?? '';
  final all = await ref.read(payQrRepoProvider).load();
  return all.where((q) => q.payload.isNotEmpty && q.visibleTo(userId)).take(5).toList();
});

/// Блок оплаты переводом: банки на выбор и QR с суммой чека — покупатель сканирует и платит в своём банке.
class PayQrPanel extends ConsumerStatefulWidget {
  final double amount;
  const PayQrPanel({super.key, required this.amount});
  @override
  ConsumerState<PayQrPanel> createState() => _PayQrPanelState();
}

class _PayQrPanelState extends ConsumerState<PayQrPanel> {
  int _selected = 0;

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final qrs = ref.watch(myPayQrsProvider).value ?? const <PayQr>[];
    if (qrs.isEmpty) {
      return Padding(
        padding: const EdgeInsets.only(top: 10),
        child: Text(l.payQrNone, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
      );
    }
    final q = qrs[_selected.clamp(0, qrs.length - 1)];
    final data = q.forAmount(widget.amount);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        FieldLabel(l.payQrTitle),
        if (qrs.length > 1)
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (var i = 0; i < qrs.length; i++)
                ChoiceChip(label: Text(qrs[i].bank), selected: i == _selected, onSelected: (_) => setState(() => _selected = i)),
            ],
          ),
        const SizedBox(height: 10),
        Center(
          child: GestureDetector(
            onTap: () => showPayQrFull(context, q.bank, data, widget.amount),
            child: Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: Colors.white,
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: pal.border),
              ),
              child: QrImageView(data: data, size: 220, backgroundColor: Colors.white),
            ),
          ),
        ),
        const SizedBox(height: 6),
        Text(
          '${q.bank} · ${money(widget.amount)} ${l.som}',
          textAlign: TextAlign.center,
          style: TextStyle(fontWeight: FontWeight.w800, color: pal.price),
        ),
        Text(
          l.payQrShowCustomer,
          textAlign: TextAlign.center,
          style: TextStyle(color: pal.textSoft, fontSize: 12),
        ),
      ],
    );
  }
}

/// QR крупно на весь экран — повернуть телефон к покупателю.
void showPayQrFull(BuildContext context, String bank, String data, double amount) => showDialog<void>(
  context: context,
  builder: (c) => Dialog(
    backgroundColor: Colors.white,
    child: InkWell(
      onTap: () => Navigator.pop(c),
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              bank,
              style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 18),
            ),
            const SizedBox(height: 10),
            QrImageView(data: data, size: 300, backgroundColor: Colors.white),
            const SizedBox(height: 10),
            Text(
              '${money(amount)} ${c.l.som}',
              style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 26),
            ),
          ],
        ),
      ),
    ),
  ),
);

/// Владелец: QR банков, кому какой показывать, сумма в QR.
class PayQrSettingsScreen extends ConsumerStatefulWidget {
  const PayQrSettingsScreen({super.key});
  @override
  ConsumerState<PayQrSettingsScreen> createState() => _PayQrSettingsScreenState();
}

class _PayQrSettingsScreenState extends ConsumerState<PayQrSettingsScreen> {
  List<PayQr>? _list;
  List<Employee> _staff = [];

  static const banks = ['MBank', 'Optima Bank', 'Bakai Bank', 'О!Деньги', 'Элсом', 'Demir Bank', 'KICB', 'РСК Банк', 'Айыл Банк', 'Balance.kg', 'Keremet Bank'];

  @override
  void initState() {
    super.initState();
    ref.read(payQrRepoProvider).load().then((v) => mounted ? setState(() => _list = v) : null);
    ref.read(staffRepoProvider).list().then((v) => mounted ? setState(() => _staff = v) : null, onError: (_) {});
  }

  Future<void> _save() async {
    try {
      final title = context.l.payQrTitle;
      await ref.read(payQrRepoProvider).save(_list!);
      Audit.log('settings', '$title: ${_list!.map((q) => q.bank).join(', ')}');
      ref.invalidate(myPayQrsProvider);
      if (mounted) toast(context, context.l.payQrSaved);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  Future<String?> _scanCamera() => Navigator.of(context).push<String>(MaterialPageRoute(builder: (_) => const _QrScanPage()));

  Future<String?> _fromGallery() async {
    final shot = await ImagePicker().pickImage(source: ImageSource.gallery);
    if (shot == null) return null;
    final controller = MobileScannerController(formats: const [BarcodeFormat.qrCode]);
    try {
      final r = await controller.analyzeImage(shot.path);
      return r?.barcodes.firstOrNull?.rawValue;
    } finally {
      await controller.dispose();
    }
  }

  Future<void> _edit([PayQr? q]) async {
    final l = context.l;
    final item = q ?? PayQr(id: DateTime.now().microsecondsSinceEpoch.toString(), bank: banks.first, payload: '');
    final bank = TextEditingController(text: item.bank);
    final payload = TextEditingController(text: item.payload);
    var dyn = item.dynamicAmount;
    final cashiers = {...item.cashiers};
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) {
          Future<void> take(Future<String?> Function() src) async {
            final v = await src();
            if (v == null || v.trim().isEmpty) {
              if (c.mounted) toast(c, l.payQrNotRecognized, error: true);
              return;
            }
            set(() => payload.text = v.trim());
          }

          final preview = payload.text.trim().isEmpty ? null : (dyn ? Elqr.buildWithAmount(payload.text, 100) : null) ?? payload.text.trim();
          return Padding(
            padding: EdgeInsets.fromLTRB(16, 14, 16, MediaQuery.viewInsetsOf(c).bottom + 12),
            child: SingleChildScrollView(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(q == null ? l.payQrAdd : item.bank, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                  FieldLabel(l.payQrBank),
                  TextField(controller: bank),
                  const SizedBox(height: 6),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [for (final b in banks) ChoiceChip(label: Text(b), selected: bank.text == b, onSelected: (_) => set(() => bank.text = b))],
                  ),
                  FieldLabel(l.payQrCode),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      ActionChip(avatar: const Icon(Icons.qr_code_scanner_rounded, size: 18), label: Text(l.payQrScan), onPressed: () => take(_scanCamera)),
                      ActionChip(avatar: const Icon(Icons.photo_library_outlined, size: 18), label: Text(l.payQrGallery), onPressed: () => take(_fromGallery)),
                      ActionChip(
                        avatar: const Icon(Icons.content_paste_rounded, size: 18),
                        label: Text(l.payQrPaste),
                        onPressed: () => take(() async => (await Clipboard.getData(Clipboard.kTextPlain))?.text),
                      ),
                    ],
                  ),
                  const SizedBox(height: 6),
                  TextField(controller: payload, minLines: 1, maxLines: 3, style: const TextStyle(fontSize: 12), onChanged: (_) => set(() {})),
                  if (preview != null) ...[
                    const SizedBox(height: 10),
                    Center(
                      child: Container(
                        padding: const EdgeInsets.all(8),
                        color: Colors.white,
                        child: QrImageView(data: preview, size: 150, backgroundColor: Colors.white),
                      ),
                    ),
                    if (dyn && Elqr.buildWithAmount(payload.text, 100) == null)
                      Text(
                        l.payQrStaticOnly,
                        textAlign: TextAlign.center,
                        style: const TextStyle(color: T.warn, fontSize: 12),
                      ),
                  ],
                  SwitchListTile(
                    contentPadding: EdgeInsets.zero,
                    value: dyn,
                    onChanged: (v) => set(() => dyn = v),
                    title: Text(l.payQrDynamic),
                    subtitle: Text(l.payQrDynamicSub, style: const TextStyle(fontSize: 12)),
                  ),
                  FieldLabel(l.payQrCashiers),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      ChoiceChip(label: Text(l.payQrAllCashiers), selected: cashiers.isEmpty, onSelected: (_) => set(cashiers.clear)),
                      for (final e in _staff)
                        FilterChip(
                          label: Text(e.name.isEmpty ? e.role : e.name),
                          selected: cashiers.contains(e.id),
                          onSelected: (on) => set(() => on ? cashiers.add(e.id) : cashiers.remove(e.id)),
                        ),
                    ],
                  ),
                  const SizedBox(height: 14),
                  FilledButton(
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                    onPressed: payload.text.trim().isEmpty || bank.text.trim().isEmpty ? null : () => Navigator.pop(c, true),
                    child: Text(l.save),
                  ),
                  if (q != null)
                    TextButton.icon(
                      style: TextButton.styleFrom(foregroundColor: T.danger),
                      onPressed: () {
                        setState(() => _list!.remove(q));
                        Navigator.pop(c, false);
                        _save();
                      },
                      icon: const Icon(Icons.delete_outline_rounded),
                      label: Text(l.payQrDelete),
                    ),
                ],
              ),
            ),
          );
        },
      ),
    );
    if (ok != true || !mounted) return;
    setState(() {
      item
        ..bank = bank.text.trim()
        ..payload = payload.text.trim()
        ..dynamicAmount = dyn
        ..cashiers = cashiers.toList();
      if (q == null) _list!.add(item);
    });
    await _save();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    String who(PayQr q) =>
        q.cashiers.isEmpty ? l.payQrAllCashiers : q.cashiers.map((id) => _staff.where((e) => e.id == id).map((e) => e.name).firstOrNull ?? '…').join(', ');
    return Scaffold(
      appBar: ScreenTitle(l.payQrTitle),
      floatingActionButton: _list == null
          ? null
          : FloatingActionButton.extended(onPressed: () => _edit(), icon: const Icon(Icons.add_rounded), label: Text(l.payQrAdd)),
      body: _list == null
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
              children: [
                Text(l.payQrHint, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                const SizedBox(height: 10),
                if (_list!.isEmpty) Text(l.payQrNoneOwner, style: TextStyle(color: pal.textSoft)),
                for (final q in _list!)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Panel(
                      onTap: () => _edit(q),
                      child: Row(
                        children: [
                          Container(
                            color: Colors.white,
                            padding: const EdgeInsets.all(4),
                            child: QrImageView(data: q.payload, size: 56, backgroundColor: Colors.white),
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(q.bank, style: const TextStyle(fontWeight: FontWeight.w800)),
                                Text(
                                  who(q),
                                  maxLines: 2,
                                  overflow: TextOverflow.ellipsis,
                                  style: TextStyle(color: pal.textSoft, fontSize: 12.5),
                                ),
                                if (q.dynamicAmount) Text(l.payQrDynamic, style: const TextStyle(color: T.success, fontSize: 12)),
                              ],
                            ),
                          ),
                          const Icon(Icons.chevron_right_rounded),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
    );
  }
}

class _QrScanPage extends StatefulWidget {
  const _QrScanPage();
  @override
  State<_QrScanPage> createState() => _QrScanPageState();
}

class _QrScanPageState extends State<_QrScanPage> {
  bool _done = false;
  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: Colors.black,
    appBar: ScreenTitle(context.l.payQrScan),
    body: MobileScanner(
      controller: MobileScannerController(formats: const [BarcodeFormat.qrCode]),
      onDetect: (capture) {
        final v = capture.barcodes.firstOrNull?.rawValue;
        if (_done || v == null || v.isEmpty) return;
        _done = true;
        Navigator.of(context).pop(v);
      },
    ),
  );
}
