import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../payment/sales_repository.dart';
import '../sale/catalog_repository.dart';
import '../sale/sale_screen.dart';

/// 13.10, макет С8 «Телефон — прокат»: тот же прокат NurCRM, что у кассы (api/rentals/, RentalsApi): создание списывает
/// вещь со склада и приходует денежный залог в кассу смены, возврат возвращает вещь и залог (штраф удерживается).
/// Стоимости проката в документе нет — её берут обычной продажей строкой «Прокат №N».
class Rental {
  final String id;
  final int number;
  final String client;
  final String status;
  final bool overdue;
  final DateTime? from;
  final DateTime? to;
  final double deposit;
  final String document;
  final List<String> items;
  const Rental(this.id, this.number, this.client, this.status, this.overdue, this.from, this.to, this.deposit, this.document, this.items);
  bool get active => status != 'returned';
}

class RentalsRepository {
  final ApiClient api;
  RentalsRepository(this.api);

  Future<List<Rental>> list() async {
    final data = await api.get('api/rentals/');
    return [
      for (final m in asList(data))
        Rental(
          str(m, 'id') ?? '',
          num0(m, 'number').round(),
          str(m, 'client_name') ?? '',
          (str(m, 'status') ?? '').toLowerCase(),
          m['overdue'] == true || (str(m, 'status') ?? '') == 'overdue',
          DateTime.tryParse(str(m, 'date_from') ?? ''),
          DateTime.tryParse(str(m, 'date_to') ?? ''),
          num0(m, 'deposit_amount'),
          str(m, 'deposit_document') ?? '',
          [
            if (m['items'] is List)
              for (final i in (m['items'] as List).whereType<Map>())
                [
                  str(i.cast<String, dynamic>(), 'name'),
                  str(i.cast<String, dynamic>(), 'size'),
                  str(i.cast<String, dynamic>(), 'color'),
                ].whereType<String>().where((x) => x.isNotEmpty).join(' · '),
          ],
        ),
    ];
  }

  Future<int> create({
    required String clientId,
    required String productId,
    String? variantId,
    required DateTime from,
    required DateTime to,
    required bool money,
    required double deposit,
    String? document,
  }) async {
    String d(DateTime x) => '${x.year}-${x.month.toString().padLeft(2, '0')}-${x.day.toString().padLeft(2, '0')}';
    final r = await api.post(
      'api/rentals/',
      body: {
        'client': clientId,
        'items': [
          variantId != null ? {'variant': variantId} : {'product': productId},
        ],
        'date_from': d(from),
        'date_to': d(to),
        'tariff': 'day',
        'deposit_type': money ? 'money' : 'document',
        'deposit_amount': (money ? deposit : 0).toStringAsFixed(2),
        if (money) 'deposit_method': 'cash',
        if (!money && document != null) 'deposit_document': document,
      },
    );
    return r is Map ? num0(r.cast<String, dynamic>(), 'number').round() : 0;
  }

  Future<void> giveBack(String id, bool damaged, double penalty) =>
      api.post('api/rentals/$id/return/', body: {'condition': damaged ? 'damaged' : 'ok', 'penalty': penalty.toStringAsFixed(2)});
}

final rentalsRepoProvider = Provider((ref) => RentalsRepository(ref.watch(apiProvider)));

class RentalsScreen extends ConsumerStatefulWidget {
  const RentalsScreen({super.key});
  @override
  ConsumerState<RentalsScreen> createState() => _RentalsScreenState();
}

