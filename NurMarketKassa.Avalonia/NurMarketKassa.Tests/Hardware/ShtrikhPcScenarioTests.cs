using System.Text.RegularExpressions;
using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06 (копия папки весов владельца): главный экран РС-200 строится из сценария самообслуживания <c>Shared\SelfScenario\Semya.ssc</c> — списка кнопок, привязанных к кодам
/// товаров. Касса пересобирает его из выгруженных товаров. Эталон — настоящий Semya.ssc владельца (46 товаров на 2 экранах, 190 строк): генерация для тех же кодов в том же порядке совпадает
/// с ним построчно; различаются только номера плиток (в оригинале они не сквозные: 4, 11, 13, 17 … — номера старой системы, у кассы — подряд с 1).</summary>
public sealed class ShtrikhPcScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pcscn-" + Guid.NewGuid().ToString("N")[..8]);

    public ShtrikhPcScenarioTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly long[] ReferenceCodes = { 26629, 62821, 1979, 63337, 26208, 59768, 5024, 1995, 63086, 21586, 3591, 8703, 48104, 62618, 1976, 69312, 3625, 1998, 26872, 61866, 62823, 62405, 61988, 54501, 96034, 50155, 8618, 48002, 63456, 2125, 51257, 3596, 26956, 63377, 74084, 36656, 49574, 73621, 77485, 88098, 63462, 86962, 87589, 14926, 23713, 74457 };

    private const string Reference = """
<F 100000; 19; 2; Verdana
<F 100001; 12; 2; Verdana
<W 1; 0; 0; 0; 1024; 584; 0; 0; 0; 0;
<B 2; 1; 4; 4; 166; 112; 1; 26629; 0; 0;
<T 3; 2; 7; 17; 160; 96; 0; 0; 4; 26629; ; 0; 0; 0; 0; 0; 100001
<P 4; 2; 7; 7; 160; 106; 0; 0; 4; 26629; ; 1; 0; 0
<T 5; 2; 7; 7; 160; 16; 0; 0; 3; 4; ; 16711935; 16711935; 2; 1; 1; 100000
<B 6; 1; 174; 4; 166; 112; 1; 62821; 0; 0;
<T 7; 6; 177; 17; 160; 96; 0; 0; 4; 62821; ; 0; 0; 0; 0; 0; 100001
<P 8; 6; 177; 7; 160; 106; 0; 0; 4; 62821; ; 1; 0; 0
<T 9; 6; 177; 7; 160; 16; 0; 0; 3; 11; ; 16711935; 16711935; 2; 1; 1; 100000
<B 10; 1; 344; 4; 166; 112; 1; 1979; 0; 0;
<T 11; 10; 347; 17; 160; 96; 0; 0; 4; 1979; ; 0; 0; 0; 0; 0; 100001
<P 12; 10; 347; 7; 160; 106; 0; 0; 4; 1979; ; 1; 0; 0
<T 13; 10; 347; 7; 160; 16; 0; 0; 3; 13; ; 16711935; 16711935; 2; 1; 1; 100000
<B 14; 1; 514; 4; 166; 112; 1; 63337; 0; 0;
<T 15; 14; 517; 17; 160; 96; 0; 0; 4; 63337; ; 0; 0; 0; 0; 0; 100001
<P 16; 14; 517; 7; 160; 106; 0; 0; 4; 63337; ; 1; 0; 0
<T 17; 14; 517; 7; 160; 16; 0; 0; 3; 17; ; 16711935; 16711935; 2; 1; 1; 100000
<B 18; 1; 684; 4; 166; 112; 1; 26208; 0; 0;
<T 19; 18; 687; 17; 160; 96; 0; 0; 4; 26208; ; 0; 0; 0; 0; 0; 100001
<P 20; 18; 687; 7; 160; 106; 0; 0; 4; 26208; ; 1; 0; 0
<T 21; 18; 687; 7; 160; 16; 0; 0; 3; 20; ; 16711935; 16711935; 2; 1; 1; 100000
<B 22; 1; 854; 4; 166; 112; 1; 59768; 0; 0;
<T 23; 22; 857; 17; 160; 96; 0; 0; 4; 59768; ; 0; 0; 0; 0; 0; 100001
<P 24; 22; 857; 7; 160; 106; 0; 0; 4; 59768; ; 1; 0; 0
<T 25; 22; 857; 7; 160; 16; 0; 0; 3; 21; ; 16711935; 16711935; 2; 1; 1; 100000
<B 26; 1; 4; 120; 166; 112; 1; 5024; 0; 0;
<T 27; 26; 7; 133; 160; 96; 0; 0; 4; 5024; ; 0; 0; 0; 0; 0; 100001
<P 28; 26; 7; 123; 160; 106; 0; 0; 4; 5024; ; 1; 0; 0
<T 29; 26; 7; 123; 160; 16; 0; 0; 3; 27; ; 16711935; 16711935; 2; 1; 1; 100000
<B 30; 1; 174; 120; 166; 112; 1; 1995; 0; 0;
<T 31; 30; 177; 133; 160; 96; 0; 0; 4; 1995; ; 0; 0; 0; 0; 0; 100001
<P 32; 30; 177; 123; 160; 106; 0; 0; 4; 1995; ; 1; 0; 0
<T 33; 30; 177; 123; 160; 16; 0; 0; 3; 38; ; 16711935; 16711935; 2; 1; 1; 100000
<B 34; 1; 344; 120; 166; 112; 1; 63086; 0; 0;
<T 35; 34; 347; 133; 160; 96; 0; 0; 4; 63086; ; 0; 0; 0; 0; 0; 100001
<P 36; 34; 347; 123; 160; 106; 0; 0; 4; 63086; ; 1; 0; 0
<T 37; 34; 347; 123; 160; 16; 0; 0; 3; 46; ; 16711935; 16711935; 2; 1; 1; 100000
<B 38; 1; 514; 120; 166; 112; 1; 21586; 0; 0;
<T 39; 38; 517; 133; 160; 96; 0; 0; 4; 21586; ; 0; 0; 0; 0; 0; 100001
<P 40; 38; 517; 123; 160; 106; 0; 0; 4; 21586; ; 1; 0; 0
<T 41; 38; 517; 123; 160; 16; 0; 0; 3; 47; ; 16711935; 16711935; 2; 1; 1; 100000
<B 42; 1; 684; 120; 166; 112; 1; 3591; 0; 0;
<T 43; 42; 687; 133; 160; 96; 0; 0; 4; 3591; ; 0; 0; 0; 0; 0; 100001
<P 44; 42; 687; 123; 160; 106; 0; 0; 4; 3591; ; 1; 0; 0
<T 45; 42; 687; 123; 160; 16; 0; 0; 3; 51; ; 16711935; 16711935; 2; 1; 1; 100000
<B 46; 1; 854; 120; 166; 112; 1; 8703; 0; 0;
<T 47; 46; 857; 133; 160; 96; 0; 0; 4; 8703; ; 0; 0; 0; 0; 0; 100001
<P 48; 46; 857; 123; 160; 106; 0; 0; 4; 8703; ; 1; 0; 0
<T 49; 46; 857; 123; 160; 16; 0; 0; 3; 52; ; 16711935; 16711935; 2; 1; 1; 100000
<B 50; 1; 4; 236; 166; 112; 1; 48104; 0; 0;
<T 51; 50; 7; 249; 160; 96; 0; 0; 4; 48104; ; 0; 0; 0; 0; 0; 100001
<P 52; 50; 7; 239; 160; 106; 0; 0; 4; 48104; ; 1; 0; 0
<T 53; 50; 7; 239; 160; 16; 0; 0; 3; 64; ; 16711935; 16711935; 2; 1; 1; 100000
<B 54; 1; 174; 236; 166; 112; 1; 62618; 0; 0;
<T 55; 54; 177; 249; 160; 96; 0; 0; 4; 62618; ; 0; 0; 0; 0; 0; 100001
<P 56; 54; 177; 239; 160; 106; 0; 0; 4; 62618; ; 1; 0; 0
<T 57; 54; 177; 239; 160; 16; 0; 0; 3; 65; ; 16711935; 16711935; 2; 1; 1; 100000
<B 58; 1; 344; 236; 166; 112; 1; 1976; 0; 0;
<T 59; 58; 347; 249; 160; 96; 0; 0; 4; 1976; ; 0; 0; 0; 0; 0; 100001
<P 60; 58; 347; 239; 160; 106; 0; 0; 4; 1976; ; 1; 0; 0
<T 61; 58; 347; 239; 160; 16; 0; 0; 3; 70; ; 16711935; 16711935; 2; 1; 1; 100000
<B 62; 1; 514; 236; 166; 112; 1; 69312; 0; 0;
<T 63; 62; 517; 249; 160; 96; 0; 0; 4; 69312; ; 0; 0; 0; 0; 0; 100001
<P 64; 62; 517; 239; 160; 106; 0; 0; 4; 69312; ; 1; 0; 0
<T 65; 62; 517; 239; 160; 16; 0; 0; 3; 74; ; 16711935; 16711935; 2; 1; 1; 100000
<B 66; 1; 684; 236; 166; 112; 1; 3625; 0; 0;
<T 67; 66; 687; 249; 160; 96; 0; 0; 4; 3625; ; 0; 0; 0; 0; 0; 100001
<P 68; 66; 687; 239; 160; 106; 0; 0; 4; 3625; ; 1; 0; 0
<T 69; 66; 687; 239; 160; 16; 0; 0; 3; 78; ; 16711935; 16711935; 2; 1; 1; 100000
<B 70; 1; 854; 236; 166; 112; 1; 1998; 0; 0;
<T 71; 70; 857; 249; 160; 96; 0; 0; 4; 1998; ; 0; 0; 0; 0; 0; 100001
<P 72; 70; 857; 239; 160; 106; 0; 0; 4; 1998; ; 1; 0; 0
<T 73; 70; 857; 239; 160; 16; 0; 0; 3; 88; ; 16711935; 16711935; 2; 1; 1; 100000
<B 74; 1; 4; 352; 166; 112; 1; 26872; 0; 0;
<T 75; 74; 7; 365; 160; 96; 0; 0; 4; 26872; ; 0; 0; 0; 0; 0; 100001
<P 76; 74; 7; 355; 160; 106; 0; 0; 4; 26872; ; 1; 0; 0
<T 77; 74; 7; 355; 160; 16; 0; 0; 3; 90; ; 16711935; 16711935; 2; 1; 1; 100000
<B 78; 1; 174; 352; 166; 112; 1; 61866; 0; 0;
<T 79; 78; 177; 365; 160; 96; 0; 0; 4; 61866; ; 0; 0; 0; 0; 0; 100001
<P 80; 78; 177; 355; 160; 106; 0; 0; 4; 61866; ; 1; 0; 0
<T 81; 78; 177; 355; 160; 16; 0; 0; 3; 95; ; 16711935; 16711935; 2; 1; 1; 100000
<B 82; 1; 344; 352; 166; 112; 1; 62823; 0; 0;
<T 83; 82; 347; 365; 160; 96; 0; 0; 4; 62823; ; 0; 0; 0; 0; 0; 100001
<P 84; 82; 347; 355; 160; 106; 0; 0; 4; 62823; ; 1; 0; 0
<T 85; 82; 347; 355; 160; 16; 0; 0; 3; 104; ; 16711935; 16711935; 2; 1; 1; 100000
<B 86; 1; 514; 352; 166; 112; 1; 62405; 0; 0;
<T 87; 86; 517; 365; 160; 96; 0; 0; 4; 62405; ; 0; 0; 0; 0; 0; 100001
<P 88; 86; 517; 355; 160; 106; 0; 0; 4; 62405; ; 1; 0; 0
<T 89; 86; 517; 355; 160; 16; 0; 0; 3; 116; ; 16711935; 16711935; 2; 1; 1; 100000
<B 90; 1; 684; 352; 166; 112; 1; 61988; 0; 0;
<T 91; 90; 687; 365; 160; 96; 0; 0; 4; 61988; ; 0; 0; 0; 0; 0; 100001
<P 92; 90; 687; 355; 160; 106; 0; 0; 4; 61988; ; 1; 0; 0
<T 93; 90; 687; 355; 160; 16; 0; 0; 3; 118; ; 16711935; 16711935; 2; 1; 1; 100000
<B 94; 1; 854; 352; 166; 112; 1; 54501; 0; 0;
<T 95; 94; 857; 365; 160; 96; 0; 0; 4; 54501; ; 0; 0; 0; 0; 0; 100001
<P 96; 94; 857; 355; 160; 106; 0; 0; 4; 54501; ; 1; 0; 0
<T 97; 94; 857; 355; 160; 16; 0; 0; 3; 142; ; 16711935; 16711935; 2; 1; 1; 100000
<B 98; 1; 4; 468; 166; 112; 1; 96034; 0; 0;
<T 99; 98; 7; 481; 160; 96; 0; 0; 4; 96034; ; 0; 0; 0; 0; 0; 100001
<P 100; 98; 7; 471; 160; 106; 0; 0; 4; 96034; ; 1; 0; 0
<T 101; 98; 7; 471; 160; 16; 0; 0; 3; 143; ; 16711935; 16711935; 2; 1; 1; 100000
<B 102; 1; 174; 468; 166; 112; 1; 50155; 0; 0;
<T 103; 102; 177; 481; 160; 96; 0; 0; 4; 50155; ; 0; 0; 0; 0; 0; 100001
<P 104; 102; 177; 471; 160; 106; 0; 0; 4; 50155; ; 1; 0; 0
<T 105; 102; 177; 471; 160; 16; 0; 0; 3; 150; ; 16711935; 16711935; 2; 1; 1; 100000
<B 106; 1; 344; 468; 166; 112; 1; 8618; 0; 0;
<T 107; 106; 347; 481; 160; 96; 0; 0; 4; 8618; ; 0; 0; 0; 0; 0; 100001
<P 108; 106; 347; 471; 160; 106; 0; 0; 4; 8618; ; 1; 0; 0
<T 109; 106; 347; 471; 160; 16; 0; 0; 3; 152; ; 16711935; 16711935; 2; 1; 1; 100000
<B 110; 1; 514; 468; 166; 112; 1; 48002; 0; 0;
<T 111; 110; 517; 481; 160; 96; 0; 0; 4; 48002; ; 0; 0; 0; 0; 0; 100001
<P 112; 110; 517; 471; 160; 106; 0; 0; 4; 48002; ; 1; 0; 0
<T 113; 110; 517; 471; 160; 16; 0; 0; 3; 155; ; 16711935; 16711935; 2; 1; 1; 100000
<B 114; 1; 684; 468; 166; 112; 1; 63456; 0; 0;
<T 115; 114; 687; 481; 160; 96; 0; 0; 4; 63456; ; 0; 0; 0; 0; 0; 100001
<P 116; 114; 687; 471; 160; 106; 0; 0; 4; 63456; ; 1; 0; 0
<T 117; 114; 687; 471; 160; 16; 0; 0; 3; 159; ; 16711935; 16711935; 2; 1; 1; 100000
<B 118; 1; 854; 468; 166; 112; 2; 119; 2; 0;
<W 119; 0; 0; 0; 1024; 584; 0; 0; 0; 0;
<B 120; 119; 4; 4; 166; 112; 1; 2125; 0; 0;
<T 121; 120; 7; 17; 160; 96; 0; 0; 4; 2125; ; 0; 0; 0; 0; 0; 100001
<P 122; 120; 7; 7; 160; 106; 0; 0; 4; 2125; ; 1; 0; 0
<T 123; 120; 7; 7; 160; 16; 0; 0; 3; 161; ; 16711935; 16711935; 2; 1; 1; 100000
<B 124; 119; 174; 4; 166; 112; 1; 51257; 0; 0;
<T 125; 124; 177; 17; 160; 96; 0; 0; 4; 51257; ; 0; 0; 0; 0; 0; 100001
<P 126; 124; 177; 7; 160; 106; 0; 0; 4; 51257; ; 1; 0; 0
<T 127; 124; 177; 7; 160; 16; 0; 0; 3; 167; ; 16711935; 16711935; 2; 1; 1; 100000
<B 128; 119; 344; 4; 166; 112; 1; 3596; 0; 0;
<T 129; 128; 347; 17; 160; 96; 0; 0; 4; 3596; ; 0; 0; 0; 0; 0; 100001
<P 130; 128; 347; 7; 160; 106; 0; 0; 4; 3596; ; 1; 0; 0
<T 131; 128; 347; 7; 160; 16; 0; 0; 3; 172; ; 16711935; 16711935; 2; 1; 1; 100000
<B 132; 119; 514; 4; 166; 112; 1; 26956; 0; 0;
<T 133; 132; 517; 17; 160; 96; 0; 0; 4; 26956; ; 0; 0; 0; 0; 0; 100001
<P 134; 132; 517; 7; 160; 106; 0; 0; 4; 26956; ; 1; 0; 0
<T 135; 132; 517; 7; 160; 16; 0; 0; 3; 175; ; 16711935; 16711935; 2; 1; 1; 100000
<B 136; 119; 684; 4; 166; 112; 1; 63377; 0; 0;
<T 137; 136; 687; 17; 160; 96; 0; 0; 4; 63377; ; 0; 0; 0; 0; 0; 100001
<P 138; 136; 687; 7; 160; 106; 0; 0; 4; 63377; ; 1; 0; 0
<T 139; 136; 687; 7; 160; 16; 0; 0; 3; 176; ; 16711935; 16711935; 2; 1; 1; 100000
<B 140; 119; 854; 4; 166; 112; 1; 74084; 0; 0;
<T 141; 140; 857; 17; 160; 96; 0; 0; 4; 74084; ; 0; 0; 0; 0; 0; 100001
<P 142; 140; 857; 7; 160; 106; 0; 0; 4; 74084; ; 1; 0; 0
<T 143; 140; 857; 7; 160; 16; 0; 0; 3; 179; ; 16711935; 16711935; 2; 1; 1; 100000
<B 144; 119; 4; 120; 166; 112; 1; 36656; 0; 0;
<T 145; 144; 7; 133; 160; 96; 0; 0; 4; 36656; ; 0; 0; 0; 0; 0; 100001
<P 146; 144; 7; 123; 160; 106; 0; 0; 4; 36656; ; 1; 0; 0
<T 147; 144; 7; 123; 160; 16; 0; 0; 3; 185; ; 16711935; 16711935; 2; 1; 1; 100000
<B 148; 119; 174; 120; 166; 112; 1; 49574; 0; 0;
<T 149; 148; 177; 133; 160; 96; 0; 0; 4; 49574; ; 0; 0; 0; 0; 0; 100001
<P 150; 148; 177; 123; 160; 106; 0; 0; 4; 49574; ; 1; 0; 0
<T 151; 148; 177; 123; 160; 16; 0; 0; 3; 199; ; 16711935; 16711935; 2; 1; 1; 100000
<B 152; 119; 344; 120; 166; 112; 1; 73621; 0; 0;
<T 153; 152; 347; 133; 160; 96; 0; 0; 4; 73621; ; 0; 0; 0; 0; 0; 100001
<P 154; 152; 347; 123; 160; 106; 0; 0; 4; 73621; ; 1; 0; 0
<T 155; 152; 347; 123; 160; 16; 0; 0; 3; 200; ; 16711935; 16711935; 2; 1; 1; 100000
<B 156; 119; 514; 120; 166; 112; 1; 77485; 0; 0;
<T 157; 156; 517; 133; 160; 96; 0; 0; 4; 77485; ; 0; 0; 0; 0; 0; 100001
<P 158; 156; 517; 123; 160; 106; 0; 0; 4; 77485; ; 1; 0; 0
<T 159; 156; 517; 123; 160; 16; 0; 0; 3; 201; ; 16711935; 16711935; 2; 1; 1; 100000
<B 160; 119; 684; 120; 166; 112; 1; 88098; 0; 0;
<T 161; 160; 687; 133; 160; 96; 0; 0; 4; 88098; ; 0; 0; 0; 0; 0; 100001
<P 162; 160; 687; 123; 160; 106; 0; 0; 4; 88098; ; 1; 0; 0
<T 163; 160; 687; 123; 160; 16; 0; 0; 3; 203; ; 16711935; 16711935; 2; 1; 1; 100000
<B 164; 119; 854; 120; 166; 112; 1; 63462; 0; 0;
<T 165; 164; 857; 133; 160; 96; 0; 0; 4; 63462; ; 0; 0; 0; 0; 0; 100001
<P 166; 164; 857; 123; 160; 106; 0; 0; 4; 63462; ; 1; 0; 0
<T 167; 164; 857; 123; 160; 16; 0; 0; 3; 217; ; 16711935; 16711935; 2; 1; 1; 100000
<B 168; 119; 4; 236; 166; 112; 1; 86962; 0; 0;
<T 169; 168; 7; 249; 160; 96; 0; 0; 4; 86962; ; 0; 0; 0; 0; 0; 100001
<P 170; 168; 7; 239; 160; 106; 0; 0; 4; 86962; ; 1; 0; 0
<T 171; 168; 7; 239; 160; 16; 0; 0; 3; 233; ; 16711935; 16711935; 2; 1; 1; 100000
<B 172; 119; 174; 236; 166; 112; 1; 87589; 0; 0;
<T 173; 172; 177; 249; 160; 96; 0; 0; 4; 87589; ; 0; 0; 0; 0; 0; 100001
<P 174; 172; 177; 239; 160; 106; 0; 0; 4; 87589; ; 1; 0; 0
<T 175; 172; 177; 239; 160; 16; 0; 0; 3; 234; ; 16711935; 16711935; 2; 1; 1; 100000
<B 176; 119; 344; 236; 166; 112; 1; 14926; 0; 0;
<T 177; 176; 347; 249; 160; 96; 0; 0; 4; 14926; ; 0; 0; 0; 0; 0; 100001
<P 178; 176; 347; 239; 160; 106; 0; 0; 4; 14926; ; 1; 0; 0
<T 179; 176; 347; 239; 160; 16; 0; 0; 3; 261; ; 16711935; 16711935; 2; 1; 1; 100000
<B 180; 119; 514; 236; 166; 112; 1; 23713; 0; 0;
<T 181; 180; 517; 249; 160; 96; 0; 0; 4; 23713; ; 0; 0; 0; 0; 0; 100001
<P 182; 180; 517; 239; 160; 106; 0; 0; 4; 23713; ; 1; 0; 0
<T 183; 180; 517; 239; 160; 16; 0; 0; 3; 269; ; 16711935; 16711935; 2; 1; 1; 100000
<B 184; 119; 684; 236; 166; 112; 1; 74457; 0; 0;
<T 185; 184; 687; 249; 160; 96; 0; 0; 4; 74457; ; 0; 0; 0; 0; 0; 100001
<P 186; 184; 687; 239; 160; 106; 0; 0; 4; 74457; ; 1; 0; 0
<T 187; 184; 687; 239; 160; 16; 0; 0; 3; 284; ; 16711935; 16711935; 2; 1; 1; 100000
<B 188; 119; 4; 468; 166; 112; 2; 1; 2; 1;
""";

    /// <summary>Настоящий файл целиком: с переводом строки в конце (в raw-строке его нет).</summary>
    private static readonly string Original = Reference + "\n";

    private static readonly Regex TileNumber = new(@"^(<T \d+; \d+; \d+; \d+; 160; 16; 0; 0; 3; )\d+(; ; )", RegexOptions.Multiline);

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.None);

    private static string MaskNumbers(string text) => TileNumber.Replace(text.Replace("\r\n", "\n"), "${1}N${2}");

    private ShtrikhPcSettings Settings(Action<ShtrikhPcSettings>? tweak = null)
    {
        var s = new ShtrikhPcSettings { Folder = Path.Combine(_root, "Shared"), QuickKeys = false };
        Directory.CreateDirectory(s.Folder);
        tweak?.Invoke(s);
        return s.Normalize();
    }

    private static long[] Codes(int n) => Enumerable.Range(1, n).Select(i => 1000L + i).ToArray();

    // ---------------------------------------------------------------- эталон

    [Fact]
    public void Build_ReproducesTheOriginalOwnersScenario_LineByLine()
    {
        var actual = ShtrikhPcScenarioWriter.Build(ReferenceCodes);

        actual.Split('\n').Length.Should().Be(Original.Split('\n').Length);
        Lines(MaskNumbers(actual)).Should().Equal(Lines(MaskNumbers(Original)), "всё, кроме номеров плиток, совпадает с настоящим Semya.ssc");
        // номера плиток у кассы — подряд с 1
        TileNumbers(actual).Should().Equal(Enumerable.Range(1, ReferenceCodes.Length));
    }

    [Fact]
    public void Reference_IsWhatWeThinkItIs()
    {
        ReferenceCodes.Should().HaveCount(46);
        Original.Split('\n').Length.Should().Be(191, "190 строк и перевод строки в конце");
        Reference.Should().NotContain("\r");
    }

    private static List<int> TileNumbers(string text) =>
        Regex.Matches(text, @"^<T \d+; \d+; \d+; \d+; 160; 16; 0; 0; 3; (\d+); ; ", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToList();

    // ---------------------------------------------------------------- число экранов, id, переходы

    [Theory]
    [InlineData(1, 1)]
    [InlineData(29, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(46, 2)]
    [InlineData(58, 2)]
    [InlineData(59, 3)]
    [InlineData(60, 3)]
    public void ScreenCount(int products, int screens)
    {
        ShtrikhPcScenarioWriter.ScreenSizes(products).Count.Should().Be(screens);
        ShtrikhPcScenarioWriter.ScreenSizes(products).Sum().Should().Be(products);
        Regex.Matches(ShtrikhPcScenarioWriter.Build(Codes(products)), @"^<W ", RegexOptions.Multiline).Count.Should().Be(screens);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(60)]
    public void Build_Structure_IdsAreUniqueAndGrowing_ButtonsAndNumbersAreRight(int products)
    {
        var text = ShtrikhPcScenarioWriter.Build(Codes(products));
        var lines = text.TrimEnd('\n').Split('\n');

        lines[0].Should().Be("<F 100000; 19; 2; Verdana");
        lines[1].Should().Be("<F 100001; 12; 2; Verdana");
        text.Should().EndWith("\n");
        text.Should().NotContain("\r");
        text.All(c => c < 128).Should().BeTrue("ASCII, как в настоящем файле");

        var ids = lines.Skip(2).Select(l => int.Parse(Regex.Match(l, @"^<[A-Z] (\d+);").Groups[1].Value)).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeInAscendingOrder();
        ids.First().Should().Be(1);
        ids.Zip(ids.Skip(1), (a, b) => b - a).Should().OnlyContain(d => d == 1, "номера подряд без пропусков");

        var screens = ShtrikhPcScenarioWriter.ScreenSizes(products).Count;
        var productButtons = lines.Where(l => Regex.IsMatch(l, @"^<B \d+; \d+; \d+; \d+; 166; 112; 1; ")).ToList();
        productButtons.Should().HaveCount(products);
        productButtons.Select(l => Regex.Match(l, @"; 1; (\d+); 0; 0;$").Groups[1].Value).Should().Equal(Codes(products).Select(c => c.ToString()), "порядок как в списке");
        TileNumbers(text).Should().Equal(Enumerable.Range(1, products));

        var forward = lines.Where(l => Regex.IsMatch(l, @"^<B \d+; \d+; 854; 468; 166; 112; 2; \d+; 2; 0;$")).ToList();
        var back = lines.Where(l => Regex.IsMatch(l, @"^<B \d+; \d+; \d+; \d+; 166; 112; 2; \d+; 2; 1;$")).ToList();
        forward.Should().HaveCount(screens - 1, "«Вперёд» — в последнем слоте (854, 468) каждого экрана, кроме последнего");
        back.Should().HaveCount(screens - 1, "«Назад» — на каждом экране, кроме первого");
        if (screens == 1)
            lines[^1].Should().StartWith("<T ", "одному экрану переходы не нужны");

        // «Вперёд» ведёт на экран, номер которого идёт сразу за кнопкой
        foreach (var f in forward)
        {
            var m = Regex.Match(f, @"^<B (\d+); \d+; 854; 468; 166; 112; 2; (\d+); 2; 0;$");
            lines.Should().Contain(l => l.StartsWith($"<W {int.Parse(m.Groups[2].Value)}; ", StringComparison.Ordinal));
            int.Parse(m.Groups[2].Value).Should().Be(int.Parse(m.Groups[1].Value) + 1);
        }
    }

    [Fact]
    public void Build_30Products_FillOneScreenCompletely_WithNoNavigationButtons()
    {
        var lines = ShtrikhPcScenarioWriter.Build(Codes(30)).TrimEnd('\n').Split('\n');

        lines.Count(l => l.StartsWith("<W ", StringComparison.Ordinal)).Should().Be(1);
        lines.Should().Contain("<B 118; 1; 854; 468; 166; 112; 1; 1030; 0; 0;", "тридцатый товар стоит в последнем слоте");
        lines.Count(l => Regex.IsMatch(l, @"; 2; \d+; 2; [01];$")).Should().Be(0);
    }

    [Fact]
    public void Build_31Products_FirstScreenHas29AndForwardButton_LastHasBackInFirstSlotOfBottomRow()
    {
        var lines = ShtrikhPcScenarioWriter.Build(Codes(31)).TrimEnd('\n').Split('\n');

        lines.Should().Contain("<B 118; 1; 854; 468; 166; 112; 2; 119; 2; 0;");
        lines.Should().Contain("<W 119; 0; 0; 0; 1024; 584; 0; 0; 0; 0;");
        lines.Should().Contain("<B 120; 119; 4; 4; 166; 112; 1; 1030; 0; 0;");
        lines[^1].Should().Be("<B 128; 119; 4; 468; 166; 112; 2; 1; 2; 1;", "как в настоящем сценарии: назад на экран 1 — в слоте (4, 468)");
    }

    [Fact]
    public void Build_60Products_MiddleScreenHasBackAndForward()
    {
        var lines = ShtrikhPcScenarioWriter.Build(Codes(60)).TrimEnd('\n').Split('\n');

        lines.Count(l => l.StartsWith("<W ", StringComparison.Ordinal)).Should().Be(3);
        var middle = lines.Where(l => Regex.IsMatch(l, @"^<B \d+; 119; 684; 468; 166; 112; 2; 1; 2; 1;$")).ToList();
        middle.Should().HaveCount(1, "на среднем экране «Назад» — в слоте (684, 468), «Вперёд» — в (854, 468)");
        lines.Count(l => Regex.IsMatch(l, @"^<B \d+; 119; 854; 468; 166; 112; 2; \d+; 2; 0;$")).Should().Be(1);
    }

    [Fact]
    public void Build_Empty_HasOnlyFonts()
    {
        ShtrikhPcScenarioWriter.Build(Array.Empty<long>()).Should().Be("<F 100000; 19; 2; Verdana\n<F 100001; 12; 2; Verdana\n");
        ShtrikhPcScenarioWriter.ScreenSizes(0).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- запись

    [Fact]
    public async Task Write_CreatesFile_InSelfScenarioFolder_Atomically()
    {
        var settings = Settings();

        var result = await ShtrikhPcScenarioWriter.WriteAsync(Codes(5), settings);

        result.Status.Should().Be(ShtrikhPcScenarioStatus.Written);
        result.Products.Should().Be(5);
        result.Screens.Should().Be(1);
        var path = Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc");
        result.Path.Should().Be(path);
        File.ReadAllText(path).Should().Be(ShtrikhPcScenarioWriter.Build(Codes(5)));
        File.Exists(path + ".tmp").Should().BeFalse();
        result.BackupPath.Should().BeNull("прежнего файла не было");
    }

    [Fact]
    public async Task Write_FirstReplacement_KeepsBackup_OnlyOnce()
    {
        var settings = Settings();
        var dir = Path.Combine(settings.Folder, "SelfScenario");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Semya.ssc");
        File.WriteAllText(path, Reference.Replace("\r\n", "\n"));

        var first = await ShtrikhPcScenarioWriter.WriteAsync(Codes(5), settings, new DateTime(2026, 11, 6, 21, 35, 7));

        first.Status.Should().Be(ShtrikhPcScenarioStatus.Written);
        first.BackupPath.Should().Be(path + ".bak-20261106-213507");
        File.ReadAllText(first.BackupPath!).Should().Be(Reference.Replace("\r\n", "\n"), "резервная копия — прежний сценарий владельца");

        var second = await ShtrikhPcScenarioWriter.WriteAsync(Codes(7), settings, new DateTime(2026, 11, 7, 10, 0, 0));
        second.Status.Should().Be(ShtrikhPcScenarioStatus.Written);
        second.BackupPath.Should().BeNull("резервная копия — только при первой замене, дальше не плодим");
        Directory.GetFiles(dir, "Semya.ssc.bak-*").Should().ContainSingle();
    }

    [Fact]
    public async Task Write_SameSetTwice_IsIdempotent_FileNotRewritten()
    {
        var settings = Settings();
        await ShtrikhPcScenarioWriter.WriteAsync(Codes(40), settings);
        var path = Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc");
        var before = File.ReadAllBytes(path);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var again = await ShtrikhPcScenarioWriter.WriteAsync(Codes(40), settings);

        again.Status.Should().Be(ShtrikhPcScenarioStatus.Unchanged);
        File.ReadAllBytes(path).Should().Equal(before);
        File.GetLastWriteTimeUtc(path).Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle("ни копий, ни временных файлов");
    }

    [Fact]
    public async Task Write_NoProducts_LeavesExistingScenarioUntouched()
    {
        var settings = Settings();
        var dir = Path.Combine(settings.Folder, "SelfScenario");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Semya.ssc");
        File.WriteAllText(path, "прежний сценарий");

        var result = await ShtrikhPcScenarioWriter.WriteAsync(Array.Empty<long>(), settings);

        result.Status.Should().Be(ShtrikhPcScenarioStatus.Skipped);
        File.ReadAllText(path).Should().Be("прежний сценарий");
        Directory.GetFiles(dir).Should().ContainSingle();
    }

    [Fact]
    public async Task Write_RespectsMaxProducts_AndCustomFileName()
    {
        var settings = Settings(s => { s.ScenarioMaxProducts = 10; s.ScenarioFile = "NurCRM.ssc"; });

        var result = await ShtrikhPcScenarioWriter.WriteAsync(Codes(50), settings);

        result.Products.Should().Be(10);
        File.Exists(Path.Combine(settings.Folder, "SelfScenario", "NurCRM.ssc")).Should().BeTrue();
        File.Exists(Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Semya.ssc", "Semya.ssc")]
    [InlineData("NurCRM.ssc", "NurCRM.ssc")]
    [InlineData("", "Semya.ssc")]
    [InlineData("..\\x.ssc", "Semya.ssc")]
    [InlineData("a/b.ssc", "Semya.ssc")]
    [InlineData("list.txt", "Semya.ssc")]
    public void SafeFileName(string input, string expected) => ShtrikhPcScenarioWriter.SafeFileName(input).Should().Be(expected);

    // ---------------------------------------------------------------- вместе с файлом импорта

    private static ShtrikhPcItem Item(long code, string name = "Яблоки") => new(code, name, 100m, false);

    [Fact]
    public async Task ImportWrite_AlsoRebuildsTheMainScreenScenario_WithTheUploadedCodes()
    {
        var settings = Settings();

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5), Item(6), Item(7) }, settings);

        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        result.Scenario.Should().NotBeNull();
        result.Scenario!.Status.Should().Be(ShtrikhPcScenarioStatus.Written);
        result.Scenario.Products.Should().Be(3);
        var text = File.ReadAllText(Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc"));
        text.Should().Contain("; 1; 5; 0; 0;").And.Contain("; 1; 6; 0; 0;").And.Contain("; 1; 7; 0; 0;");
    }

    [Fact]
    public async Task ImportWrite_SwitchedOff_OrNothingWritten_OrPending_DoesNotTouchTheScenario()
    {
        var off = Settings(s => s.UpdateScenario = false);
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, off);
        Directory.Exists(Path.Combine(off.Folder, "SelfScenario")).Should().BeFalse("галочка «Обновлять главный экран» выключена");

        var on = Settings();
        var nothing = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(0) }, on);
        nothing.Status.Should().Be(ShtrikhPcWriteStatus.NothingToWrite);
        Directory.Exists(Path.Combine(on.Folder, "SelfScenario")).Should().BeFalse("товаров 0 — сценарий не трогаем");
    }

    [Fact]
    public async Task ImportWrite_PendingUpload_NotConfirmed_LeavesScenarioAlone()
    {
        var settings = Settings(s => s.UpdateScenario = false);
        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(5) }, settings); // файл не принят весами
        settings.UpdateScenario = true;

        var second = await ShtrikhPcImportWriter.WriteAsync(new[] { Item(6) }, settings);

        second.Status.Should().Be(ShtrikhPcWriteStatus.PendingNotAccepted);
        Directory.Exists(Path.Combine(settings.Folder, "SelfScenario")).Should().BeFalse();
    }

    [Fact]
    public async Task ImportWrite_OnlyWithPicture_FiltersTheScreen()
    {
        var settings = Settings(s => s.ScenarioOnlyWithPicture = true);
        var withPicture = Item(5) with { PictureResource = 100005, PictureFile = "Pictures/5.bmp" };

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { withPicture, Item(6) }, settings);

        result.Scenario!.Products.Should().Be(1);
        File.ReadAllText(Path.Combine(settings.Folder, "SelfScenario", "Semya.ssc")).Should().Contain("; 1; 5; 0; 0;").And.NotContain("; 1; 6; 0; 0;");
    }

    [Fact]
    public void ScenarioSettings_Defaults_AndNormalize()
    {
        var s = new ShtrikhPcSettings().Normalize();

        s.UpdateScenario.Should().BeTrue("главный экран обновляется по умолчанию");
        s.ScenarioFile.Should().Be("Semya.ssc");
        s.ScenarioFolder.Should().Be("SelfScenario");
        s.ScenarioOnlyWithPicture.Should().BeFalse();
        s.ScenarioMaxProducts.Should().Be(300);
        s.QuickKeys.Should().BeFalse("«Быстрый доступ» (2001…) — не главный экран, по умолчанию не пишется");
        new ShtrikhPcSettings { ScenarioFile = "..\\evil.ssc", ScenarioFolder = "a/b" }.Normalize().ScenarioFile.Should().Be("Semya.ssc");
        new ShtrikhPcSettings { ScenarioFolder = "a/b" }.Normalize().ScenarioFolder.Should().Be("SelfScenario");
    }
}
