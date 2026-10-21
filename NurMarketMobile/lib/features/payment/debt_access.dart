import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../admin/admin_more.dart';

/// 18.10, владелец («создать доступы для долга»; на десктопе — с 16.10, DebtAccess). Правило продажи в долг для сотрудника
/// или для всех остальных (user = «*»): можно, нельзя или с лимитом — чеков и сом долга в день. Правила общие для всех касс
/// и телефонов компании: api/main/app-settings/nurmarket-mobile/, ключ DebtAccess (рядом с QR оплаты); копия — на телефоне.
/// Счёт дня ведёт сам телефон (как касса — свой). Владелец и администратор (право настроек) ограничений не имеют.
class DebtRule {
  static const everyone = '*';
  static const allow = 'allow';
  static const deny = 'deny';
  static const limit = 'limit';

  String user;
  String mode;
  int maxReceipts;
  double maxSum;

  DebtRule({this.user = everyone, this.mode = allow, this.maxReceipts = 0, this.maxSum = 0});

  Map<String, dynamic> toJson() => {'user': user, 'mode': mode, 'max_receipts': maxReceipts, 'max_sum': maxSum};

  static DebtRule fromJson(Map m) {
    double n(Object? v) => v is num ? v.toDouble() : double.tryParse('${v ?? ''}') ?? 0;
    final mode = '${m['mode'] ?? ''}';
    final user = '${m['user'] ?? ''}';
    return DebtRule(
      user: user.isEmpty ? everyone : user,
      mode: mode == deny || mode == limit ? mode : allow,
      maxReceipts: n(m['max_receipts']).toInt().clamp(0, 1 << 30),
      maxSum: n(m['max_sum']) < 0 ? 0 : n(m['max_sum']),
    );
  }
}

class DebtAccessRepository {
  final ApiClient api;
  final AppDatabase db;
  DebtAccessRepository(this.api, this.db);

  static const _app = 'nurmarket-mobile';
  static const _key = 'DebtAccess';
  static const _local = 'debt.rules';
  static const _usage = 'debt.usage';

  static List<DebtRule> decode(String? raw) {
    if (raw == null || raw.isEmpty) return [];
    try {
      final list = jsonDecode(raw);
      return list is List
          ? [
              for (final m in list)
                if (m is Map) DebtRule.fromJson(m),
            ]
          : [];
    } catch (_) {
      return [];
    }
  }

  /// Сначала сервер, без связи — копия телефона.
  Future<List<DebtRule>> load() async {
    try {
      final data = await api.get('api/main/app-settings/$_app/', timeout: const Duration(seconds: 10));
      final list = data is Map && data['settings'] is Map ? (data['settings'] as Map)[_key] : null;
      final raw = jsonEncode(list is List ? list : const []);
      await db.setSetting(_local, raw);
      return decode(raw);
    } on ApiException {
      return decode(await db.getSetting(_local));
    }
  }

  Future<void> save(List<DebtRule> rules) async {
    final raw = jsonEncode([for (final r in rules) r.toJson()]);
    await api.patch('api/main/app-settings/$_app/', body: {_key: jsonDecode(raw)});
    await db.setSetting(_local, raw);
  }

  static DebtRule ruleFor(List<DebtRule> rules, String userId) =>
      rules.where((r) => userId.isNotEmpty && r.user.toLowerCase() == userId.toLowerCase()).firstOrNull ??
      rules.where((r) => r.user == DebtRule.everyone).firstOrNull ??
      DebtRule();

  static bool exempt(Session? s) => s != null && (s.isOwner || s.can(Session.viewSettings));

  static String _today() {
    final d = DateTime.now();
    return '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
  }

  /// Сколько чеков и сом в долг этот сотрудник уже дал сегодня с этого телефона.
  Future<(int, double)> usedToday(String userId) async {
    try {
      final m = jsonDecode(await db.getSetting(_usage) ?? '{}');
      if (m is Map && m['date'] == _today() && m['users'] is Map) {
        final u = (m['users'] as Map)[userId];
        if (u is Map) return ((u['count'] as num?)?.toInt() ?? 0, (u['sum'] as num?)?.toDouble() ?? 0.0);
      }
    } catch (_) {
      // испорченная запись — день с нуля
    }
    return (0, 0.0);
  }

