import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/sync/account_switch.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/providers.dart';
import '../../core/theme/brand_mark.dart';
import '../../core/ui/account_banners.dart';
import '../../core/theme/tokens.dart';
import '../../core/tester/tester_mode.dart';
import '../../core/util/ui.dart';
import '../profile/logout_guard.dart';
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
  final _passwordFocus = FocusNode();
  bool _busy = false;
  bool _hide = true;
  String? _error;

  @override
  void dispose() {
    _login.dispose();
    _password.dispose();
    _passwordFocus.dispose();
    super.dispose();
  }

  /// 2026-10-26 (живой аудит, П9, Н-1): язык выбирается и до входа — как в профиле (тот же ключ ui.lang).
  static const _languages = {'ru': 'Русский', 'ky': 'Кыргызча', 'en': 'English', 'tr': 'Türkçe', 'uz': "O'zbekcha"};

  Future<void> _chooseLanguage() async {
    final current = ref.read(prefsProvider).locale.languageCode;
    final code = await showModalBottomSheet<String>(
      context: context,
      builder: (c) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final e in _languages.entries)
              ListTile(
                title: Text(e.value),
                trailing: e.key == current ? const Icon(Icons.check_rounded) : null,
                onTap: () => Navigator.pop(c, e.key),
              ),
          ],
        ),
      ),
    );
    if (code != null) await ref.read(prefsProvider.notifier).setLocale(code);
  }

  Future<void> _submit() async {
    if (_busy) return;
    if (_login.text.trim().isEmpty || _password.text.isEmpty) {
      setState(() => _error = context.l.loginEmpty);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      // 2026-10-29 («ничего не должно остаться»): вводят чужой логин, а на телефоне остались неотправленные продажи прошлого
      // аккаунта (выход не делали: истёк вход) — их нужно выгрузить в файл или удалить; иначе вход не продолжается
      final typed = OutboxMeta.ownerOf(Session(email: _login.text, access: '', refresh: ''));
      final last = ((await ref.read(databaseProvider).getSetting(AccountSwitch.lastOwnerKey)) ?? '').trim().toLowerCase();
      if (last.isNotEmpty && last != typed && mounted) {
        if (!await confirmQueueResolved(context, ref, foreignOwner: typed)) {
          if (mounted) setState(() => _busy = false);
          return;
        }
      }
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
    // 2026-10-26 (приёмка 1.18.6, В-1н): на 411×731 dp при открытой клавиатуре «Войти» закрывала поля — шапка с логотипом
    // (≈280 dp), список и закреплённая кнопка не помещались в остаток над клавиатурой. Теперь весь экран — одна прокрутка:
    // кнопка идёт СРАЗУ ПОД полями (не поверх), а при клавиатуре шапка сжимается до строки с языком и названием.
    final kb = MediaQuery.viewInsetsOf(context).bottom > 0;
    // 2026-10-26 (живой аудит, П9): шапка всегда тёмная — значки строки состояния (время, сеть) светлые и в светлой теме
    return LightStatusBar(child: Scaffold(
      resizeToAvoidBottomInset: true,
      body: LayoutBuilder(
        builder: (context, box) => SingleChildScrollView(
          keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
          child: ConstrainedBox(
            constraints: BoxConstraints(minHeight: box.maxHeight),
            child: IntrinsicHeight(
              child: Column(
                children: [
                  Container(
                    width: double.infinity,
                    decoration: const BoxDecoration(
                      color: T.navy,
                      borderRadius: BorderRadius.vertical(bottom: Radius.circular(28)),
                    ),
                    padding: EdgeInsets.fromLTRB(24, MediaQuery.paddingOf(context).top + (kb ? 4 : 32), 24, kb ? 12 : 28),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        // 2026-10-26 (живой аудит, П9): переключатель языка в шапке (справа), касание 48 dp, подпись для TalkBack
                        Row(
                          children: [
                            if (kb)
                              const Expanded(
                                child: Text('NurCRM', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800, color: Colors.white)),
                              )
                            else
                              const Spacer(),
                            Semantics(
                              button: true,
                              label: '${l.language}: ${_languages[ref.watch(prefsProvider).locale.languageCode] ?? ''}',
                              excludeSemantics: true,
                              child: Tooltip(
                                message: l.language,
                                child: TextButton.icon(
                                  style: TextButton.styleFrom(foregroundColor: Colors.white, minimumSize: const Size(48, 48)),
                                  onPressed: _chooseLanguage,
                                  icon: const Icon(Icons.language_rounded, size: 20),
                                  label: Text(ref.watch(prefsProvider).locale.languageCode.toUpperCase(), style: const TextStyle(fontWeight: FontWeight.w700)),
                                ),
                              ),
                            ),
                          ],
                        ),
                        if (!kb) ...[
                          // 2026-10-22, брендбук NUR CRM: знак «солнце над горой» для чёрного фона (минимум 28 px — здесь 64).
                          // 2026-10-27: 5 быстрых нажатий на логотип — «Режим тестировщика» (только тестовая сборка или по коду)
                          const TesterTap(child: BrandLogo(width: 130, kind: BrandLogoKind.vertical, variant: BrandVariant.dark)),
                          const SizedBox(height: 14),
                          const Text(
                            'NurCRM',
                            style: TextStyle(fontSize: 26, fontWeight: FontWeight.w800, color: Colors.white),
                          ),
                          const SizedBox(height: 6),
                          Text(l.loginTagline, style: const TextStyle(fontSize: 13, color: Color(0xFFC7D2DB))),
                        ],
                      ],
                    ),
                  ),
                  Expanded(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(20, 24, 20, 20),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          FieldLabel(l.loginLabel),
                          TextField(
                            controller: _login,
                            keyboardType: TextInputType.emailAddress,
                            // 2026-10-29: без автозаполнения — на экране входа не должно быть следов прошлого аккаунта
                            autocorrect: false,
                            enableSuggestions: false,
                            // 2026-10-26 (живой аудит, П9): «Далее» на клавиатуре переводит на пароль
                            textInputAction: TextInputAction.next,
                            onSubmitted: (_) => _passwordFocus.requestFocus(),
                            decoration: InputDecoration(hintText: l.loginHint),
                          ),
                          const SizedBox(height: 12),
                          FieldLabel(l.passwordLabel),
                          TextField(
                            controller: _password,
                            focusNode: _passwordFocus,
                            obscureText: _hide,
                            autocorrect: false,
                            enableSuggestions: false,
                            textInputAction: TextInputAction.done,
                            onSubmitted: (_) => _submit(),
                            decoration: InputDecoration(
                              // 2026-10-26 (живой аудит, П9, Н-1): подсказка «Пароль», а не «••••••••» (похоже на введённый пароль)
                              hintText: l.passwordLabel,
                              suffixIcon: IconButton(
                                tooltip: _hide ? l.a11yShowPassword : l.a11yHidePassword,
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
                          // 2026-10-29 (полная изоляция аккаунтов): «Загружаем данные аккаунта…» после входа и неотправленные продажи других аккаунтов
                          if (_busy) const AccountLoadBanner(margin: EdgeInsets.only(top: 12)),
                          const Spacer(),
                          const SizedBox(height: 16),
                          SafeArea(
                            top: false,
                            child: FilledButton(
                              onPressed: _busy ? null : _submit,
                              child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5, color: T.navy)) : Text(l.signIn),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    ));
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
