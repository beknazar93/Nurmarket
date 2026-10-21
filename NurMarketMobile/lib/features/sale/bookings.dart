import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_screens.dart';

/// 13.10, макет С7 «Телефон — услуги»: запись клиентов к мастерам на сегодня и завтра, «Рассчитать» — услуги записи сразу
/// в чек (оплата — обычный чек NurCRM), напоминание в WhatsApp. У сервера нет API записи — записи на этом телефоне
/// (настройка bookings), мастера и длительность услуг — те же, что в «Новая позиция → Услуга».
class Booking {
  final String id;
  DateTime start;
  int minutes;
  String master;
  String client;
  String phone;
  List<String> serviceIds;
  String status; // booked | arrived | paid | cancelled
  Booking({
    required this.id,
    required this.start,
    required this.minutes,
    required this.master,
    required this.client,
    this.phone = '',
    this.serviceIds = const [],
    this.status = 'booked',
  });

  Map<String, dynamic> toJson() => {
    'id': id,
    'start': start.toIso8601String(),
    'minutes': minutes,
    'master': master,
    'client': client,
    'phone': phone,
    'services': serviceIds,
    'status': status,
  };

  static Booking fromJson(Map<String, dynamic> j) => Booking(
    id: j['id'] as String,
    start: DateTime.parse(j['start'] as String),
    minutes: (j['minutes'] as num?)?.toInt() ?? 30,
    master: j['master'] as String? ?? '',
    client: j['client'] as String? ?? '',
    phone: j['phone'] as String? ?? '',
    serviceIds: ((j['services'] as List?) ?? const []).map((e) => e.toString()).toList(),
    status: j['status'] as String? ?? 'booked',
  );
}

class BookingsStore {
  final AppDatabase db;
  BookingsStore(this.db);

  Future<List<Booking>> load() async {
    final raw = await db.getSetting('bookings');
    if (raw == null) return [];
    return (jsonDecode(raw) as List).map((e) => Booking.fromJson((e as Map).cast<String, dynamic>())).toList();
  }

  /// Храним 60 дней — старше никому не нужно на телефоне.
  Future<void> save(List<Booking> list) async {
    final from = DateTime.now().subtract(const Duration(days: 60));
    await db.setSetting('bookings', jsonEncode(list.where((b) => b.start.isAfter(from)).map((b) => b.toJson()).toList()));
  }

  Future<List<String>> masters() async {
    final raw = await db.getSetting('services.masters');
    return raw == null ? [] : (jsonDecode(raw) as List).map((e) => e.toString()).toList();
  }

  Future<int> serviceMinutes(String productId) async {
    final raw = await db.getSetting('service.$productId');
    return raw == null ? 30 : ((jsonDecode(raw) as Map)['minutes'] as num?)?.toInt() ?? 30;
  }
}

final bookingsStoreProvider = Provider((ref) => BookingsStore(ref.watch(databaseProvider)));

class BookingsScreen extends ConsumerStatefulWidget {
  const BookingsScreen({super.key});
  @override
  ConsumerState<BookingsScreen> createState() => _BookingsScreenState();
}

class _BookingsScreenState extends ConsumerState<BookingsScreen> {
  List<Booking> _all = [];
  List<String> _masters = [];
  List<Product> _services = [];
  int _dayOffset = 0;
  String? _master;

  DateTime get _day => DateUtils.dateOnly(DateTime.now()).add(Duration(days: _dayOffset));

  @override
  void initState() {
    super.initState();
    _reload();
  }

  Future<void> _reload() async {
    final store = ref.read(bookingsStoreProvider);
    final all = await store.load();
    final masters = await store.masters();
    final services = (await ref.read(databaseProvider).searchProducts('', limit: 1000)).where((p) => p.isService).toList();
    if (!mounted) return;
    setState(() {
      _all = all;
      _masters = {...masters, ...all.map((b) => b.master).where((m) => m.isNotEmpty)}.toList();
      _services = services;
    });
  }

  Future<void> _save() async {
    await ref.read(bookingsStoreProvider).save(_all);
    if (mounted) setState(() {});
  }

  String _serviceNames(Booking b) => b.serviceIds.map((id) => _services.where((p) => p.id == id).firstOrNull?.name ?? '').where((n) => n.isNotEmpty).join(', ');

  double _sum(Booking b) => b.serviceIds.fold(0.0, (s, id) => s + (_services.where((p) => p.id == id).firstOrNull?.price ?? 0));

