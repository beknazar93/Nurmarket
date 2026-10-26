// 2026-10-26 (голос-управление, В0): протокол инструментов (function calling) Gemini Live — вызов от сервера, ответ,
// схема параметров, кэш ответов по id, речь пользователя. Сами инструменты и права — assistant/tool_registry.dart.
// Файл без Flutter и без сети: его проверяют тесты на фейках.
import 'dart:convert';

/// Флаг отмены вызова: сервер прислал toolCallCancellation (пользователь перебил). Обработчик может смотреть [isCancelled];
/// ответ, пришедший после отмены, звонок выбрасывает. Уже открытый экран не закрывается.
class ToolCancel {
  bool _cancelled = false;
  bool get isCancelled => _cancelled;
  void cancel() => _cancelled = true;
}

/// Один вызов инструмента от модели: toolCall.functionCalls[].{id,name,args}.
class LiveToolCall {
  final String id;
  final String name;
  final Map<String, dynamic> args;
  final ToolCancel cancel = ToolCancel();
  LiveToolCall(this.id, this.name, this.args);

  /// Из элемента functionCalls; без имени — null. args может прийти и JSON-строкой — разбираем и её.
  static LiveToolCall? fromJson(Object? j) {
    if (j is! Map) return null;
    final name = j['name'];
    if (name is! String || name.isEmpty) return null;
    var a = j['args'];
    if (a is String) {
      try {
        a = jsonDecode(a);
      } catch (_) {
        a = null;
      }
    }
    return LiveToolCall(
      j['id'] is String ? j['id'] as String : '',
      name,
      a is Map ? Map<String, dynamic>.from(a) : <String, dynamic>{},
    );
  }

  @override
  String toString() => 'LiveToolCall($id, $name)';
}

/// Обработчик звонка: ответ инструмента (response из functionResponses) — status/say/data/…, см. [ToolResult.toJson].
typedef ToolHandler = Future<Map<String, dynamic>> Function(LiveToolCall call);

/// Ответ-заглушка, если инструмент не уложился в срок (звонок шлёт его модели, итог придёт позже текстом).
Map<String, dynamic> pendingResponse([String say = 'Данные ещё грузятся']) => {'status': 'pending', 'say': say};

// ---------------------------------------------------------------------------------------------------------------------
// Схема параметров (Schema из Live API). Типы — заглавными, как в документации (OBJECT/STRING/…): сверить в В9 (раздел 15 ТЗ).
// ---------------------------------------------------------------------------------------------------------------------

Map<String, dynamic> sStr({String? desc, List<String>? values}) => {
  'type': 'STRING',
  'description': ?desc,
  'enum': ?values,
};

Map<String, dynamic> sNum({String? desc}) => {'type': 'NUMBER', 'description': ?desc};

Map<String, dynamic> sInt({String? desc}) => {'type': 'INTEGER', 'description': ?desc};

Map<String, dynamic> sBool({String? desc}) => {'type': 'BOOLEAN', 'description': ?desc};

Map<String, dynamic> sArr(Map<String, dynamic> items, {String? desc}) => {
  'type': 'ARRAY',
  'items': items,
  'description': ?desc,
};

Map<String, dynamic> sObj(Map<String, Map<String, dynamic>> props, {List<String> required = const []}) => {
  'type': 'OBJECT',
  'properties': props,
  if (required.isNotEmpty) 'required': required,
};

/// Объявление инструмента для setup.tools[].functionDeclarations. Описание — по-английски (модель точнее выбирает инструмент).
class ToolDecl {
  final String name;
  final String description;

  /// Схема параметров (OBJECT); null — инструмент без параметров.
  final Map<String, dynamic>? parameters;
  const ToolDecl(this.name, this.description, {this.parameters});

  Map<String, dynamic> toJson() => {
    'name': name,
    'description': description,
    'parameters': ?parameters,
  };