  /// После успешной продажи «В долг».
  Future<void> recordSale(String userId, double debtAmount) async {
    if (debtAmount <= 0) return;
    Map<String, dynamic> m;
    try {
      final parsed = jsonDecode(await db.getSetting(_usage) ?? '{}');
      m = parsed is Map<String, dynamic> && parsed['date'] == _today() ? parsed : {'date': _today(), 'users': <String, dynamic>{}};
    } catch (_) {
      m = {'date': _today(), 'users': <String, dynamic>{}};
    }
    final users = (m['users'] as Map).cast<String, dynamic>();
    final u = users[userId] is Map ? (users[userId] as Map).cast<String, dynamic>() : <String, dynamic>{'count': 0, 'sum': 0.0};
    u['count'] = ((u['count'] as num?) ?? 0) + 1;
    u['sum'] = (((u['sum'] as num?) ?? 0) + debtAmount);
    users[userId] = u;
    m['users'] = users;
    await db.setSetting(_usage, jsonEncode(m));
  }

  /// Можно ли продать в долг на эту сумму. null — можно, иначе — объяснение для кассира.
  Future<String?> check(AppLocalizations l, Session? s, List<DebtRule> rules, double debtAmount) async {
    if (exempt(s)) return null;
    final userId = s?.userId ?? '';
    final rule = ruleFor(rules, userId);
    if (rule.mode == DebtRule.deny) return l.debtDenied;
    if (rule.mode != DebtRule.limit) return null;
    final (count, sum) = await usedToday(userId);
    if (rule.maxReceipts > 0 && count + 1 > rule.maxReceipts) return l.debtLimitReceipts(count, rule.maxReceipts);
    if (rule.maxSum > 0 && sum + debtAmount > rule.maxSum + 0.005) {
      final left = rule.maxSum - sum < 0 ? 0.0 : rule.maxSum - sum;
      return l.debtLimitSum(money(left), money(rule.maxSum), money(debtAmount));
    }
    return null;
  }

  /// Подсказка кассиру при выборе «В долг» (пусто — без ограничений).
  Future<String> hint(AppLocalizations l, Session? s, List<DebtRule> rules) async {
    if (exempt(s)) return '';
    final userId = s?.userId ?? '';
    final rule = ruleFor(rules, userId);
    if (rule.mode == DebtRule.deny) return l.debtDenied;
    if (rule.mode != DebtRule.limit) return '';
    final (count, sum) = await usedToday(userId);
    final parts = [if (rule.maxReceipts > 0) l.debtReceiptsOf(count, rule.maxReceipts), if (rule.maxSum > 0) l.debtSumOf(money(sum), money(rule.maxSum))];
    return parts.isEmpty ? '' : l.debtYourLimit(parts.join(', '));
  }
}

