import 'dart:io';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';

/// 14.10, владелец: «журнал аудита». Все действия на этом телефоне по дням: фильтр по виду, поиск (сотрудник, товар,
/// номер чека), выгрузка в Excel (CSV).
class AuditLogScreen extends StatefulWidget {
  const AuditLogScreen({super.key});
  @override
  State<AuditLogScreen> createState() => _AuditLogScreenState();
}

class _AuditLogScreenState extends State<AuditLogScreen> {
  List<AuditEntry>? _all;
  String? _kind;
  final _q = TextEditingController();

  @override
  void initState() {
    super.initState();
    Audit.read().then((v) => mounted ? setState(() => _all = v) : null);
  }

  String _kindName(BuildContext c, String k) {
    final l = c.l;
    return switch (k) {
      'sale' => l.auditSale,
      'return' => l.auditReturn,
      'discount' => l.discount,
      'delete' => l.deletedFromCheck,
      'price' => l.auditPrice,
      'product' => l.auditProduct,
      'receive' => l.receiveWithPrices,
      'cash' => l.cashInOut,
      'shift' => l.shifts,
      'ai' => l.auditAi, // 2026-10-26 (В5): действия ИИ в звонке
      _ => l.auditSettings,
    };
  }

  IconData _icon(String k) => switch (k) {
    'sale' => Icons.receipt_long_rounded,
    'return' => Icons.undo_rounded,
    'discount' => Icons.percent_rounded,
    'delete' => Icons.delete_outline_rounded,
    'price' => Icons.sell_outlined,
    'product' => Icons.inventory_2_outlined,
    'receive' => Icons.move_to_inbox_outlined,
    'cash' => Icons.swap_vert_rounded,
    'shift' => Icons.schedule_rounded,
    'ai' => Icons.auto_awesome_outlined,
    _ => Icons.settings_outlined,
  };

  List<AuditEntry> get _shown {
    final q = _q.text.trim().toLowerCase();
    return (_all ?? const <AuditEntry>[])
        .where((e) => _kind == null || e.kind == _kind)
        .where((e) => q.isEmpty || e.text.toLowerCase().contains(q) || e.user.toLowerCase().contains(q))
        .toList();
  }

  Future<void> _export() async {
    final rows = _shown;
    if (rows.isEmpty) return;
    String q(Object? v) => '"${'${v ?? ''}'.replaceAll('"', '""')}"';
    final b = StringBuffer('﻿${[context.l.dateLabel, context.l.staff, context.l.auditKind, context.l.auditWhat, context.l.esfSum].map(q).join(';')}\n');
    for (final e in rows) {
      b.writeln(
        [_fmt(e.at), e.user, _kindName(context, e.kind), e.text, e.amount == null ? '' : e.amount!.toStringAsFixed(2).replaceAll('.', ',')].map(q).join(';'),
      );
    }
    final dir = await getTemporaryDirectory();
    final file = File('${dir.path}/audit.csv');
    await file.writeAsString(b.toString());
    await SharePlus.instance.share(
      ShareParams(
        files: [XFile(file.path, mimeType: 'text/csv', name: 'audit.csv')],
      ),
    );
  }

  static String _two(int v) => v.toString().padLeft(2, '0');
  static String _fmt(DateTime d) => '${_two(d.day)}.${_two(d.month)}.${d.year} ${_two(d.hour)}:${_two(d.minute)}';

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final rows = _shown;
    String? lastDay;
    return Scaffold(
      appBar: ScreenTitle(
        l.auditTitle,
        subtitle: l.auditSub,
        actions: [IconButton(tooltip: l.esfExportCsv, onPressed: _export, icon: const Icon(Icons.table_chart_outlined))],
      ),
      body: _all == null
          ? const Center(child: CircularProgressIndicator())
          : Column(
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 4, 16, 6),
                  child: TextField(
                    controller: _q,
                    decoration: InputDecoration(hintText: l.auditSearch, prefixIcon: const Icon(Icons.search_rounded), isDense: true),
                    onChanged: (_) => setState(() {}),
                  ),
                ),
                SizedBox(
                  height: 44,
                  child: ListView(
                    scrollDirection: Axis.horizontal,
                    padding: const EdgeInsets.symmetric(horizontal: 16),
                    children: [
                      Padding(
                        padding: const EdgeInsets.only(right: 6),
                        child: ChoiceChip(label: Text(l.filterAll), selected: _kind == null, onSelected: (_) => setState(() => _kind = null)),
                      ),
                      for (final k in Audit.kinds)
                        Padding(
                          padding: const EdgeInsets.only(right: 6),
                          child: ChoiceChip(label: Text(_kindName(context, k)), selected: _kind == k, onSelected: (_) => setState(() => _kind = k)),
                        ),
                    ],
                  ),
                ),
                Expanded(
                  child: rows.isEmpty
                      ? Center(
                          child: Text(l.nothingFound, style: TextStyle(color: pal.textSoft)),
                        )
                      : ListView.builder(
                          padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                          itemCount: rows.length,
                          itemBuilder: (c, i) {
                            final e = rows[i];
                            final day = '${_two(e.at.day)}.${_two(e.at.month)}.${e.at.year}';
                            final header = day != lastDay;
                            lastDay = day;
                            return Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                if (header)
                                  Padding(
                                    padding: const EdgeInsets.fromLTRB(4, 12, 4, 6),
                                    child: Text(
                                      day,
                                      style: TextStyle(fontWeight: FontWeight.w800, color: pal.textSoft, fontSize: 12),
                                    ),
                                  ),
                                Padding(
                                  padding: const EdgeInsets.only(bottom: 6),
                                  child: Panel(
                                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                                    child: Row(
                                      children: [
                                        Container(
                                          width: 36,
                                          height: 36,
                                          decoration: BoxDecoration(color: pal.accentSoft, borderRadius: BorderRadius.circular(10)),
                                          child: Icon(_icon(e.kind), size: 18, color: pal.action),
                                        ),
                                        const SizedBox(width: 10),
                                        Expanded(
                                          child: Column(
                                            crossAxisAlignment: CrossAxisAlignment.start,
                                            children: [
                                              Text(e.text, style: const TextStyle(fontWeight: FontWeight.w700)),
                                              Text(
                                                '${_two(e.at.hour)}:${_two(e.at.minute)} · ${e.user.isEmpty ? '—' : e.user} · ${_kindName(context, e.kind)}',
                                                style: TextStyle(fontSize: 12, color: pal.textSoft),
                                              ),
                                            ],
                                          ),
                                        ),
                                        if (e.amount != null) ...[
                                          const SizedBox(width: 10),
                                          Text(
                                            '${money(e.amount!)} ${l.som}',
                                            style: TextStyle(
                                              fontWeight: FontWeight.w800,
                                              color: e.kind == 'return' || e.kind == 'delete' ? T.danger : pal.price,
                                            ),
                                          ),
                                        ],
                                      ],
                                    ),
                                  ),
                                ),
                              ],
                            );
                          },
                        ),
                ),
              ],
            ),
    );
  }
}
