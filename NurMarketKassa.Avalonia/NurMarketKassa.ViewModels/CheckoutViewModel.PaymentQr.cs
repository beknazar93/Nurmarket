using NurMarketKassa.Services;
using System;
using System.Globalization;
using System.IO;

namespace NurMarketKassa.ViewModels
{
    // 2026-11-12 (жалоба тестера, переслал владелец: «не генерируется QR код для кассовой стороны (сделать QR побольше для тех, кто использует 1 экран)»).
    // Причина: QR с уже вписанной суммой (Настройки → Операции → «QR с суммой») строился ТОЛЬКО для экрана покупателя (CustomerDisplayViewModel.InformationImagePath),
    // а окно оплаты кассы всегда показывало исходную картинку банка 84×84 (64×64 на 1024×600). У кого второго монитора нет, покупатель видел лишь крошечный QR без суммы.
    // Теперь окно оплаты берёт тот же QR, что и экран покупателя (с суммой, если она включена), а при одном экране показывает его крупно (≥ 240 точек),
    // с подписью суммы и банка и кнопкой «Показать QR крупнее». Расчёт денег не трогается — здесь только показ.
    public partial class CheckoutViewModel
    {
        /// <summary>Размер PNG динамического QR для окна оплаты (рисуется с запасом под показ «крупнее»).</summary>
        private const int CashierQrSizePx = 640;

        private bool? _largeQr;
        private string? _cashierQrPath;
        private string? _cashierQrKey;
        private string? _cashierDynamicPath;
        private bool _dynamicQrFailed;

        /// <summary>true — экран один (или QR на экране покупателя не виден): QR крупно в окне оплаты. Определяется один раз при открытии окна.</summary>
        public bool IsLargeQr => _largeQr ??= !(_customerDisplay?.ShowsPaymentQrToCustomer ?? false);

        /// <summary>Маленькая карточка QR (есть экран покупателя с QR) — как раньше. С учётом способа оплаты (банк виден только в безнале и смешанной):
        /// по переходу этого флага окно прокручивает к QR и закрывает «QR крупнее».</summary>
        public bool ShowQrCardCompact => IsBankSelectionVisible && ShowQrCard && !IsLargeQr;

        /// <summary>Крупная карточка QR (один экран).</summary>
        public bool ShowQrCardLarge => IsBankSelectionVisible && ShowQrCard && IsLargeQr;

        /// <summary>Сумма, которую покупатель платит по QR: в смешанной оплате — безналичная часть, иначе — к оплате.</summary>
        private double QrAmountValue => IsMixedMode ? ParseNonNegative(_mixedNonCashAmount) ?? 0 : EffectivePayableNow;

        /// <summary>Картинка QR для окна оплаты: при включённом «QR с суммой» — QR с суммой (тот же построитель, что у экрана покупателя),
        /// иначе или при осечке — загруженная картинка банка. null — QR нет (картой, банк без QR).</summary>
        public string? CashierQrImagePath => _cashierQrPath;

        /// <summary>«QR с суммой» включён, но собрать его не вышло (картинку банка не распознали, это не ELQR) — показан обычный QR.</summary>
        public bool HasDynamicQrError => _dynamicQrFailed && HasQrCode;

        public string DynamicQrErrorText => Tr.T(
            "QR с суммой не собран — показан обычный QR банка, покупатель вводит сумму сам",
            "Суммасы бар QR түзүлгөн жок — банктын жөнөкөй QR'ы көрсөтүлдү, сатып алуучу сумманы өзү жазат",
            "Could not build a QR with the amount — the bank's regular QR is shown, the customer enters the amount",
            "Tutarlı QR oluşturulamadı — bankanın normal QR'ı gösteriliyor, müşteri tutarı kendisi girer",
            "Summali QR yaratilmadi — bankning oddiy QR'i ko'rsatildi, xaridor summani o'zi kiritadi");

