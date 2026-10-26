// 2026-10-26 (голос-управление, В3а): «ручки» денежных экранов (раздел 3.5 ТЗ). Оплата, возврат, приёмка с поставщиком,
// закрытие смены проводятся ТЕМ ЖЕ кодом, что и кнопка экрана (`_pay`, `_submit`, `_post`, `_close`): второй копии логики
// проведения нет. Экран при открытии регистрирует ручку (`AiScreenHandles.register`), при закрытии снимает.
// 2026-10-26 (голос-управление, В3б): экраны зарегистрировали ручки (pay, return, receive, close_shift, new_item). Поправка
// владельца: просьба = одобрение, поэтому ручка НЕ показывает диалогов «вы уверены?» — то, что экран спрашивал бы у человека
// по бизнес-правилам (кто клиент, сколько насчитано в кассе), возвращается как `clarify` (вопрос голосом). Ручка экрана
// сообщает итог через [OutcomeSink]: экран докладывает результат в той же точке, где показывал бы тост или «Оплачено».
// Пока ручки нет, инструмент честно отвечает «недоступно» и ничего не делает. Файл без Flutter-виджетов и без сети.
import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'screen_registry.dart' show foldText, productNameMatchesQuery;
import 'tool_registry.dart' show ToolStatus;

/// Итог ручки: что показал экран. [data] — номер документа, сумма, сдача, id продажи… (ключи согласует инструмент и экран).
class HandleOutcome {
  final ToolStatus status;

  /// Для `clarify`/ошибки: готовый текст (вопрос или сообщение самого экрана на языке приложения).
  final String? ask;

  /// Вместо [ask]: ключ текста движка (action_texts.dart, 5 языков) — язык звонка выбирает инструмент. В [askParams] число
  /// (`num`) инструмент форматирует как деньги/количество на языке звонка, строки подставляет как есть.
  final String? askKey;
  final Map<String, Object> askParams;
  final Map<String, dynamic> data;

  /// Исход неизвестен (ответ сервера не дошёл): действие могло пройти. Репозиторий повторит его ТЕМ ЖЕ ключом операции —
  /// второй раз деньги не проведутся, поэтому ИИ предлагает повторить просьбу или проверить на экране.
  final bool unknown;
  const HandleOutcome(this.status, {this.ask, this.askKey, this.askParams = const {}, this.data = const {}, this.unknown = false});

  static HandleOutcome ok([Map<String, dynamic> data = const {}]) => HandleOutcome(ToolStatus.ok, data: data);
  static HandleOutcome clarify(String ask) => HandleOutcome(ToolStatus.clarify, ask: ask);
  static HandleOutcome clarifyKey(String key, [Map<String, Object> params = const {}]) => HandleOutcome(ToolStatus.clarify, askKey: key, askParams: params);
  static HandleOutcome error(String message) => HandleOutcome(ToolStatus.error, ask: message);
  static HandleOutcome errorKey(String key, [Map<String, Object> params = const {}]) => HandleOutcome(ToolStatus.error, askKey: key, askParams: params);
  static HandleOutcome notFoundKey(String key, [Map<String, Object> params = const {}]) => HandleOutcome(ToolStatus.notFound, askKey: key, askParams: params);
}

/// Ручка экрана: заполнить поля из аргументов и нажать ту же кнопку.
class AiHandle {
  /// Заполнить поля экрана (способ оплаты, клиент, сумма наличных…). Вернуть `clarify` с вопросом экрана, если чего-то
  /// не хватает; null — всё заполнено.
  final Future<HandleOutcome?> Function(Map<String, dynamic> args) fill;

  /// Нажать кнопку экрана (`_pay`, `_submit`, `_post`, `_close`) и вернуть итог.
  final Future<HandleOutcome> Function() run;
  const AiHandle({required this.fill, required this.run});
}

/// Доклад экрана об итоге: `run` ручки запускает кнопку экрана и ждёт ПЕРВОГО доклада [report] (экран зовёт его там, где
/// показывал бы тост ошибки или «Оплачено»). Экран при этом идёт своим путём дальше (переход на «Оплачено» и т. п.) — ручка
/// возвращается сразу, как только итог известен. Нажали кнопку в обычном порядке (без ИИ) — [report] ничего не делает.
class OutcomeSink {
  Completer<HandleOutcome>? _c;

  /// Запустить [action] (кнопку экрана). [afterEnd] — что считать итогом, если экран закончил, ничего не доложив (или упал
  /// уже ПОСЛЕ проведения денег): null — «экран не завершил действие».
  Future<HandleOutcome> run(Future<void> Function() action, {HandleOutcome? Function()? afterEnd}) {
    final c = _c = Completer<HandleOutcome>();
    void finish(HandleOutcome fallback) {
      if (c.isCompleted) return;
      c.complete(afterEnd?.call() ?? fallback);
    }

    action().then(
      (_) => finish(HandleOutcome.errorKey('h.silent')),
      onError: (Object e, StackTrace s) => finish(HandleOutcome.errorKey('h.failed')),
    );
    return c.future;
  }

  void report(HandleOutcome o) {
    final c = _c;
    if (c != null && !c.isCompleted) c.complete(o);
  }
}

/// Кандидаты по словам просьбы («кола», «Асель»): все слова начала названия; при нескольких побеждает точное совпадение.
List<T> aiMatchByName<T>(Iterable<T> items, String query, String Function(T) name) {
  final hits = [for (final i in items) if (productNameMatchesQuery(name(i), query)) i];
  if (hits.length <= 1) return hits;
  final q = foldText(query).trim();
  final exact = [for (final i in hits) if (foldText(name(i)).trim() == q) i];
  return exact.length == 1 ? exact : hits;
}

class AiScreenHandles {
  final _handles = <String, AiHandle>{};
  final _waiters = <String, List<Completer<AiHandle?>>>{};

  void register(String key, AiHandle h) {
    _handles[key] = h;
    for (final c in _waiters.remove(key) ?? const <Completer<AiHandle?>>[]) {
      if (!c.isCompleted) c.complete(h);
    }
  }

  /// Снимает ручку, только если она всё ещё та же (новый экран мог уже зарегистрировать свою).
  void unregister(String key, AiHandle h) {
    if (identical(_handles[key], h)) _handles.remove(key);
  }

  AiHandle? get(String key) => _handles[key];

  /// Ждать регистрацию (экран открывается не мгновенно): раздел 3.5 ТЗ — до 2 с.
  Future<AiHandle?> waitFor(String key, {Duration timeout = const Duration(seconds: 2)}) {
    final have = _handles[key];
    if (have != null) return Future.value(have);
    final c = Completer<AiHandle?>();
    (_waiters[key] ??= []).add(c);
    return c.future.timeout(timeout, onTimeout: () {
      _waiters[key]?.remove(c);
      return null;
    });
  }
}

/// Общий реестр ручек приложения: экраны регистрируются в нём, звонок (ActionKit) берёт его же —
/// `ActionKit(handles: ref.read(aiHandlesProvider))`.
final aiHandlesProvider = Provider<AiScreenHandles>((ref) => AiScreenHandles());