  String _hm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  Future<void> _book({DateTime? at, String? master}) async {
    final l = context.l;
    if (_masters.isEmpty) {
      toast(context, l.bookNoMasters, error: true);
      return;
    }
    final client = TextEditingController();
    final phone = TextEditingController();
    var who = master ?? _master ?? _masters.first;
    var time = TimeOfDay.fromDateTime(at ?? DateTime.now().add(const Duration(minutes: 30)));
    final chosen = <String>{};
    var minutes = 30;
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(16, 12, 16, MediaQuery.viewInsetsOf(c).bottom + 16),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.bookNew, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                FieldLabel(l.client),
                TextField(controller: client, autofocus: true),
                FieldLabel(l.phone),
                TextField(controller: phone, keyboardType: TextInputType.phone),
                FieldLabel(l.mastersLabel),
                Wrap(
                  spacing: 6,
                  children: [for (final m in _masters) ChoiceChip(label: Text(m), selected: who == m, onSelected: (_) => set(() => who = m))],
                ),
                FieldLabel(l.kindService),
                Wrap(
                  spacing: 6,
                  children: [
                    for (final s in _services)
                      FilterChip(
                        label: Text('${s.name} · ${money(s.price)}'),
                        selected: chosen.contains(s.id),
                        onSelected: (on) async {
                          on ? chosen.add(s.id) : chosen.remove(s.id);
                          var total = 0;
                          for (final id in chosen) {
                            total += await ref.read(bookingsStoreProvider).serviceMinutes(id);
                          }
                          set(() => minutes = total == 0 ? 30 : total);
                        },
                      ),
                  ],
                ),
                FieldLabel('${l.bookTime} · $minutes мин'),
                OutlinedButton.icon(
                  onPressed: () async {
                    final t = await showTimePicker(context: c, initialTime: time);
                    if (t != null) set(() => time = t);
                  },
                  icon: const Icon(Icons.schedule_rounded),
                  label: Text(time.format(c)),
                ),
                const SizedBox(height: 14),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: () => Navigator.pop(c, true),
                  child: Text(l.bookNew),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    if (ok != true || client.text.trim().isEmpty || !mounted) return;
    final start = DateTime(_day.year, _day.month, _day.day, time.hour, time.minute);
    final end = start.add(Duration(minutes: minutes));
    final clash = _all.any(
      (b) => b.master == who && b.status != 'cancelled' && b.start.isBefore(end) && start.isBefore(b.start.add(Duration(minutes: b.minutes))),
    );
    if (clash) {
      toast(context, l.bookClash, error: true);
      return;
    }
    _all.add(
      Booking(
        id: DateTime.now().microsecondsSinceEpoch.toString(),
        start: start,
        minutes: minutes,
        master: who,
        client: client.text.trim(),
        phone: phone.text.trim(),
        serviceIds: chosen.toList(),
      ),
    );
    await _save();
  }

