## v1.17.26 — тестовая версия (срочная)

---

# Русский

### Исправлено: было → стало

| Что | Было | Стало |
|---|---|---|
| Этикетка весов TM-30F (Dahua) с форматом штрихкода FFWWWWWEEEEEC — в штрихкоде СУММА | Касса находила товар, но читала сумму как вес: «Айфон» 0,220 кг × 160 = 35,20, штрихкод 2110007035207 → в чеке 3,520 кг и 563,20 | Для префикса весов, где в штрихкоде сумма, касса берёт сумму 35,20 и считает вес 0,220 кг |

### Как включить (один раз, на компьютере с весами)

1. ☰ → Настройки → Весы → «Настройки весов выбранной марки…» (марка TM-30F (Dahua)) → вкладка «Штрих-код».
2. «Формат (Barcode)» — тот, что стоит на весах: FFWWWWWEEEEEC (отмечен «сейчас на весах»).
3. «Префикс штрихкода» — как на этикетке, например 21.
4. «Сохранить».

Или на самих весах выбрать формат с весом FFWWWWWNNNNNC («Русский масштаб» → Системные параметры → Barcode → Download) — тогда настройка в кассе не нужна.

### Проверено

- Разбор кассой кодов с настоящих этикеток: 2134567012072 → сумма 12,07 (раньше вес 1,207); 2154321123455 → 123,45; обычный весовой 2000001003923 (префикс 20) по-прежнему вес 0,392 кг.
- Сборка установлена на рабочем компьютере, касса запускается, продажи проходят.

### Не проверено

- Скан этикетки на компьютере с весами после обновления — проверьте первой этикеткой.

---

# Кыргызча

### Оңдолду: мурун → азыр

| Эмне | Мурун | Азыр |
|---|---|---|
| TM-30F (Dahua) таразасынын FFWWWWWEEEEEC форматындагы этикеткасы — штрих-коддо СУММА | Касса товарды тапчу, бирок сумманы салмак катары окучу: 0,220 кг × 160 = 35,20 ордуна 3,520 кг жана 563,20 | Штрих-коддо сумма бар префикс үчүн касса 35,20 сумманы алып, салмакты 0,220 кг деп эсептейт |

### Кантип күйгүзүү (бир жолу, таразасы бар компьютерде)

«TM-30F таразасынын жөндөөлөрү» → «Штрих-код» → таразадагы формат FFWWWWWEEEEEC жана этикеткадагы префикс (мисалы 21) → «Сактоо». Же таразада салмак форматын FFWWWWWNNNNNC тандаңыз.

### Текшерилди

- Чыныгы этикеткалардын коддору: 2134567012072 → сумма 12,07; 2000001003923 (префикс 20) мурдагыдай салмак 0,392 кг.

---

# English

### Fixed: before → now

| What | Before | Now |
|---|---|---|
| TM-30F (Dahua) label with barcode format FFWWWWWEEEEEC — the barcode holds the TOTAL | The till found the product but read the total as weight: 0.220 kg × 160 = 35.20 became 3.520 kg and 563.20 | For a scale prefix whose barcode holds the total, the till takes 35.20 and computes 0.220 kg |

### How to turn it on (once, on the PC with the scale)

“TM-30F scale settings” → “Barcode” → the format set on the scale FFWWWWWEEEEEC and the prefix from the label (e.g. 21) → “Save”. Or choose the weight format FFWWWWWNNNNNC on the scale.

### Checked

- Codes from real labels: 2134567012072 → total 12.07; 2000001003923 (prefix 20) still weight 0.392 kg.

---

# Türkçe

### Düzeltildi: önce → şimdi

| Ne | Önce | Şimdi |
|---|---|---|
| FFWWWWWEEEEEC barkod biçimli TM-30F (Dahua) etiketi — barkodda TUTAR | Kasa ürünü buluyor ama tutarı ağırlık okuyordu: 0,220 kg × 160 = 35,20 yerine 3,520 kg ve 563,20 | Barkodunda tutar olan önek için kasa 35,20’yi alır ve 0,220 kg hesaplar |

### Nasıl açılır (bir kez, terazili bilgisayarda)

«TM-30F terazi ayarları» → «Barkod» → terazideki biçim FFWWWWWEEEEEC ve etiketteki önek (örn. 21) → «Kaydet». Ya da teraziyi ağırlık biçimine FFWWWWWNNNNNC alın.

### Kontrol edildi

- Gerçek etiket kodları: 2134567012072 → tutar 12,07; 2000001003923 (önek 20) hâlâ ağırlık 0,392 kg.

---

# O'zbekcha

### Tuzatildi: oldin → hozir

| Nima | Oldin | Hozir |
|---|---|---|
| FFWWWWWEEEEEC shtrix-kod formatidagi TM-30F (Dahua) yorlig'i — shtrix-kodda SUMMA | Kassa tovarni topardi, lekin summani vazn deb o'qirdi: 0,220 kg × 160 = 35,20 o'rniga 3,520 kg va 563,20 | Shtrix-kodida summa bor prefiks uchun kassa 35,20 ni oladi va vaznni 0,220 kg deb hisoblaydi |

### Qanday yoqish (bir marta, tarozili kompyuterda)

«TM-30F tarozi sozlamalari» → «Shtrix-kod» → tarozidagi format FFWWWWWEEEEEC va yorliqdagi prefiks (masalan 21) → «Saqlash». Yoki tarozida vazn formatini FFWWWWWNNNNNC tanlang.

### Tekshirildi

- Haqiqiy yorliq kodlari: 2134567012072 → summa 12,07; 2000001003923 (prefiks 20) avvalgidek vazn 0,392 kg.