class _RentalsScreenState extends ConsumerState<RentalsScreen> {
  List<Rental>? _rows;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(rentalsRepoProvider).list();
      if (mounted) setState(() => _rows = rows.where((r) => r.active).toList());
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Future<void> _giveBack(Rental r) async {
    final l = context.l;
    var damaged = false;
    final penalty = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => AlertDialog(
          title: Text('${l.rentalReturn} · №${r.number}'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              SegmentedButton<bool>(
                segments: [
                  ButtonSegment(value: false, label: Text(l.rentalCondOk)),
                  ButtonSegment(value: true, label: Text(l.rentalCondDamaged)),
                ],
                selected: {damaged},
                onSelectionChanged: (v) => set(() => damaged = v.first),
              ),
              const SizedBox(height: 10),
              TextField(
                controller: penalty,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: InputDecoration(labelText: l.rentalPenalty),
              ),
            ],
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.rentalReturn)),
          ],
        ),
      ),
    );
    if (ok != true) return;
    try {
      await ref.read(rentalsRepoProvider).giveBack(r.id, damaged, parseNum(penalty.text) ?? 0);
      ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      toast(context, l.rentalReturned);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    String d(DateTime? x) => x == null ? '' : '${x.day.toString().padLeft(2, '0')}.${x.month.toString().padLeft(2, '0')}';
    return Scaffold(
      appBar: ScreenTitle(
        l.rentals,
        actions: [
          IconButton.filled(
            onPressed: () async {
              await context.push('/rentals/new');
              await _load();
            },
            icon: const Icon(Icons.add_rounded),
            tooltip: l.rentalNew,
          ),
        ],
      ),
      body: _rows == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error!))
          : RefreshIndicator(
              onRefresh: _load,
              child: _rows!.isEmpty
                  ? ListView(
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(32),
                          child: Center(child: Text(l.empty)),
                        ),
                      ],
                    )
                  : ListView.separated(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                      itemCount: _rows!.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 8),
                      itemBuilder: (c, i) {
                        final r = _rows![i];
                        return Panel(
                          color: r.overdue ? context.pal.dangerSoft : null,
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Row(
                                children: [
                                  Expanded(
                                    child: Text('№${r.number} · ${r.client}', style: const TextStyle(fontWeight: FontWeight.w800)),
                                  ),
                                  Text(
                                    r.overdue ? l.rentalOverdue : l.rentalActive,
                                    style: TextStyle(fontWeight: FontWeight.w700, color: r.overdue ? T.danger : T.success),
                                  ),
                                ],
                              ),
                              Text(r.items.join(', '), style: const TextStyle(fontSize: 13)),
                              Text(
                                '${d(r.from)} — ${d(r.to)} · ${r.deposit > 0 ? '${money(r.deposit)} ${l.som}' : r.document}',
                                style: TextStyle(fontSize: 12, color: c.pal.textSoft),
                              ),
                              const SizedBox(height: 8),
                              OutlinedButton(onPressed: () => _giveBack(r), child: Text(l.rentalReturn)),
                            ],
                          ),
                        );
                      },
                    ),
            ),
    );
  }
}

/// Новый прокат: клиент, вещь (с размером), до даты, цена за сутки, залог деньгами или документом → «Выдать и в чек».
class NewRentalScreen extends ConsumerStatefulWidget {
  const NewRentalScreen({super.key});
  @override
  ConsumerState<NewRentalScreen> createState() => _NewRentalScreenState();
}

class _NewRentalScreenState extends ConsumerState<NewRentalScreen> {
  Client? _client;
  Product? _product;
  ProductVariant? _variant;
  DateTime _to = DateTime.now().add(const Duration(days: 1));
  final _perDay = TextEditingController();
  final _deposit = TextEditingController();
  final _document = TextEditingController();
  bool _money = true;
  bool _busy = false;

  int get _days {
    final today = DateTime.now();
    final n = DateTime(_to.year, _to.month, _to.day).difference(DateTime(today.year, today.month, today.day)).inDays;
    return n < 1 ? 1 : n;
  }

