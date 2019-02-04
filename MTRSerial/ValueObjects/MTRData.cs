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

    public class MTRResponse
    {
        public int EmitCardNumber { get; set; }
        public int EmitCardProdWeek { get; set; }
        public int EmitCardProdYear { get; set; }

        public List<MTRResponseCheckPoint> CheckPoints { get; set; }

        public string CompetitorName { get; set; }
        public string Display1 { get; set; }
        public string Display2 { get; set; }
        public string Display3 { get; set; }
        public int NotInUse1 { get; set; }
        public int NotInUse2 { get; set; }
    }
}
