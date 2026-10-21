import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';

/// 13.10, владелец: «при оплате где выбор клиента? и как клиент может взять скидку из своих бонусов?» — бонусы NurCRM,
/// те же запросы, что у кассы (ServerLoyalty): баланс — bonus_balance клиента, включены ли баллы и процент начисления —
/// настройка магазина api/main/app-shop-settings/, списание и начисление — POST api/main/clients/{id}/bonus/ с номером продажи.
class LoyaltyRepository {
  final ApiClient api;
  LoyaltyRepository(this.api);

  bool? _enabled;
  double _percent = 0;

  /// (баллы включены, процент начисления).
  Future<(bool, double)> settings() async {
    if (_enabled != null) return (_enabled!, _percent);
    try {
      final data = await api.get('api/main/app-shop-settings/', timeout: const Duration(seconds: 8));
      if (data is Map) {
        final m = data.cast<String, dynamic>();
        _enabled = m['points_enabled'] != false;
        _percent = num0(m, 'points_percent').clamp(0, 100).toDouble();
      }
    } on ApiException {
      // без связи — бонусы на этой продаже не трогаем
      return (false, 0.0);
    }
    return (_enabled ?? false, _percent);
  }

  Future<double> balance(String clientId) async {
    final data = await api.get('api/main/clients/$clientId/', timeout: const Duration(seconds: 8));
    return data is Map ? num0(data.cast<String, dynamic>(), 'bonus_balance') : 0;
  }

  /// reason: redeem (delta < 0) | earn (delta > 0); sale — id продажи, чтобы возврат чека мог их отменить.
  Future<void> post(String clientId, double delta, String reason, String? saleId) async {
    await api.post(
      'api/main/clients/$clientId/bonus/',
      body: {'delta': double.parse(delta.toStringAsFixed(2)), 'reason': reason, if (saleId != null && saleId.isNotEmpty) 'sale': saleId},
    );
  }
}

final loyaltyRepoProvider = Provider((ref) => LoyaltyRepository(ref.watch(apiProvider)));