  Future<void> _actions(Booking b) async {
    final l = context.l;
    final action = await showModalBottomSheet<String>(
      context: context,
      builder: (c) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              title: Text('${_hm(b.start)} · ${b.client}', style: const TextStyle(fontWeight: FontWeight.w800)),
              subtitle: Text('${b.master} · ${_serviceNames(b)}'),
            ),
            if (b.status != 'paid' && b.serviceIds.isNotEmpty)
              ListTile(
                leading: const Icon(Icons.point_of_sale_rounded),
                title: Text('${l.bookCheckout} · ${money(_sum(b))}'),
                onTap: () => Navigator.pop(c, 'pay'),
              ),
            if (b.status == 'booked')
              ListTile(leading: const Icon(Icons.how_to_reg_rounded), title: Text(l.bookMarkArrived), onTap: () => Navigator.pop(c, 'arrived')),
            if (b.phone.isNotEmpty && b.status == 'booked')
              ListTile(leading: const Icon(Icons.chat_rounded), title: Text(l.bookRemind), onTap: () => Navigator.pop(c, 'remind')),
            if (b.status != 'paid')
              ListTile(
                leading: const Icon(Icons.event_busy_rounded, color: T.danger),
                title: Text(l.bookCancel),
                onTap: () => Navigator.pop(c, 'cancel'),
              ),
          ],
        ),
      ),
    );
    if (!mounted) return;
    switch (action) {
      case 'pay':
        final db = ref.read(databaseProvider);
        final cart = ref.read(cartProvider.notifier);
        for (final id in b.serviceIds) {
          final p = await db.productById(id);
          if (p != null) cart.add(p);
        }
        b.status = 'paid';
        await _save();
        if (mounted) context.go('/sale');
      case 'arrived':
        b.status = 'arrived';
        await _save();
      case 'remind':
        await openWhatsApp(
          b.phone,
          l.bookReminder(b.client, '${b.start.day.toString().padLeft(2, '0')}.${b.start.month.toString().padLeft(2, '0')} ${_hm(b.start)}', b.master),
        );
      case 'cancel':
        b.status = 'cancelled';
        await _save();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final day = _all.where((b) => DateUtils.isSameDay(b.start, _day) && b.status != 'cancelled' && (_master == null || b.master == _master)).toList()
      ..sort((a, b) => a.start.compareTo(b.start));
    // Свободные часы — только когда выбран один мастер (как колонка мастера на кассе).
    final free = <DateTime>[];
    if (_master != null) {
      for (var h = 9; h < 21; h++) {
        final t = DateTime(_day.year, _day.month, _day.day, h);
        if (t.isBefore(DateTime.now())) continue;
        final busy = day.any(
          (b) =>
              !t.isBefore(b.start) && t.isBefore(b.start.add(Duration(minutes: b.minutes))) ||
              (b.start.isAfter(t) && b.start.isBefore(t.add(const Duration(hours: 1)))),
        );
        if (!busy) free.add(t);
      }
    }
    final rows = <(DateTime, Booking?)>[for (final b in day) (b.start, b), for (final t in free) (t, null)]..sort((a, b) => a.$1.compareTo(b.$1));
    return Scaffold(
      appBar: ScreenTitle(l.bookingsTitle),
      floatingActionButton: FloatingActionButton.extended(onPressed: () => _book(), icon: const Icon(Icons.add_rounded), label: Text(l.bookNew)),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
          children: [
            SegmentedButton<int>(
              segments: [
                ButtonSegment(value: 0, label: Text(l.dayToday)),
                ButtonSegment(value: 1, label: Text(l.dayTomorrow)),
              ],
              selected: {_dayOffset},
              onSelectionChanged: (s) => setState(() => _dayOffset = s.first),
            ),
            const SizedBox(height: 8),
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  Padding(
                    padding: const EdgeInsets.only(right: 6),
                    child: ChoiceChip(label: Text(l.allMasters), selected: _master == null, onSelected: (_) => setState(() => _master = null)),
                  ),
                  for (final m in _masters)
                    Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: ChoiceChip(label: Text(m), selected: _master == m, onSelected: (_) => setState(() => _master = m)),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 8),
            if (_masters.isEmpty) Panel(child: Text(l.bookNoMasters)),
            if (rows.isEmpty && _masters.isNotEmpty)
              Padding(
                padding: const EdgeInsets.all(24),
                child: Text(
                  l.nothingFound,
                  textAlign: TextAlign.center,
                  style: TextStyle(color: pal.textSoft),
                ),
              ),
            for (final (t, b) in rows)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: b == null
                    ? Panel(
                        onTap: () => _book(at: t, master: _master),
                        child: Row(
                          children: [
                            Text(_hm(t), style: const TextStyle(fontWeight: FontWeight.w800)),
                            const SizedBox(width: 12),
                            Text(l.bookFree, style: TextStyle(color: pal.textSoft)),
                          ],
                        ),
                      )
                    : Panel(
                        onTap: () => _actions(b),
                        child: Row(
                          children: [
                            Text(_hm(b.start), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(b.client, style: const TextStyle(fontWeight: FontWeight.w700)),
                                  Text(
                                    [_serviceNames(b), b.master, '${b.minutes} мин'].where((x) => x.isNotEmpty).join(' · '),
                                    style: TextStyle(color: pal.textSoft, fontSize: 12.5),
                                  ),
                                ],
                              ),
                            ),
                            Text(
                              switch (b.status) {
                                'arrived' => l.bookArrived,
                                'paid' => l.bookPaid,
                                _ => l.bookBooked,
                              },
                              style: TextStyle(
                                fontWeight: FontWeight.w800,
                                fontSize: 12,
                                color: b.status == 'paid' ? T.success : (b.status == 'arrived' ? pal.action : pal.textSoft),
                              ),
                            ),
                          ],
                        ),
                      ),
              ),
            const SizedBox(height: 10),
            Text(l.bookLocalNote, style: TextStyle(color: pal.textSoft, fontSize: 12)),
          ],
        ),
      ),
    );
  }
}
