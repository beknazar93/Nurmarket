using System.Text;

namespace NurMarketKassa.Tests.Import;

/// <summary>Сборка минимального .xls: контейнер OLE (одна FAT-секция, поток Workbook не меньше 4096 байт — без мини-потока) и BIFF8 (BOF, CODEPAGE, BOUNDSHEET, LABEL, NUMBER).</summary>
internal static class MiniXls
{
    public static byte[] Build(string sheetName, object[][] rows) => Build((sheetName, false, rows));

    public static byte[] Build(params (string Name, bool Hidden, object[][] Rows)[] sheets)
    {
        // 1) тела листов
        var bodies = sheets.Select(s => SheetBody(s.Rows)).ToList();
        // 2) глобальная часть: размер известен заранее — BOF(20) + CODEPAGE(6) + BOUNDSHEET'ы + EOF(4)
        var boundSizes = sheets.Select(s => 4 + 4 + 1 + 1 + 1 + 1 + s.Name.Length * 2).ToList();
        var globalsLen = 20 + 6 + boundSizes.Sum() + 4;
        var wb = new MemoryStream();
        var w = new BinaryWriter(wb);
        Record(w, 0x0809, b => { b.Write((ushort)0x0600); b.Write((ushort)0x0005); b.Write((ushort)0x0DBB); b.Write((ushort)0x07CC); b.Write(0u); b.Write(6u); });
        Record(w, 0x0042, b => b.Write((ushort)1251));
        var offset = globalsLen;
        for (var i = 0; i < sheets.Length; i++)
        {
            var pos = offset;
            var s = sheets[i];
            Record(w, 0x0085, b =>
            {
                b.Write((uint)pos);
                b.Write((byte)(s.Hidden ? 1 : 0));
                b.Write((byte)0);
                b.Write((byte)s.Name.Length);
                b.Write((byte)1); // UTF-16
                b.Write(Encoding.Unicode.GetBytes(s.Name));
            });
            offset += bodies[i].Length;
        }
        Record(w, 0x000A, _ => { });
        foreach (var body in bodies)
            w.Write(body);
        var stream = wb.ToArray();
        if (stream.Length < 4096)
            Array.Resize(ref stream, 4096);
        return Ole(stream);
    }

    private static byte[] SheetBody(object[][] rows)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        Record(w, 0x0809, b => { b.Write((ushort)0x0600); b.Write((ushort)0x0010); b.Write((ushort)0x0DBB); b.Write((ushort)0x07CC); b.Write(0u); b.Write(6u); });
        var cols = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
        Record(w, 0x0200, b => { b.Write(0u); b.Write((uint)rows.Length); b.Write((ushort)0); b.Write((ushort)cols); b.Write((ushort)0); });
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
            {
                var rr = r;
                var cc = c;
                switch (rows[r][c])
                {
                    case string { Length: > 0 } text:
                        Record(w, 0x0204, b => { b.Write((ushort)rr); b.Write((ushort)cc); b.Write((ushort)0); b.Write((ushort)text.Length); b.Write((byte)1); b.Write(Encoding.Unicode.GetBytes(text)); });
                        break;
                    case double d:
                        Record(w, 0x0203, b => { b.Write((ushort)rr); b.Write((ushort)cc); b.Write((ushort)0); b.Write(d); });
                        break;
                }
            }
        Record(w, 0x000A, _ => { });
        return ms.ToArray();
    }

    private static void Record(BinaryWriter w, ushort type, Action<BinaryWriter> body)
    {
        var ms = new MemoryStream();
        body(new BinaryWriter(ms));
        w.Write(type);
        w.Write((ushort)ms.Length);
        w.Write(ms.ToArray());
    }

    private static byte[] Ole(byte[] workbook)
    {
        const uint End = 0xFFFFFFFE, Free = 0xFFFFFFFF, FatSect = 0xFFFFFFFD;
        var sectors = workbook.Length / 512; // кратно 512 (4096)
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        // заголовок (512 байт)
        w.Write(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        w.Write(new byte[16]);
        w.Write((ushort)0x003E); w.Write((ushort)3); w.Write((ushort)0xFFFE); w.Write((ushort)9); w.Write((ushort)6);
        w.Write(new byte[6]);
        w.Write(0u);          // число секций каталога (для версии 3 — 0)
        w.Write(1u);          // число секций FAT
        w.Write(1u);          // первая секция каталога
        w.Write(0u);          // транзакции
        w.Write(4096u);       // порог мини-потока
        w.Write(End); w.Write(0u); // мини-FAT нет
        w.Write(End); w.Write(0u); // DIFAT нет
        w.Write(0u);          // DIFAT[0] — секция 0 содержит FAT
        for (var i = 1; i < 109; i++)
            w.Write(Free);
        // секция 0: FAT
        var fat = new uint[128];
        Array.Fill(fat, Free);
        fat[0] = FatSect;
        fat[1] = End;
        for (var i = 0; i < sectors; i++)
            fat[2 + i] = i == sectors - 1 ? End : (uint)(3 + i);
        foreach (var f in fat)
            w.Write(f);
        // секция 1: каталог (4 записи по 128 байт)
        Entry(w, "Root Entry", 5, Free, Free, 1, End, 0);
        Entry(w, "Workbook", 2, Free, Free, Free, 2, (uint)workbook.Length);
        Entry(w, "", 0, Free, Free, Free, 0, 0);
        Entry(w, "", 0, Free, Free, Free, 0, 0);
        // секции 2..: поток Workbook
        w.Write(workbook);
        return file.ToArray();
    }

    private static void Entry(BinaryWriter w, string name, byte type, uint left, uint right, uint child, uint start, uint size)
    {
        var nameBytes = new byte[64];
        if (name.Length > 0)
            Encoding.Unicode.GetBytes(name).CopyTo(nameBytes, 0);
        w.Write(nameBytes);
        w.Write((ushort)(name.Length == 0 ? 0 : (name.Length + 1) * 2));
        w.Write(type);
        w.Write((byte)1); // чёрный
        w.Write(left); w.Write(right); w.Write(child);
        w.Write(new byte[16]); // CLSID
        w.Write(0u);           // флаги состояния
        w.Write(new byte[16]); // время создания и изменения
        w.Write(start);
        w.Write((ulong)size);
    }
}
