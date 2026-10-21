import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../shift/shift_repository.dart';

/// Вход (макет PLogin, Т15): тот же логин, что на кассе и в вебе.
class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});
  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _login = TextEditingController();
  final _password = TextEditingController();
  bool _busy = false;
  bool _hide = true;
  String? _error;

  Future<void> _submit() async {
    if (_login.text.trim().isEmpty || _password.text.isEmpty) {
      setState(() => _error = context.l.loginEmpty);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(sessionProvider.notifier).login(_login.text, _password.text);
      if (mounted) context.go('/cashbox');
    } on ApiException catch (e) {
      setState(() => _error = e.status == 401 || e.status == 400 ? context.l.loginWrong : e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      body: Column(
        children: [
          Container(
            width: double.infinity,
            decoration: const BoxDecoration(
              color: T.navy,
              borderRadius: BorderRadius.vertical(bottom: Radius.circular(28)),
            ),
            padding: EdgeInsets.fromLTRB(24, MediaQuery.paddingOf(context).top + 32, 24, 28),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                ClipRRect(borderRadius: BorderRadius.circular(16), child: Image.asset('assets/images/logo.png', width: 64, height: 64)),
                const SizedBox(height: 14),
                const Text(
                  'NurMarket',
                  style: TextStyle(fontSize: 26, fontWeight: FontWeight.w800, color: Colors.white),
                ),
                const SizedBox(height: 6),
                Text(l.loginTagline, style: const TextStyle(fontSize: 13, color: Color(0xFFC7D2DB))),
              ],
            ),
          ),
          Expanded(
            child: ListView(
              padding: const EdgeInsets.fromLTRB(20, 24, 20, 20),
              children: [
                FieldLabel(l.loginLabel),
                TextField(
                  controller: _login,
                  keyboardType: TextInputType.emailAddress,
                  autofillHints: const [AutofillHints.username],
                  decoration: InputDecoration(hintText: l.loginHint),
                ),
                const SizedBox(height: 12),
                FieldLabel(l.passwordLabel),
                TextField(
                  controller: _password,
                  obscureText: _hide,
                  autofillHints: const [AutofillHints.password],
                  onSubmitted: (_) => _submit(),
                  decoration: InputDecoration(
                    hintText: '••••••••',
                    suffixIcon: IconButton(
                      icon: Icon(_hide ? Icons.visibility_outlined : Icons.visibility_off_outlined),
                      onPressed: () => setState(() => _hide = !_hide),
                    ),
                  ),
                ),
                if (_error != null) ...[
                  const SizedBox(height: 12),
                  Container(
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(color: context.pal.dangerSoft, borderRadius: BorderRadius.circular(12)),
                    child: Text(_error!, style: const TextStyle(color: T.danger, fontSize: 13)),
                  ),
                ],
              ],
            ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 16),
              child: FilledButton(
                onPressed: _busy ? null : _submit,
                child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5, color: T.navy)) : Text(l.signIn),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Выбор кассы после входа — смена кассира своя на каждой кассе.
class CashboxScreen extends ConsumerStatefulWidget {
  const CashboxScreen({super.key});
  @override
  ConsumerState<CashboxScreen> createState() => _CashboxScreenState();
}

class _CashboxScreenState extends ConsumerState<CashboxScreen> {
  late Future<List<Cashbox>> _boxes;

  @override
  void initState() {
    super.initState();
    _boxes = ref.read(shiftRepoProvider).cashboxes();
  }

  Future<void> _choose(Cashbox b) async {
    await ref.read(sessionProvider.notifier).chooseCashbox(b);
    if (mounted) context.go('/home');
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final s = ref.watch(sessionProvider);
    return Scaffold(
      appBar: ScreenTitle(l.chooseCashbox, subtitle: [s?.companyName, s?.userName].whereType<String>().where((e) => e.isNotEmpty).join(' · '), back: false),
      body: FutureBuilder<List<Cashbox>>(
        future: _boxes,
        builder: (c, snap) {
          if (snap.connectionState != ConnectionState.done) return const Center(child: CircularProgressIndicator());
          if (snap.hasError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(snap.error.toString(), textAlign: TextAlign.center),
                    const SizedBox(height: 12),
                    OutlinedButton(onPressed: () => setState(() => _boxes = ref.read(shiftRepoProvider).cashboxes()), child: Text(l.retry)),
                  ],
                ),
              ),
            );
          }
          final boxes = snap.data!;
          if (boxes.length == 1) WidgetsBinding.instance.addPostFrameCallback((_) => _choose(boxes.first));
          return ListView.separated(
            padding: const EdgeInsets.all(16),
            itemCount: boxes.length,
            separatorBuilder: (_, _) => const SizedBox(height: 10),
            itemBuilder: (c, i) => Panel(
              onTap: () => _choose(boxes[i]),
              child: Row(
                children: [
                  Container(
                    width: 40,
                    height: 40,
                    decoration: BoxDecoration(color: T.navy, borderRadius: BorderRadius.circular(10)),
                    alignment: Alignment.center,
                    child: const Icon(Icons.point_of_sale_rounded, color: T.accent, size: 22),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(boxes[i].name, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15)),
                  ),
                  const Icon(Icons.chevron_right_rounded),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}
