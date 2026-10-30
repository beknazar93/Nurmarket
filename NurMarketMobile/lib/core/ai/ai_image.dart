import 'dart:isolate';
import 'dart:typed_data';

import 'package:image/image.dart' as img;

/// 2026-10-30 (оптимизация мобилки, п. 5): снимок для ИИ уменьшается до [maxSide] пикселей по длинной стороне и пересжимается
/// в JPEG (в отдельном изоляте — интерфейс не замирает). Камера отдавала 1–3 МБ, это 1,3–4 МБ исходящего трафика на каждое
/// распознавание и лишняя задержка; для чтения упаковки хватает 1280 px (накладные — 2048 px, чтобы мелкий текст не пропал).
/// Размер читается из заголовка JPEG без разбора картинки: снимок камеры в пределах нормы (пресет high = 1280×720) возвращается
/// сразу. Любая неудача (не картинка, повреждён) — возвращается исходный файл без изменений.
Future<Uint8List> shrinkForAi(Uint8List bytes, {int maxSide = 1280, int quality = 80}) async {
  final size = jpegSize(bytes);
  if (size != null && size.$1 <= maxSide && size.$2 <= maxSide) return bytes; // уже в пределах нормы
  // Не JPEG (PNG, WebP…) и маленький файл (до ~200 КБ) — не тратим время на разбор.
  if (size == null && bytes.length < 200 * 1024) return bytes;
  try {
    final out = await Isolate.run(() => shrinkSync(bytes, maxSide: maxSide, quality: quality));
    return out ?? bytes;
  } catch (_) {
    return bytes;
  }
}

/// Ширина и высота JPEG по заголовку (маркер SOFn) — без разбора самой картинки; null — не JPEG или заголовок не найден.
(int, int)? jpegSize(Uint8List b) {
  if (b.length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
  var i = 2;
  while (i + 9 < b.length) {
    if (b[i] != 0xFF) {
      i++;
      continue;
    }
    final m = b[i + 1];
    if (m == 0xFF) {
      i++;
      continue;
    }
    if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) {
      i += 2;
      continue;
    }
    final len = (b[i + 2] << 8) | b[i + 3];
    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) {
      return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
    }
    if (len < 2) return null;
    i += 2 + len;
  }
  return null;
}

/// Синхронная часть (для проверки в тестах без изолята). null — менять нечего (картинка и так в пределах или не разобралась).
Uint8List? shrinkSync(Uint8List bytes, {required int maxSide, int quality = 80}) {
  final decoded = img.decodeImage(bytes);
  if (decoded == null) return null;
  if (decoded.width <= maxSide && decoded.height <= maxSide) return null;
  final oriented = img.bakeOrientation(decoded);
  final landscape = oriented.width >= oriented.height;
  final small = img.copyResize(oriented, width: landscape ? maxSide : null, height: landscape ? null : maxSide, interpolation: img.Interpolation.average);
  final out = Uint8List.fromList(img.encodeJpg(small, quality: quality));
  return out.length < bytes.length ? out : null;
}