final debtAccessRepoProvider = Provider((ref) => DebtAccessRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

/// Экран владельца: «Управление → Деньги → Доступ к долгу».
class DebtAccessScreen extends ConsumerStatefulWidget {
  const DebtAccessScreen({super.key});
  @override
  ConsumerState<DebtAccessScreen> createState() => _DebtAccessScreenState();
}

class _Line {
  final String user;
  final String name;
  String mode; // inherit | allow | deny | limit
  final TextEditingController receipts;
  final TextEditingController sum;
  _Line(this.user, this.name, this.mode, DebtRule? r)
    : receipts = TextEditingController(text: (r?.maxReceipts ?? 0) > 0 ? '${r!.maxReceipts}' : ''),
      sum = TextEditingController(text: (r?.maxSum ?? 0) > 0 ? r!.maxSum.toStringAsFixed(0) : '');
}

class _DebtAccessScreenState extends ConsumerState<DebtAccessScreen> {
  static const _inherit = 'inherit';
  List<_Line>? _lines;
  bool _busy = false;
  String _note = '';

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final l = context.l;
    final rules = await ref.read(debtAccessRepoProvider).load();
    List<Employee> staff = [];
    try {
      staff = await ref.read(staffRepoProvider).list();
    } on ApiException {
      _note = l.debtStaffNotLoaded;
    }
    if (!mounted) return;
    final all = rules.where((r) => r.user == DebtRule.everyone).firstOrNull;
    final lines = <_Line>[_Line(DebtRule.everyone, l.debtAllStaff, all?.mode ?? DebtRule.allow, all)];
    for (final e in staff..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()))) {
      final own = rules.where((r) => r.user.toLowerCase() == e.id.toLowerCase()).firstOrNull;
      lines.add(_Line(e.id, e.name.isEmpty ? e.role : e.name, own?.mode ?? _inherit, own));
    }
    // правила уволенных / не загруженных сотрудников не теряем
    for (final r in rules.where((r) => r.user != DebtRule.everyone && staff.every((e) => e.id.toLowerCase() != r.user.toLowerCase()))) {
      lines.add(_Line(r.user, '${l.staff} ${r.user}', r.mode, r));
    }
    setState(() => _lines = lines);
  }

  Future<void> _save() async {
    final l = context.l;
    final rules = <DebtRule>[];
    for (final x in _lines!) {
      if (x.mode == _inherit) continue;
      final r = DebtRule(user: x.user, mode: x.mode);
      if (x.mode == DebtRule.limit) {
        r.maxReceipts = int.tryParse(x.receipts.text.trim()) ?? 0;
        r.maxSum = parseNum(x.sum.text) ?? 0;
      }
      if (r.user == DebtRule.everyone && r.mode == DebtRule.allow) continue; // «все — можно» = без правила
      rules.add(r);
    }
    setState(() => _busy = true);
    try {
      await ref.read(debtAccessRepoProvider).save(rules);
      Audit.log('settings', '${l.debtAccessTitle}: ${rules.length}');
      if (mounted) toast(context, l.debtAccessSaved);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(l.debtAccessTitle),
      bottomNavigationBar: _lines == null
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 6, 16, 10),
                child: FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: _busy ? null : _save,
                  child: Text(l.save),
                ),
              ),
            ),
      body: _lines == null
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
              children: [
                Text(l.debtAccessHint, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                if (_note.isNotEmpty)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(_note, style: const TextStyle(color: T.danger, fontSize: 12.5)),
                  ),
                const SizedBox(height: 10),
                for (final x in _lines!)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Panel(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(x.name, style: TextStyle(fontWeight: x.user == DebtRule.everyone ? FontWeight.w800 : FontWeight.w700, fontSize: 15)),
                          const SizedBox(height: 8),
                          Wrap(
                            spacing: 6,
                            runSpacing: 6,
                            children: [
                              for (final (id, text) in [
                                if (x.user != DebtRule.everyone) (_inherit, l.debtSameAsAll),
                                (DebtRule.allow, l.debtAllow),
                                (DebtRule.deny, l.debtDeny),
                                (DebtRule.limit, l.debtLimit),
                              ])
                                ChoiceChip(
                                  label: Text(text),
                                  selected: x.mode == id,
                                  selectedColor: id == DebtRule.deny ? T.danger.withValues(alpha: 0.18) : null,
                                  onSelected: (_) => setState(() => x.mode = id),
                                ),
                            ],
                          ),
                          if (x.mode == DebtRule.limit) ...[
                            const SizedBox(height: 10),
                            Row(
                              children: [
                                Expanded(
                                  child: TextField(
                                    controller: x.receipts,
                                    keyboardType: TextInputType.number,
                                    decoration: InputDecoration(labelText: l.debtReceiptsPerDay, hintText: l.debtNoLimit),
                                  ),
                                ),
                                const SizedBox(width: 10),
                                Expanded(
                                  child: TextField(
                                    controller: x.sum,
                                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                                    decoration: InputDecoration(labelText: l.debtSumPerDay, hintText: l.debtNoLimit),
                                  ),
                                ),
                              ],
                            ),
                          ],
                        ],
                      ),
                    ),
                  ),
              ],
            ),
    );
  }
}
