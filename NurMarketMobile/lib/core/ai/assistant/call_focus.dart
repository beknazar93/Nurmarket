// 2026-10-27 (мобилка, видеозвонок, М2): «этот товар» в звонке. Пользователь показывает товар камерой («Что это?») и говорит
// «поставь цену 120» или «переименуй его» — модель передаёт `ref: "camera"`, и инструменты берут товар с последней карточки
// камеры, а не ищут по пересказанному названию (строгий поиск по пересказу и был причиной «не нашла»). `ref: "last"` — товар,
// с которым последним работал любой инструмент звонка. Файл без Flutter и сети.
import '../../db/database.dart';

class CallFocus {
  /// 2026-10-27 (ревью М-4): «этот товар» с камеры живёт недолго — через [cameraTtl] после кадра ссылка `camera` (и `last`,
  /// полученный с камеры) протухает, и инструмент переспрашивает «какой товар?», а не правит прошлый.
  static const cameraTtl = Duration(minutes: 2);

  /// Часы (в проверках подменяются).
  DateTime Function() now = DateTime.now;

  Product? _camera;
  DateTime _cameraAt = DateTime.fromMillisecondsSinceEpoch(0);
  Product? _last;
  bool _lastViaCamera = false;

  bool get _cameraExpired => now().difference(_cameraAt) > cameraTtl;

  /// Товар последней карточки камеры (найден по штрихкоду / выбран из «Похоже на»); null — не было или протух.
  Product? get camera => _camera != null && !_cameraExpired ? _camera : null;
  set camera(Product? p) {
    _camera = p;
    _cameraAt = now();
  }

  /// Товар, с которым последним работал инструмент (поиск, цена, правка); полученный с камеры — только пока камера свежая.
  Product? get last => _last != null && _lastViaCamera && _cameraExpired ? null : _last;
  set last(Product? p) {
    _last = p;
    _lastViaCamera = false;
  }

  void setCamera(Product p) {
    camera = p;
    _last = p;
    _lastViaCamera = true;
  }

  void setLast(Product p) => last = p;

  /// 2026-10-27 (ревью М-4): новый кадр камеры. Узнали один товар — он «этот»; не узнали (варианты, ошибка, другая карточка) —
  /// прежний товар камеры и «последний» сбрасываются, иначе «поставь этому цену» тихо правит прошлый товар.
  void newFrame(Product? found) {
    if (found != null) {
      setCamera(found);
    } else {
      _camera = null;
      _last = null;
      _lastViaCamera = false;
    }
  }

  void clear() {
    _camera = null;
    _last = null;
    _lastViaCamera = false;
  }

  /// Товар по ссылке: `camera` — с камеры, `last` — последний; иное — null.
  Product? byRef(String ref) => switch (ref.trim().toLowerCase()) {
    'camera' || 'cam' || 'video' => camera,
    // 2026-10-27 (ревью М-4): «этот товар», когда камера показывает один товар, а последним правили другой, — неоднозначно:
    // null → инструмент спросит «о каком товаре речь», а не угадает
    'last' || 'this' || 'current' => (last != null && camera != null && last!.id != camera!.id) ? null : (last ?? camera),
    _ => null,
  };

  static bool isRef(String ref) => const {'camera', 'cam', 'video', 'last', 'this', 'current'}.contains(ref.trim().toLowerCase());
}

/// Что нужно общим инструментам от мастера «добавить товар»: «отмена» вслух, пока идёт мастер, сбрасывает черновик, а не
/// откатывает прошлое действие ИИ (цена, остаток). Реализует [ProductWizard] (product_wizard.dart).
abstract class WizardHook {
  bool get active;

  /// Сбросить черновик; возвращает фразу для пользователя на языке [lang].
  String cancelNow(String lang);
}
