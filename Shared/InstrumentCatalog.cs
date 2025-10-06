using System.Collections.Generic;

namespace RsiWatcherGUI.Shared
{
    public static class InstrumentCatalog
    {
        // Display (Russian) and Tag (root) pairs
        public static readonly (string Display, string Tag)[] Items = new[]
        {
            ("Доллар–рубль (Si)", "Si"),
            ("Нефть Brent (BR)", "BR"),
            ("Индекс РТС (RI)", "RI"),
            ("Сбербанк (SBRF)", "SBRF"),
            ("Газпром (GAZR)", "GAZR"),
        };
    }
}
