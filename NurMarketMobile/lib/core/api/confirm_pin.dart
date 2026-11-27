import 'package:flutter/material.dart';

import '../../l10n/app_localizations.dart';
import '../util/ui.dart';
import 'api_client.dart';

/// 2026-11-26 (проверка в эмуляторе, как у кассы: ReturnSaleDialog.Pin.cs, SalesApiService.ReturnConfirm.cs): на компаниях с включённым «Подтверждать PIN»
/// сервер отвечает на возврат чека 403 `pin_required`. PIN владельца/администратора проверяется запросом users/auth/pin/verify/ {pin} (без области действия) →
/// токен подтверждения (живёт 5 минут) → тот же запрос повторяется с заголовком X-Confirm-Token. PIN и токен держатся только в памяти.
class ConfirmPin {
  ConfirmPin._();

  static const verifyPath = 'api/users/auth/pin/verify/';
  static const header = 'X-Confirm-Token';

  /// Поля ответа, в которых может лежать токен подтверждения (как ConfirmTokenFieldCandidates кассы). access/refresh — токены входа, не подтверждение.
  static const _tokenFields = ['confirm_token', 'confirmation_token', 'x_confirm_token', 'token'];

  /// PIN → токен для [header]. Неверный PIN, блокировка и прочие отказы — [ApiException] (текст — [errorText]).
  static Future<String> verify(ApiClient api, String pin) async {
    final r = await api.post(verifyPath, body: {'pin': pin.trim()});
    if (r is Map && r['access'] == null && r['refresh'] == null) {
      for (final k in _tokenFields) {
        final v = r[k];
        if (v is String && v.trim().isNotEmpty) return v.trim();
      }
    }
    throw ApiException(ApiText.badResponse);
  }

  /// Понятный текст отказа проверки PIN: неверный (с числом оставшихся попыток), заблокирован, не задан; остальное — текст сервера.
  static String errorText(AppLocalizations l, ApiException e) {
    final code = e.code ?? '';
    if (e.status == 429 || code == 'pin_locked') return l.pinLocked;
    if (code == 'pin_not_set') return l.pinNotSet;
    final p = e.payload;
    if (e.status == 400 && (code.contains('pin') || (p is Map && p.containsKey('pin')))) {
      final left = p is Map ? p['attempts_left'] : null;
      final n = left is num ? left.toInt() : int.tryParse('$left');
      return n == null ? l.pinInvalid : l.pinInvalidLeft(n);
    }
    return e.message;
  }

  /// Окно PIN → токен. null — отмена. Неверный PIN: текст ошибки под полем и ввод заново (окно не закрывается).
  static Future<String?> ask(BuildContext context, ApiClient api) =>
      showDialog<String>(context: context, barrierDismissible: false, builder: (_) => _PinDialog(api));
}

class _PinDialog extends StatefulWidget {
  final ApiClient api;
  const _PinDialog(this.api);
  @override
  State<_PinDialog> createState() => _PinDialogState();
}

class _PinDialogState extends State<_PinDialog> {
  final _pin = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _pin.dispose();
    super.dispose();
  }

  Future<void> _confirm() async {
    if (_busy || _pin.text.trim().isEmpty) return;
    final l = context.l;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final token = await ConfirmPin.verify(widget.api, _pin.text);
      if (mounted) Navigator.pop(context, token);
    } on ApiException catch (e) {
      if (!mounted) return;
      _pin.clear();
      setState(() {
        _busy = false;
        _error = ConfirmPin.errorText(l, e);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return AlertDialog(
      title: Text(l.pinTitle),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l.pinReturnInfo),
          const SizedBox(height: 12),
          TextField(
            controller: _pin,
            autofocus: true,
            obscureText: true,
            keyboardType: TextInputType.number,
            textInputAction: TextInputAction.done,
            onSubmitted: (_) => _confirm(),
            decoration: InputDecoration(labelText: l.pinFieldLabel, errorText: _error, errorMaxLines: 3),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: _busy ? null : () => Navigator.pop(context), child: Text(l.cancel)),
        FilledButton(onPressed: _busy ? null : _confirm, child: Text(l.pinConfirm)),
      ],
    );
  }
}
