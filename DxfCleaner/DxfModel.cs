using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DxfCleaner
{
    /// <summary>
    /// Один "код 0"-блок ASCII DXF (SECTION/ENDSEC/EOF/TABLE/LAYER/3DFACE/TEXT/… — в DXF ЛЮБОЙ
    /// топ-уровневый элемент, включая служебные маркеры секций, устроен одинаково: код 0 = тип,
    /// дальше пары код/значение до следующего кода 0). Список Codes хранит пары В ИСХОДНОМ
    /// ПОРЯДКЕ — нужен и для точной пересборки файла, и чтобы не гадать про порядок кодов.
    /// Аналог DxfEntity из RebarZones/DxfArmoringImport.cs, но без зависимости от Revit API —
    /// напрямую переиспользовать тот класс нельзя (см. класс-док DxfCleanerCore).
    /// </summary>
    public class DxfEntity
    {
        public string Type;
        public readonly List<(int Code, string Value)> Codes = new List<(int Code, string Value)>();

        public string Layer
        {
            get { foreach (var c in Codes) if (c.Code == 8) return c.Value; return ""; }
        }

        public bool Has(int code)
        {
            foreach (var c in Codes) if (c.Code == code) return true;
            return false;
        }

        public double D(int code, double def = 0)
        {
            foreach (var c in Codes)
                if (c.Code == code && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
            return def;
        }

        public string S(int code, string def = null)
        {
            foreach (var c in Codes) if (c.Code == code) return c.Value;
            return def;
        }

        /// <summary>Меняет значение кода, если он есть; иначе дописывает пару в конец (для
        /// используемых здесь кодов TEXT — 10/20/11/21/40 — они в реальном экспорте всегда
        /// присутствуют, "дописать в конец" тут не более чем страховка).</summary>
        public void Set(int code, string value)
        {
            for (int i = 0; i < Codes.Count; i++)
                if (Codes[i].Code == code) { Codes[i] = (code, value); return; }
            Codes.Add((code, value));
        }
    }

    /// <summary>
    /// Чтение/запись DXF как плоского списка "код 0"-блоков — без понимания структуры секций
    /// (та восстанавливается тем же порядком блоков, что и в исходном файле). DxfCleanerCore сам
    /// отслеживает границы секции ENTITIES по блокам SECTION(2=ENTITIES)/ENDSEC внутри списка.
    /// </summary>
    public static class DxfDocument
    {
        public static List<DxfEntity> Load(string path)
        {
            var groups = new List<DxfEntity>();
            // FileShare.ReadWrite — читаем, даже если файл открыт другой программой.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                DxfEntity cur = null;
                string codeLine, valLine;
                while ((codeLine = sr.ReadLine()) != null)
                {
                    valLine = sr.ReadLine();
                    if (valLine == null) break;
                    if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                        continue;
                    string val = valLine.TrimEnd('\r');

                    if (code == 0)
                    {
                        if (cur != null) groups.Add(cur);
                        cur = new DxfEntity { Type = val };
                        continue;
                    }
                    cur?.Codes.Add((code, val));
                }
                if (cur != null) groups.Add(cur);
            }
            return groups;
        }

        public static void Save(string path, List<DxfEntity> groups)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.NewLine = "\r\n";
                foreach (var g in groups)
                {
                    sw.WriteLine("0");
                    sw.WriteLine(g.Type);
                    foreach (var (code, value) in g.Codes)
                    {
                        sw.WriteLine(code.ToString(CultureInfo.InvariantCulture));
                        sw.WriteLine(value);
                    }
                }
            }
        }
    }
}