        /// <summary>Картинку QR не удалось прочитать (файл повреждён, не картинка) — текст вместо пустого белого квадрата.</summary>
        public string QrImageErrorText => Tr.T(
            "QR не показан: картинка QR банка не читается. Загрузите её заново: Настройки → Операции. Оплату можно подтвердить Enter.",
            "QR көрсөтүлгөн жок: банктын QR сүрөтү окулбайт. Аны кайра жүктөңүз: Жөндөөлөр → Операциялар. Төлөмдү Enter менен ырастаса болот.",
            "QR not shown: the bank's QR image can't be read. Upload it again: Settings → Operations. You can still confirm the payment with Enter.",
            "QR gösterilmedi: bankanın QR resmi okunamıyor. Yeniden yükleyin: Ayarlar → İşlemler. Ödemeyi Enter ile onaylayabilirsiniz.",
            "QR ko'rsatilmadi: bankning QR rasmi o'qilmayapti. Uni qayta yuklang: Sozlamalar → Operatsiyalar. To'lovni Enter bilan tasdiqlash mumkin.");

        /// <summary>«QR · MBank» под крупным QR.</summary>
        public string QrPayMethodText => string.IsNullOrWhiteSpace(SelectedBank?.BankName)
            ? QrCardTitle
            : $"{QrCardTitle} · {SelectedBank!.BankName}";

        public string ShowQrLargeText => Tr.T("Показать QR крупнее", "QR'ды чоңураак көрсөтүү", "Show QR larger", "QR'ı büyüt", "QR'ni kattaroq ko'rsatish");

        public string QrLargeCloseHint => Tr.T("Нажмите в любом месте или Esc — закрыть", "Каалаган жерди басыңыз же Esc — жабуу",
            "Tap anywhere or press Esc to close", "Kapatmak için herhangi bir yere dokunun veya Esc", "Yopish uchun istalgan joyni bosing yoki Esc");

        /// <summary>Пересчитать картинку QR окна оплаты (банк, «Карта/QR», сумма, смешанная оплата). Вызывается из RaiseMixedViewProps.</summary>
        private void RefreshCashierQr()
        {
            var staticPath = QrCodePath;
            string? path = staticPath;
            var failed = false;
            var amount = Math.Round(QrAmountValue, 2);
            if (staticPath is not null && UserPreferences.Instance.DynamicPaymentQrEnabled && amount > 0.004)
            {
                var key = staticPath + "|" + amount.ToString("0.00", CultureInfo.InvariantCulture);
                if (key != _cashierQrKey || (_cashierDynamicPath is not null && !File.Exists(_cashierDynamicPath)))
                {
                    _cashierQrKey = key;
                    _cashierDynamicPath = DynamicPaymentQrService.TryBuildDynamicQrImage(staticPath, (decimal)amount, CashierQrSizePx);
                }
                path = _cashierDynamicPath ?? staticPath;
                failed = _cashierDynamicPath is null;
            }

            if (!string.Equals(path, _cashierQrPath, StringComparison.Ordinal))
            {
                _cashierQrPath = path;
                OnPropertyChanged(nameof(CashierQrImagePath));
            }
            if (failed != _dynamicQrFailed)
            {
                _dynamicQrFailed = failed;
                OnPropertyChanged(nameof(HasDynamicQrError));
            }
            OnPropertyChanged(nameof(ShowQrCardCompact));
            OnPropertyChanged(nameof(ShowQrCardLarge));
            OnPropertyChanged(nameof(QrPayMethodText));
        }

        /// <summary>2026-11-12: окно оплаты закрыто (оплата, Esc, ✕) — экрану покупателя сбрасываем выбранный банк и сумму QR. Раньше это делала
        /// только команда «Отмена» модели, а Esc/✕ и успешная оплата оставляли сумму смешанной оплаты висеть до следующего окна оплаты.</summary>
        public void ResetCustomerDisplayQr() => _customerDisplay?.SetSelectedBankQrPath(null);
    }
}