  Future<void> _pickClient() async {
    final l = context.l;
    final q = TextEditingController();
    List<Client> found = [];
    final c = await showModalBottomSheet<Client>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(16, 16, 16, MediaQuery.viewInsetsOf(c).bottom + 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: q,
                autofocus: true,
                decoration: InputDecoration(hintText: l.clientSearch, prefixIcon: const Icon(Icons.search_rounded)),
                onSubmitted: (v) async {
                  try {
                    final r = await ref.read(salesRepoProvider).clients(v);
                    set(() => found = r);
                  } on ApiException catch (e) {
                    if (c.mounted) toast(c, e.message, error: true);
                  }
                },
              ),
              SizedBox(
                height: 300,
                child: ListView(
                  children: [for (final x in found) ListTile(title: Text(x.name), subtitle: Text(x.phone), onTap: () => Navigator.pop(c, x))],
                ),
              ),
            ],
          ),
        ),
      ),
    );
    if (c != null) setState(() => _client = c);
  }

  Future<void> _pickItem() async {
    final l = context.l;
    final q = TextEditingController();
    List<Product> found = await ref.read(databaseProvider).searchProducts('', limit: 50);
    if (!mounted) return;
    final p = await showModalBottomSheet<Product>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(16, 16, 16, MediaQuery.viewInsetsOf(c).bottom + 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: q,
                autofocus: true,
                decoration: InputDecoration(hintText: l.nameOrBarcode, prefixIcon: const Icon(Icons.search_rounded)),
                onChanged: (v) async {
                  final r = await ref.read(databaseProvider).searchProducts(v, limit: 50);
                  set(() => found = r);
                },
              ),
              SizedBox(
                height: 320,
                child: ListView(
                  children: [
                    for (final x in found)
                      ListTile(
                        title: Text(x.name),
                        subtitle: Text('${qtyText(x.quantity)} ${x.unit} · ${money(x.price)} ${l.som}'),
                        onTap: () => Navigator.pop(c, x),
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
    if (p == null || !mounted) return;
    ProductVariant? v;
    if (ref.read(prefsProvider).clothing) {
      try {
        final vs = (await ref.read(catalogRepoProvider).variants(p.id)).where((x) => x.active).toList();
        if (vs.isNotEmpty && mounted) {
          v = await pickVariant(context, p, vs);
          if (v == null) return;
        }
      } catch (_) {
        // без размеров
      }
    }
    setState(() {
      _product = p;
      _variant = v;
      if (_perDay.text.isEmpty) _perDay.text = money(p.price).replaceAll(' ', '').replaceAll(' ', '');
    });
  }

  Future<void> _create() async {
    final l = context.l;
    if (_client == null) {
      toast(context, l.chooseClient, error: true);
      return;
    }
    if (_product == null) {
      toast(context, l.chooseItem, error: true);
      return;
    }
    if (!_money && _document.text.trim().isEmpty) {
      toast(context, l.rentalDocumentHint, error: true);
      return;
    }
    setState(() => _busy = true);
    try {
      final number = await ref
          .read(rentalsRepoProvider)
          .create(
            clientId: _client!.id,
            productId: _product!.id,
            variantId: _variant?.id,
            from: DateTime.now(),
            to: _to,
            money: _money,
            deposit: parseNum(_deposit.text) ?? 0,
            document: _document.text.trim(),
          );
      final perDay = parseNum(_perDay.text) ?? 0;
      if (perDay > 0) ref.read(cartProvider.notifier).addCustom(l.rentalLine(number, _days.toString()), perDay * _days, 1);
      ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      toast(context, l.rentalCreated(number));
      context.go('/sale');
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final perDay = parseNum(_perDay.text) ?? 0;
    return Scaffold(
      appBar: ScreenTitle(l.rentalNew),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          FieldLabel(l.rentalClient),
          Panel(
            onTap: _pickClient,
            child: Text(_client?.display ?? l.chooseClient, style: const TextStyle(fontWeight: FontWeight.w700)),
          ),
          FieldLabel(l.rentalItem),
          Panel(
            onTap: _pickItem,
            child: Text(
              _product == null ? l.chooseItem : '${_product!.name}${_variant == null ? '' : ' (${_variant!.label})'}',
              style: const TextStyle(fontWeight: FontWeight.w700),
            ),
          ),
          FieldLabel(l.rentalUntil),
          Panel(
            onTap: () async {
              final d = await showDatePicker(
                context: context,
                initialDate: _to,
                firstDate: DateTime.now(),
                lastDate: DateTime.now().add(const Duration(days: 365)),
              );
              if (d != null) setState(() => _to = d);
            },
            child: Text(
              '${_to.day.toString().padLeft(2, '0')}.${_to.month.toString().padLeft(2, '0')}.${_to.year} · ${l.rentalDays(_days)}',
              style: const TextStyle(fontWeight: FontWeight.w700),
            ),
          ),
          FieldLabel(l.rentalPerDay),
          TextField(controller: _perDay, keyboardType: const TextInputType.numberWithOptions(decimal: true), onChanged: (_) => setState(() {})),
          if (perDay > 0)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text('${l.rentalDays(_days)} × ${money(perDay)} = ${money(perDay * _days)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800)),
            ),
          const SizedBox(height: 12),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: true, label: Text(l.rentalDeposit)),
              ButtonSegment(value: false, label: Text(l.rentalDocument)),
            ],
            selected: {_money},
            onSelectionChanged: (v) => setState(() => _money = v.first),
          ),
          const SizedBox(height: 8),
          if (_money)
            TextField(
              controller: _deposit,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: l.rentalDeposit, suffixText: l.som),
            )
          else
            TextField(
              controller: _document,
              decoration: InputDecoration(labelText: l.rentalDocument, hintText: l.rentalDocumentHint),
            ),
          const SizedBox(height: 18),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(54)),
            onPressed: _busy ? null : _create,
            child: Text(l.rentalCreate),
          ),
        ],
      ),
    );
  }
}
