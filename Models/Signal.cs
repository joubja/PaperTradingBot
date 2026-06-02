using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PaperTradingBot.Models
{

    public enum SignalType
    {
        Hold,
        Buy,
        Sell
    }

    public class Signal
    {
        public SignalType Type { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

}
