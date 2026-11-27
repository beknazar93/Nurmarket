import 'package:flutter/widgets.dart';

/// 2026-11-26 (владелец: «0 не убирается автоматически при создании товара в количестве!»): один общий механизм для всех числовых
/// полей (клавиатура «число») вместо правки каждого экрана.
/// 1) Поле получило фокус (касание, переход, программно) — содержимое выделяется целиком: первая цифра заменяет «0» («5», а не «05»),
///    «0,5» набирается как обычно. Касание ставит курсор уже после фокуса, поэтому выделение — после кадра.
/// 2) Поле, в котором при входе стояло «0» / «0,00», при выходе пустым не остаётся — снова «0» (а не пустота и не ошибка).
class NumericFocus {
  NumericFocus._();

  static bool _installed = false;
  static EditableTextState? _zeroField; // поле, в которое вошли с нулём
  static EditableTextState? _current; // поле, на котором фокус сейчас (повторные уведомления того же фокуса игнорируются)

  /// Значение «ноль по умолчанию»: «0», «0,00», «0.0», «00» (не «0,5», не пусто).
  static final RegExp _zero = RegExp(r'^\s*0+([.,]0*)?\s*$');

  static bool isZeroText(String? t) => t != null && t.isNotEmpty && _zero.hasMatch(t);

  /// Один раз за запуск приложения: следит за переходом фокуса между полями.
  static void install() {
    if (_installed) return;
    _installed = true;
    FocusManager.instance.addListener(_onFocusChanged);
  }

  @visibleForTesting
  static void uninstall() {
    if (!_installed) return;
    _installed = false;
    _zeroField = null;
    _current = null;
    FocusManager.instance.removeListener(_onFocusChanged);
  }

  static bool _isNumeric(EditableTextState s) {
    final w = s.widget;
    return w.keyboardType.index == TextInputType.number.index && !w.obscureText && w.maxLines == 1 && !w.readOnly;
  }

  static void _onFocusChanged() {
    // Ушли из поля, где стоял ноль: пустое поле → «0».
    final left = _zeroField;
    if (left != null && left.mounted && !left.widget.focusNode.hasFocus) {
      final c = left.widget.controller;
      if (c.text.trim().isEmpty) {
        c.value = const TextEditingValue(text: '0', selection: TextSelection.collapsed(offset: 1));
      }
      _zeroField = null;
    }
    final ctx = FocusManager.instance.primaryFocus?.context;
    final state = ctx?.findAncestorStateOfType<EditableTextState>();
    if (state == _current) return;
    _current = state;
    if (state == null || !_isNumeric(state)) return;
    final c = state.widget.controller;
    if (isZeroText(c.text)) _zeroField = state;
    if (c.text.isEmpty) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!state.mounted || !state.widget.focusNode.hasFocus) return;
      c.selection = TextSelection(baseOffset: 0, extentOffset: c.text.length);
    });
    WidgetsBinding.instance.scheduleFrame();
  }
}
