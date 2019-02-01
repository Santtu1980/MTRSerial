using System;
using System.Collections.Generic;
using System.Text;

namespace MTRSerial.ValueObjects
{
    public class MTRDataString
    {
        public string Preamble { get; set; }
        public string PackageSize { get; set; }
        public string PackageType { get; set; }
        public string MtrSerialNo { get; set; }
        public string TimeStamp { get; set; }
        public string Time_ms { get; set; }
        public string CardId { get; set; }
        public string PackageNo { get; set; }
        public string ProductWeek { get; set; }
        public string ProductYear { get; set; }
        public string ECardHeadSum { get; set; }
        public List<string[]> CheckPoints { get; set; }

    }

    public class MTRData
    {
        public int Preamble { get; set; }
        public int PackageSize { get; set; }
        public char PackageType { get; set; }
        public int MtrSerialNo { get; set; }
        public DateTime TimeStamp { get; set; }
        public long Time_ms { get; set; }
        public int CardId { get; set; }
        public int PackageNo { get; set; }
        public int ProductWeek { get; set; }
        public int ProductYear { get; set; }
        public int ECardHeadSum { get; set; }
        public List<MTRResponseCheckPoint> CheckPoints { get; set; }

    }
}
