using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PaperTradingBot.Models
{

    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal AverageEntryPrice { get; set; }

        public bool HasPosition => Quantity > 0m;
    }

}