  /// Размер объявлений в символах JSON — ориентир ТЗ ≤ 8000 (раздел 3.4), иначе звонок дороже.
  static int sizeOf(Iterable<Map<String, dynamic>> decls) => jsonEncode(decls.toList()).length;
}

/// Кэш ответов по id вызова: повтор того же id (после переподключения) отдаёт сохранённый ответ и не выполняется заново.
class ToolResponseCache {
  final int limit;
  final _done = <String, Map<String, dynamic>>{};
  final _running = <String>{};
  ToolResponseCache({this.limit = 200});

  /// Вызов идёт сейчас (ответ ещё не готов).
  bool isRunning(String id) => _running.contains(id);
  void markRunning(String id) => _running.add(id);
  Map<String, dynamic>? get(String id) => _done[id];

  void put(String id, Map<String, dynamic> response) {
    _running.remove(id);
    _done.remove(id);
    _done[id] = response;
    while (_done.length > limit) {
      _done.remove(_done.keys.first);
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Речь пользователя: расшифровка микрофона по репликам. Ворота намерения смотрят ТОЛЬКО сюда (раздел 2.5 г ТЗ).
// ---------------------------------------------------------------------------------------------------------------------

/// Реплика пользователя из расшифровки микрофона. [echoSuspect] — вся она пришлась на время, пока играл ответ ИИ
/// (эхо динамика): такая реплика ворота не открывает.
class UserTurn {
  final StringBuffer _text = StringBuffer();
  bool echoSuspect;
  DateTime at;
  DateTime updated;
  UserTurn(this.at, {this.echoSuspect = false}) : updated = at;
  String get text => _text.toString().trim();
}

class UserSpeechLog {
  final int keep;
  final turns = <UserTurn>[];
  bool _closed = true;
  UserSpeechLog({this.keep = 10});

  /// Кусок расшифровки (Gemini присылает реплику частями — склеиваем встык). После [closeTurn] начинается новая реплика.
  void add(String fragment, {bool echoSuspect = false, DateTime? now}) {
    if (fragment.isEmpty) return;
    final t = now ?? DateTime.now();
    if (_closed || turns.isEmpty) {
      turns.add(UserTurn(t, echoSuspect: echoSuspect));
      while (turns.length > keep) {
        turns.removeAt(0);
      }
      _closed = false;
    }
    final cur = turns.last;
    cur._text.write(fragment);
    cur.updated = t;
    // Реплика эхо, только если эхо все её куски: один настоящий кусок снимает подозрение.
    cur.echoSuspect = cur.echoSuspect && echoSuspect;
  }

  /// Реплика закончилась (ИИ начал отвечать, перебили) — следующий кусок откроет новую.
  void closeTurn() => _closed = true;

  UserTurn? get last => turns.isEmpty ? null : turns.last;

  /// Реплика перед последней (для ответа на уточнение).
  UserTurn? get previous => turns.length < 2 ? null : turns[turns.length - 2];
}

String _norm(String s) => s
    .toLowerCase()
    .replaceAll('ё', 'е')
    .replaceAll(RegExp('[’ʻʼ`]'), "'");

/// 2026-10-26 (голос-управление, В0): «замолчи / тише / стоп» программа ловит сама — быстрее, чем ждать модель (раздел 5.4 ТЗ).
/// Только короткая реплика (до 4 слов): «стоп» в длинной фразе («стоп-кран пробей») — не просьба замолчать.
bool isSilenceRequest(String text) {
  final words = _norm(text).split(RegExp(r"[^\p{L}\p{N}']+", unicode: true)).where((w) => w.isNotEmpty).toList();
  if (words.isEmpty || words.length > 4) return false;
  const exact = {'стоп', 'stop', 'dur', 'sus', 'bas', 'jim', 'enough', 'quiet', 'тихо', 'тише', 'жым', 'хватит'};
  const prefixes = ['замолч', 'помолч', 'молч', 'токто', 'тынч', 'sessiz', 'yeter', "to'xta", 'toxta', 'shut'];
  return words.any((w) => exact.contains(w) || prefixes.any(w.startsWith));
}
